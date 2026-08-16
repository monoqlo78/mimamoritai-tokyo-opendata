using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Reads the current outdoor picture for Tokyo from two public open data sources:
/// 暑さ指数 (WBGT) from 環境省 熱中症予防情報サイト, and observed temperature/humidity from
/// 気象庁 AMeDAS.
///
/// <para>
/// The two sources are fetched together but kept independent on purpose. WBGT is only
/// published from late April to late October, so from November the summer endpoint
/// simply 404s -- and November to March is precisely when an older person living alone
/// is most at risk, from ヒートショック and from a room that never gets warm. Reading the
/// year-round AMeDAS observation separately means the card and the cold rule keep
/// working through the winter instead of going blank for five months.
/// </para>
///
/// <para>
/// Every failure path returns <c>null</c>. Government sites go down and a household's
/// internet is not ours to rely on, neither of which may be allowed to stop the watch
/// service; the app just loses that rule and behaves as before.
/// </para>
/// </summary>
public sealed class TokyoWeatherAdvisoryProvider(
    HttpClient http,
    IOptions<OpenDataOptions> options,
    TimeProvider clock,
    ILogger<TokyoWeatherAdvisoryProvider> logger) : IWeatherAdvisoryProvider
{
    private readonly OpenDataOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HeatAdvisory? _cachedHeat;
    private ColdAdvisory? _cachedCold;
    private ColdForecast? _cachedForecast;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public async Task<HeatAdvisory?> GetHeatAsync(CancellationToken ct = default)
    {
        await RefreshIfStaleAsync(ct);
        return _cachedHeat;
    }

    public async Task<ColdAdvisory?> GetColdAsync(CancellationToken ct = default)
    {
        await RefreshIfStaleAsync(ct);
        return _cachedCold;
    }

    public async Task<ColdForecast?> GetTomorrowColdAsync(CancellationToken ct = default)
    {
        await RefreshIfStaleAsync(ct);

        // A forecast for a date that has since arrived is no longer a forecast.
        var tomorrow = HouseholdTime.LocalDate(clock.GetUtcNow()).AddDays(1);
        return _cachedForecast?.ForDateLocal == tomorrow ? _cachedForecast : null;
    }

    /// <summary>
    /// One fetch serves both advisories: they come from the same two files, and letting
    /// each side refresh on its own clock would have the card showing a temperature from
    /// one moment beside an index from another.
    /// </summary>
    private async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.CacheMinutes));

        if (_cachedAtUtc > DateTimeOffset.MinValue && clock.GetUtcNow() - _cachedAtUtc < ttl)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we queued.
            if (_cachedAtUtc > DateTimeOffset.MinValue && clock.GetUtcNow() - _cachedAtUtc < ttl)
            {
                return;
            }

            var (heat, cold, forecast) = await FetchAsync(ct);

            // A failed refresh must not throw away a figure we already have: half an
            // hour old and honest beats nothing at all on a hot afternoon.
            if (heat is null && cold is null && forecast is null)
            {
                return;
            }

            _cachedHeat = heat ?? _cachedHeat;
            _cachedCold = cold ?? _cachedCold;
            _cachedForecast = forecast ?? _cachedForecast;
            _cachedAtUtc = clock.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(HeatAdvisory? Heat, ColdAdvisory? Cold, ColdForecast? Forecast)> FetchAsync(
        CancellationToken ct)
    {
        // Read the year-round observation first, so that an out-of-season WBGT endpoint
        // costs us the heat rule and nothing else.
        var (temp, humidity, observedAtUtc) = await TryReadAmedasAsync(ct);

        var cold = temp is { } t && observedAtUtc is { } at
            ? new ColdAdvisory(
                t,
                ColdAdvisory.Classify(t),
                humidity,
                at,
                _options.AreaName,
                _options.AmedasAttribution)
            : null;

        return (await TryReadWbgtAsync(temp, humidity, ct), cold, await TryReadForecastAsync(ct));
    }

    private async Task<ColdForecast?> TryReadForecastAsync(CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync(_options.ForecastJsonUrl, ct);
            var tomorrow = HouseholdTime.LocalDate(clock.GetUtcNow()).AddDays(1);

            if (ParseForecastLow(json, _options.PointCode, tomorrow) is not { } low)
            {
                return null;
            }

            return new ColdForecast(
                tomorrow,
                low,
                ColdAdvisory.Classify(low),
                _options.AreaName,
                _options.AmedasAttribution);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "JMA forecast unavailable; tomorrow's cold notice stays off.");
            return null;
        }
    }

    private async Task<HeatAdvisory?> TryReadWbgtAsync(
        double? temp, double? humidity, CancellationToken ct)
    {
        try
        {
            var csv = await http.GetStringAsync(_options.WbgtCsvUrl, ct);
            var nowLocal = HouseholdTime.ToLocal(clock.GetUtcNow());

            if (ParseWbgt(csv, _options.PointCode, nowLocal, _options.ForecastToleranceHours) is not { } reading)
            {
                logger.LogInformation(
                    "WBGT open data had no usable column for point {Point}; heat rule stays off.",
                    _options.PointCode);
                return null;
            }

            return new HeatAdvisory(
                reading.Wbgt,
                HeatAdvisory.Classify(reading.Wbgt),
                temp,
                humidity,
                reading.AtLocal.ToUniversalTime(),
                _options.AreaName,
                _options.Attribution);
        }
        catch (Exception ex)
        {
            // Out of season the endpoint 404s, which is expected rather than exceptional.
            logger.LogInformation(ex, "WBGT open data unavailable; heat rule stays off.");
            return null;
        }
    }

    private async Task<(double? Temp, double? Humidity, DateTimeOffset? AtUtc)> TryReadAmedasAsync(
        CancellationToken ct)
    {
        try
        {
            var latest = (await http.GetStringAsync(_options.AmedasLatestTimeUrl, ct)).Trim();

            if (!DateTimeOffset.TryParse(
                    latest, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                return (null, null, null);
            }

            var url = string.Format(
                CultureInfo.InvariantCulture,
                _options.AmedasMapUrlFormat,
                stamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

            var json = await http.GetStringAsync(url, ct);
            var (temp, humidity) = ParseAmedas(json, _options.PointCode);

            return (temp, humidity, temp is null ? null : stamp.ToUniversalTime());
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "AMeDAS observation unavailable; cold rule stays off.");
            return (null, null, null);
        }
    }

    /// <summary>
    /// Picks the WBGT column closest to <paramref name="nowLocal"/> for one observation
    /// point.
    ///
    /// <para>
    /// The file is a three-hourly forecast whose header carries yyyyMMddHH stamps in JST
    /// and whose values are tenths of a degree. Hour 24 is the government's way of
    /// writing midnight at the end of that day, which .NET will not parse, so it is
    /// normalised to 00 on the following day.
    /// </para>
    /// </summary>
    internal static (double Wbgt, DateTimeOffset AtLocal)? ParseWbgt(
        string csv, string pointCode, DateTimeOffset nowLocal, int toleranceHours)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return null;
        }

        var header = lines[0].Trim('\r').Split(',');
        var row = lines
            .Skip(1)
            .Select(l => l.Trim('\r').Split(','))
            .FirstOrDefault(c => c.Length > 2 && c[0].Trim() == pointCode);

        if (row is null)
        {
            return null;
        }

        (double Wbgt, DateTimeOffset AtLocal)? best = null;
        var bestGap = TimeSpan.MaxValue;
        var tolerance = TimeSpan.FromHours(Math.Max(1, toleranceHours));

        for (var i = 2; i < Math.Min(header.Length, row.Length); i++)
        {
            if (ParseStamp(header[i].Trim(), nowLocal.Offset) is not { } at)
            {
                continue;
            }

            if (!int.TryParse(
                    row[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tenths))
            {
                continue;
            }

            var gap = at > nowLocal ? at - nowLocal : nowLocal - at;
            if (gap <= tolerance && gap < bestGap)
            {
                bestGap = gap;
                best = (tenths / 10.0, at);
            }
        }

        return best;
    }

    private static DateTimeOffset? ParseStamp(string stamp, TimeSpan offset)
    {
        if (stamp.Length != 10
            || !int.TryParse(stamp[..8], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(stamp[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                stamp[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        // "24" is midnight closing that day, not an hour of it.
        return hour >= 24
            ? new DateTimeOffset(day.AddDays(1), offset)
            : new DateTimeOffset(day.AddHours(hour), offset);
    }

    /// <summary>
    /// Pulls temperature and humidity for one point out of the AMeDAS nationwide map.
    /// Each field is <c>[value, qualityFlag]</c>; a non-zero flag means the instrument
    /// itself is unsure, so we drop the value rather than show a suspect number.
    /// </summary>
    internal static (double? Temp, double? Humidity) ParseAmedas(string json, string pointCode)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty(pointCode, out var point))
        {
            return (null, null);
        }

        return (ReadMeasured(point, "temp"), ReadMeasured(point, "humidity"));
    }

    private static double? ReadMeasured(JsonElement point, string name)
    {
        if (!point.TryGetProperty(name, out var pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() < 1)
        {
            return null;
        }

        var value = pair[0];
        if (value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (pair.GetArrayLength() >= 2
            && pair[1].ValueKind == JsonValueKind.Number
            && pair[1].GetInt32() != 0)
        {
            return null;
        }

        return value.GetDouble();
    }

    /// <summary>
    /// Pulls tomorrow's forecast minimum temperature for one point out of the 気象庁
    /// three-day forecast.
    ///
    /// <para>
    /// The temperature block pairs a list of timestamps with a flat list of values, two
    /// per day: the 00:00 entry is that day's low and the 09:00 entry its high. Rather
    /// than trust those positions -- the leading entries are trimmed as the day wears on,
    /// so an index that is the low in the morning is the high by the afternoon -- we take
    /// every value stamped with the target date and keep the smallest. A forecast low is
    /// by definition the smallest figure quoted for that day.
    /// </para>
    /// </summary>
    internal static double? ParseForecastLow(string json, string pointCode, DateOnly forDateLocal)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double? low = null;

        foreach (var publication in doc.RootElement.EnumerateArray())
        {
            if (!publication.TryGetProperty("timeSeries", out var series)
                || series.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in series.EnumerateArray())
            {
                if (!block.TryGetProperty("timeDefines", out var times)
                    || times.ValueKind != JsonValueKind.Array
                    || !block.TryGetProperty("areas", out var areas)
                    || areas.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var area in areas.EnumerateArray())
                {
                    if (!MatchesPoint(area, pointCode)
                        || !area.TryGetProperty("temps", out var temps)
                        || temps.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var count = Math.Min(times.GetArrayLength(), temps.GetArrayLength());

                    for (var i = 0; i < count; i++)
                    {
                        if (times[i].ValueKind != JsonValueKind.String
                            || !DateTimeOffset.TryParse(
                                times[i].GetString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var at)
                            || DateOnly.FromDateTime(at.DateTime) != forDateLocal)
                        {
                            continue;
                        }

                        if (temps[i].ValueKind != JsonValueKind.String
                            || !double.TryParse(
                                temps[i].GetString(),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var value))
                        {
                            continue;
                        }

                        low = low is null ? value : Math.Min(low.Value, value);
                    }
                }
            }
        }

        return low;
    }

    private static bool MatchesPoint(JsonElement area, string pointCode) =>
        area.TryGetProperty("area", out var meta)
        && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String
        && string.Equals(code.GetString(), pointCode, StringComparison.Ordinal);
}

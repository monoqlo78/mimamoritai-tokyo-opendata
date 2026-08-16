using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Reads the current heat picture for Tokyo from two public open data sources:
/// 暑さ指数 (WBGT) from 環境省 熱中症予防情報サイト, and observed temperature/humidity from
/// 気象庁 AMeDAS.
///
/// <para>
/// Every failure path returns <c>null</c>. The WBGT series is only published from late
/// April to late October, government sites go down, and a household's internet is not
/// ours to rely on -- none of which may be allowed to stop the watch service. When the
/// index is unavailable the app simply loses the heatstroke rule and behaves as before.
/// </para>
/// </summary>
public sealed class TokyoHeatAdvisoryProvider(
    HttpClient http,
    IOptions<OpenDataOptions> options,
    TimeProvider clock,
    ILogger<TokyoHeatAdvisoryProvider> logger) : IHeatAdvisoryProvider
{
    private readonly OpenDataOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HeatAdvisory? _cached;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public async Task<HeatAdvisory?> GetCurrentAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.CacheMinutes));

        if (_cached is not null && now - _cachedAtUtc < ttl)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we queued.
            if (_cached is not null && clock.GetUtcNow() - _cachedAtUtc < ttl)
            {
                return _cached;
            }

            var advisory = await FetchAsync(ct);

            // A failed refresh must not throw away a figure we already have: half an
            // hour old and honest beats nothing at all on a hot afternoon.
            if (advisory is null)
            {
                return _cached;
            }

            _cached = advisory;
            _cachedAtUtc = clock.GetUtcNow();
            return advisory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HeatAdvisory?> FetchAsync(CancellationToken ct)
    {
        double wbgt;
        DateTimeOffset observedAtUtc;

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

            wbgt = reading.Wbgt;
            observedAtUtc = reading.AtLocal.ToUniversalTime();
        }
        catch (Exception ex)
        {
            // Out of season the endpoint 404s, which is expected rather than exceptional.
            logger.LogInformation(ex, "WBGT open data unavailable; heat rule stays off.");
            return null;
        }

        // Temperature and humidity are a nice-to-have on the card, so their failure is
        // not allowed to cost us the WBGT figure that the rule actually runs on.
        var (temp, humidity) = await TryReadAmedasAsync(ct);

        return new HeatAdvisory(
            wbgt,
            HeatAdvisory.Classify(wbgt),
            temp,
            humidity,
            observedAtUtc,
            _options.AreaName,
            _options.Attribution);
    }

    private async Task<(double? Temp, double? Humidity)> TryReadAmedasAsync(CancellationToken ct)
    {
        try
        {
            var latest = (await http.GetStringAsync(_options.AmedasLatestTimeUrl, ct)).Trim();

            if (!DateTimeOffset.TryParse(
                    latest, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                return (null, null);
            }

            var url = string.Format(
                CultureInfo.InvariantCulture,
                _options.AmedasMapUrlFormat,
                stamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

            var json = await http.GetStringAsync(url, ct);
            return ParseAmedas(json, _options.PointCode);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "AMeDAS observation unavailable; showing WBGT only.");
            return (null, null);
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
}

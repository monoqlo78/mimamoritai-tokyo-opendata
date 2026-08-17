using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Reads currently active emergency information for the household's prefecture from
/// three 気象庁 open data endpoints, none of which requires a key.
///
/// <para>
/// Three sources rather than one because 気象庁 does not publish these together:
/// 特別警報 is a code inside the prefecture's warning JSON; 土砂災害警戒情報 and
/// 顕著な大雨に関する気象情報 (線状降水帯) are separate products that only appear in the
/// 防災情報XML feed; and earthquakes are their own list. They are fetched in parallel and
/// each one's failure is contained, so losing the feed does not cost us the quake list.
/// </para>
///
/// <para>
/// The nationwide sources are filtered to the household's prefecture before anything is
/// returned. This is the whole reason the class exists: every phone in Japan already gets
/// 緊急速報メール, so a service that forwards a 震度3 in 九州 to a family in 東京 teaches
/// them to mute it -- and the heatstroke alert goes silent with it.
/// </para>
/// </summary>
public sealed class JmaDisasterAdvisoryProvider(
    HttpClient http,
    IOptions<OpenDataOptions> options,
    TimeProvider clock,
    ILogger<JmaDisasterAdvisoryProvider> logger) : IDisasterAdvisoryProvider
{
    private readonly OpenDataOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<DisasterAdvisory> _cached = [];
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// 特別警報 codes from 気象庁's 警報・注意報種別. Only these are read out of the warning
    /// file; the ordinary 警報 and 注意報 codes that share it change through the day and
    /// are already on every weather app.
    /// </summary>
    private static readonly Dictionary<string, string> SpecialWarningCodes = new()
    {
        ["32"] = "暴風雪特別警報",
        ["33"] = "大雨特別警報",
        ["35"] = "暴風特別警報",
        ["36"] = "大雪特別警報",
        ["37"] = "波浪特別警報",
        ["38"] = "高潮特別警報"
    };

    /// <summary>気象庁 震度 notation in order, so "5-" can be compared against "6+".</summary>
    private static readonly string[] IntensityLadder =
        ["1", "2", "3", "4", "5-", "5+", "6-", "6+", "7"];

    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.PrefectureCode);

    public async Task<IReadOnlyList<DisasterAdvisory>> GetActiveAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        if (now - _cachedAtUtc < TimeSpan.FromMinutes(Math.Max(1, _options.DisasterCacheMinutes)))
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (now - _cachedAtUtc < TimeSpan.FromMinutes(Math.Max(1, _options.DisasterCacheMinutes)))
            {
                return _cached;
            }

            var special = ReadSpecialWarningsAsync(ct);
            var feed = ReadFeedAsync(ct);
            var quakes = ReadQuakesAsync(ct);
            await Task.WhenAll(special, feed, quakes);

            var cutoff = now.AddHours(-Math.Max(1, _options.DisasterActiveHours));

            _cached = special.Result
                .Concat(feed.Result)
                .Concat(quakes.Result)
                .Where(a => a.IssuedAtUtc >= cutoff)
                .OrderByDescending(a => a.IssuedAtUtc)
                .ToList();
            _cachedAtUtc = now;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>特別警報 out of the prefecture's 警報・注意報 file.</summary>
    private async Task<List<DisasterAdvisory>> ReadSpecialWarningsAsync(CancellationToken ct)
    {
        var found = new List<DisasterAdvisory>();
        try
        {
            var json = await http.GetStringAsync(_options.WarningJsonUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryReadJmaTime(root, "reportDatetime", out var issued))
            {
                issued = clock.GetUtcNow();
            }

            var office = root.TryGetProperty("publishingOffice", out var o)
                ? o.GetString() ?? _options.AreaName
                : _options.AreaName;

            if (!root.TryGetProperty("areaTypes", out var areaTypes) ||
                areaTypes.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            // The same 特別警報 is listed against every municipality it covers, so the
            // codes are collected into a set first: a family needs to be told "大雨特別
            // 警報が出ています" once, not once per ward.
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var areaType in areaTypes.EnumerateArray())
            {
                if (!areaType.TryGetProperty("areas", out var areas) ||
                    areas.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var area in areas.EnumerateArray())
                {
                    if (!area.TryGetProperty("warnings", out var warnings) ||
                        warnings.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var warning in warnings.EnumerateArray())
                    {
                        var code = warning.TryGetProperty("code", out var c) ? c.GetString() : null;
                        if (code is null || !SpecialWarningCodes.ContainsKey(code))
                        {
                            continue;
                        }

                        // 解除 means it has been lifted. The entry stays in the file
                        // afterwards, so skipping it is what stops a stood-down warning
                        // from being pushed as if it were live.
                        var status = warning.TryGetProperty("status", out var s) ? s.GetString() : null;
                        if (string.Equals(status, "解除", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        codes.Add(code);
                    }
                }
            }

            found.AddRange(codes.Select(code => new DisasterAdvisory(
                DisasterKind.SpecialWarning,
                SpecialWarningCodes[code],
                office,
                null,
                issued,
                _options.AmedasAttribution)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "気象庁 warning feed unavailable; skipping 特別警報 this cycle.");
        }

        return found;
    }

    /// <summary>土砂災害警戒情報 and 顕著な大雨に関する気象情報 out of the 防災情報XML feed.</summary>
    private async Task<List<DisasterAdvisory>> ReadFeedAsync(CancellationToken ct)
    {
        var found = new List<DisasterAdvisory>();
        try
        {
            var xml = await http.GetStringAsync(_options.DisasterFeedUrl, ct);
            var feed = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";

            // Every published document is named for its own area, and the last field of
            // that name is the office code -- 130000 for 東京. Matching on it is what
            // keeps a landslide warning in 広島 off a Tokyo family's phone.
            var areaSuffix = $"_{PrefectureAreaCode()}.xml";

            foreach (var entry in feed.Descendants(atom + "entry"))
            {
                var title = entry.Element(atom + "title")?.Value?.Trim();
                var kind = title switch
                {
                    "土砂災害警戒情報" => DisasterKind.Landslide,
                    "顕著な大雨に関する気象情報" => DisasterKind.HeavyRainBand,
                    _ => (DisasterKind?)null
                };

                if (kind is null)
                {
                    continue;
                }

                var id = entry.Element(atom + "id")?.Value ?? string.Empty;
                if (!id.EndsWith(areaSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var updated = entry.Element(atom + "updated")?.Value;
                if (!DateTimeOffset.TryParse(
                        updated, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var issued))
                {
                    continue;
                }

                found.Add(new DisasterAdvisory(
                    kind.Value,
                    title!,
                    entry.Element(atom + "author")?.Element(atom + "name")?.Value?.Trim()
                        ?? _options.AreaName,
                    entry.Element(atom + "content")?.Value?.Trim(),
                    issued,
                    _options.AmedasAttribution));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "気象庁 XML feed unavailable; skipping 土砂/線状降水帯 this cycle.");
        }

        return found;
    }

    /// <summary>Earthquakes that shook this prefecture at or above the configured 震度.</summary>
    private async Task<List<DisasterAdvisory>> ReadQuakesAsync(CancellationToken ct)
    {
        var found = new List<DisasterAdvisory>();
        try
        {
            var json = await http.GetStringAsync(_options.QuakeListUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            var floor = Rank(_options.MinimumQuakeIntensity);
            var pref = _options.PrefectureCode.Trim();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // The list carries several report types per event; the intensity break-
                // down is what we need, and 震度速報 alone would double-report the same
                // quake minutes before the fuller record arrives.
                if (!item.TryGetProperty("int", out var ints) || ints.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // The list already breaks each event down by prefecture, so how hard the
                // household's own area shook is available here -- there is no need to
                // fetch the per-event detail, and no excuse for reporting the epicentre's
                // 震度 as if it were theirs.
                string? localIntensity = null;
                foreach (var entry in ints.EnumerateArray())
                {
                    if (entry.TryGetProperty("code", out var c) &&
                        string.Equals(c.GetString(), pref, StringComparison.Ordinal))
                    {
                        localIntensity = entry.TryGetProperty("maxi", out var m) ? m.GetString() : null;
                        break;
                    }
                }

                if (localIntensity is null || Rank(localIntensity) < floor)
                {
                    continue;
                }

                var eid = item.TryGetProperty("eid", out var e) ? e.GetString() : null;
                if (eid is null || !seen.Add(eid))
                {
                    continue;
                }

                var rdt = item.TryGetProperty("rdt", out var r) ? r.GetString() : null;
                if (!DateTimeOffset.TryParse(
                        rdt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                        out var issued))
                {
                    continue;
                }

                var epicentre = item.TryGetProperty("anm", out var a) ? a.GetString() : null;

                found.Add(new DisasterAdvisory(
                    DisasterKind.Earthquake,
                    "地震",
                    string.IsNullOrWhiteSpace(epicentre) ? _options.AreaName : epicentre!,
                    $"震度{localIntensity}",
                    issued,
                    _options.AmedasAttribution));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "気象庁 quake list unavailable; skipping earthquakes this cycle.");
        }

        return found;
    }

    /// <summary>"13" becomes "130000", the form the warning and feed products are named for.</summary>
    private string PrefectureAreaCode()
    {
        var code = _options.PrefectureCode.Trim();
        return code.Length >= 6 ? code : code.PadRight(6, '0');
    }

    /// <summary>
    /// Position of a 震度 on 気象庁's scale, or -1 when it is not one. String comparison
    /// would put "5-" above "6+", and an app that mixes those up is worse than no app.
    /// </summary>
    internal static int Rank(string? intensity) =>
        string.IsNullOrWhiteSpace(intensity)
            ? -1
            : Array.IndexOf(IntensityLadder, intensity.Trim());

    private static bool TryReadJmaTime(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(name, out var raw) &&
               DateTimeOffset.TryParse(
                   raw.GetString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal, out value);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Reads 気象庁's published AMeDAS station table and answers "which station is this
/// house nearest to".
///
/// <para>
/// The table is the same file the 気象庁 site itself uses, listing every station with
/// its coordinates and which elements it measures. Stations that do not measure
/// temperature are dropped on load -- 世田谷, for instance, only reports rainfall, and
/// offering it would let a family pick a station that can never answer the question
/// the app is asking.
/// </para>
///
/// <para>
/// It is cached for a day because station tables change on the order of years, and a
/// settings screen must not put a request to a government site behind every keystroke.
/// </para>
/// </summary>
public sealed class AmedasStationCatalog(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenDataOptions> options,
    TimeProvider clock,
    ILogger<AmedasStationCatalog> logger) : IAmedasStationCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AmedasStation> _stations = [];
    private DateTimeOffset _loadedAtUtc = DateTimeOffset.MinValue;

    public async Task<AmedasStation?> FindNearestAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var stations = await LoadAsync(ct);

        return stations
            .OrderBy(s => s.DistanceKmTo(latitude, longitude))
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AmedasStation>> ListAsync(string codePrefix, CancellationToken ct = default)
    {
        var stations = await LoadAsync(ct);

        return [.. stations
            .Where(s => s.Code.StartsWith(codePrefix, StringComparison.Ordinal))
            .OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    public async Task<AmedasStation?> FindAsync(string code, CancellationToken ct = default)
    {
        var stations = await LoadAsync(ct);

        return stations.FirstOrDefault(s => s.Code == code);
    }

    private async Task<IReadOnlyList<AmedasStation>> LoadAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_stations.Count > 0 && now - _loadedAtUtc < TimeSpan.FromHours(24))
        {
            return _stations;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_stations.Count > 0 && now - _loadedAtUtc < TimeSpan.FromHours(24))
            {
                return _stations;
            }

            var client = httpClientFactory.CreateClient(nameof(AmedasStationCatalog));
            var json = await client.GetStringAsync(options.Value.AmedasStationTableUrl, ct);
            var parsed = Parse(json);

            if (parsed.Count > 0)
            {
                _stations = parsed;
                _loadedAtUtc = now;
            }

            return _stations;
        }
        catch (Exception ex)
        {
            // A settings screen that cannot reach 気象庁 should still open; it just
            // cannot offer to change the station. Keeping any previously loaded copy
            // means a transient failure does not empty the dropdown either.
            logger.LogInformation(ex, "AMeDAS station table unavailable; keeping {Count} cached stations.", _stations.Count);
            return _stations;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The table is an object keyed by station code. Coordinates arrive as
    /// [degrees, minutes] pairs, and <c>elems</c> is a positional string whose first
    /// character is 1 when the station measures temperature.
    /// </summary>
    internal static IReadOnlyList<AmedasStation> Parse(string json)
    {
        var stations = new List<AmedasStation>();

        // A truncated or empty response must read as "no stations", not as an exception:
        // this runs while a family is opening the settings screen, and the page has to
        // stay usable when 気象庁 hands back half a file.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return stations;
        }

        using (document)
        {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return stations;
        }

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!MeasuresTemperature(entry.Value))
            {
                continue;
            }

            if (ReadDegrees(entry.Value, "lat") is not { } latitude
                || ReadDegrees(entry.Value, "lon") is not { } longitude)
            {
                continue;
            }

            var name = entry.Value.TryGetProperty("kjName", out var kjName)
                ? kjName.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            stations.Add(new AmedasStation(entry.Name, name, latitude, longitude));
        }
        }

        return stations;
    }

    private static bool MeasuresTemperature(JsonElement station) =>
        station.TryGetProperty("elems", out var elems)
        && elems.ValueKind == JsonValueKind.String
        && elems.GetString() is { Length: > 0 } value
        && value[0] == '1';

    private static double? ReadDegrees(JsonElement station, string name)
    {
        if (!station.TryGetProperty(name, out var pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() < 2)
        {
            return null;
        }

        var degrees = pair[0];
        var minutes = pair[1];

        if (!degrees.TryGetDouble(out var d) || !minutes.TryGetDouble(out var m))
        {
            return null;
        }

        return d + (m / 60.0);
    }
}

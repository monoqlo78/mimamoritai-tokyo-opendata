using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Reads one AMeDAS station's own past observations from 気象庁.
///
/// <para>
/// The station files are published in three-hour blocks of ten-minute readings, so a
/// day costs eight requests. That is why this is only ever used to fill a gap once and
/// never on the request path.
/// </para>
/// </summary>
public sealed class AmedasHistoryProvider(
    HttpClient http,
    IOptions<OpenDataOptions> options,
    ILogger<AmedasHistoryProvider> logger) : IOutdoorHistoryProvider
{
    /// <summary>The hours each published block starts at, in JST.</summary>
    private static readonly int[] BlockHours = [0, 3, 6, 9, 12, 15, 18, 21];

    private readonly OpenDataOptions _options = options.Value;

    public async Task<IReadOnlyList<OutdoorObservation>> GetDayAsync(
        string pointCode, DateOnly localDate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pointCode))
        {
            return [];
        }

        var readings = new List<OutdoorObservation>(144);
        var date = localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        foreach (var hour in BlockHours)
        {
            ct.ThrowIfCancellationRequested();

            var url = string.Format(
                CultureInfo.InvariantCulture,
                _options.AmedasPointHistoryUrlFormat,
                pointCode,
                date,
                hour.ToString("00", CultureInfo.InvariantCulture));

            try
            {
                var response = await http.GetAsync(url, ct);

                // Blocks that have not happened yet are a plain 404. Today is normal to
                // ask for -- it is the day the live capture is still filling in -- so a
                // missing block is not worth a log line.
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                readings.AddRange(Parse(await response.Content.ReadAsStringAsync(ct)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    ex, "AMeDAS history unavailable for {Point} {Date} {Hour}h.", pointCode, date, hour);
            }
        }

        return readings.OrderBy(r => r.ObservedAtUtc).ToList();
    }

    /// <summary>
    /// Turns one three-hour block into observations. Keys are JST yyyyMMddHHmmss and each
    /// measurement is <c>[value, qualityFlag]</c>, so a non-zero flag drops the value for
    /// the same reason it does on the live map: a suspect reading is worse than none.
    /// </summary>
    internal static IReadOnlyList<OutdoorObservation> Parse(string json)
    {
        var readings = new List<OutdoorObservation>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return readings;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return readings;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!DateTime.TryParseExact(
                        entry.Name,
                        "yyyyMMddHHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var localStamp))
                {
                    continue;
                }

                if (Measured(entry.Value, "temp") is not { } temp)
                {
                    continue;
                }

                var offset = HouseholdTime.Zone.GetUtcOffset(localStamp);

                readings.Add(new OutdoorObservation(
                    new DateTimeOffset(localStamp, offset).ToUniversalTime(),
                    temp,
                    Measured(entry.Value, "humidity")));
            }
        }

        return readings;
    }

    private static double? Measured(JsonElement point, string name)
    {
        if (!point.TryGetProperty(name, out var pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() < 1
            || pair[0].ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (pair.GetArrayLength() >= 2
            && pair[1].ValueKind == JsonValueKind.Number
            && pair[1].GetInt32() != 0)
        {
            return null;
        }

        return pair[0].GetDouble();
    }
}

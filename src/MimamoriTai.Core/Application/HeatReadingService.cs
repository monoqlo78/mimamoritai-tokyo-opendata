using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Outcome of one <see cref="HeatReadingService.PublishUnpublishedBatchAsync"/> cycle.</summary>
public sealed record HeatReadingPublishBatchResult(int Attempted, int Published, bool Success, string? Error)
{
    public static readonly HeatReadingPublishBatchResult Empty = new(0, 0, true, null);
}

/// <summary>
/// Captures the current outdoor observation into the app database and publishes the
/// backlog to the Fabric Eventhouse.
///
/// Split into capture and publish for the same reason
/// <see cref="PlugMiniReadingPublishService"/> is: the database write is the record
/// of truth and must succeed on its own, while the Eventhouse write is best-effort
/// and only stamps <see cref="HeatReading.PublishedToStreamAtUtc"/> when it actually
/// lands, so a Fabric outage delays analytics without ever losing an observation.
/// </summary>
public sealed class HeatReadingService(
    IAppDbContext db,
    IHeatReadingStreamPublisher publisher,
    TimeProvider clock,
    IWeatherAdvisoryProvider? advisoryProvider = null,
    IOutdoorHistoryProvider? historyProvider = null)
{
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Fetches the current advisories and stores them if that observation time is new.
    /// Returns the heat advisory (whether or not it was new) so a caller can reuse it,
    /// or null when no heat index was available.
    ///
    /// <para>
    /// Heat and cold share one row because they describe one moment outside the same
    /// window. The row's observation time is the AMeDAS reading's where we have one,
    /// falling back to the WBGT forecast column: the observation is the finer-grained
    /// and year-round of the two, so keying on it keeps the series unbroken through
    /// the five months WBGT is not published at all.
    /// </para>
    /// </summary>
    public async Task<HeatAdvisory?> CaptureAsync(string pointCode, CancellationToken ct = default)
    {
        if (advisoryProvider is null)
        {
            return null;
        }

        var advisory = await advisoryProvider.GetHeatAsync(ct);
        var cold = await advisoryProvider.GetColdAsync(ct);

        if (advisory is null && cold is null)
        {
            return null;
        }

        // The provider serves the same figures for the whole cache window, so most
        // cycles legitimately see an observation time that is already stored.
        var observedAtUtc = cold?.ObservedAtUtc ?? advisory!.ObservedAtUtc;
        var exists = await db.HeatReadings
            .AnyAsync(r => r.PointCode == pointCode && r.ObservedAtUtc == observedAtUtc, ct);

        if (exists)
        {
            return advisory;
        }

        db.HeatReadings.Add(new HeatReading
        {
            PointCode = pointCode,
            AreaName = cold?.AreaName ?? advisory!.AreaName,
            Wbgt = advisory?.Wbgt,
            Level = (int)(advisory?.Level ?? HeatAlertLevel.Unknown),
            ColdLevel = (int)(cold?.Level ?? ColdAlertLevel.Unknown),
            TemperatureC = cold?.TemperatureC ?? advisory?.TemperatureC,
            HumidityPercent = cold?.HumidityPercent ?? advisory?.HumidityPercent,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        return advisory;
    }

    /// <summary>
    /// A day is treated as already filled in once it holds this many observations. The
    /// live capture cycle can only ever record about fifty in a day, so anything above
    /// that must have come from the station's own published history.
    /// </summary>
    private const int FilledDayThreshold = 100;

    /// <summary>
    /// Fills in the outdoor observations for days this app was not running, from the
    /// station's published history.
    ///
    /// <para>
    /// Without this the weather history starts the moment the app does, so the chart that
    /// lays the weather over a household's electricity has nothing to draw for its first
    /// fortnight, and empties again the day a family moves the household to a nearer
    /// station. Neither is a real gap in the record -- 気象庁 has published those days all
    /// along -- so leaving the chart blank would be our omission presented as missing data.
    /// </para>
    /// <para>
    /// Only days that are short of readings are fetched, and only observation times we do
    /// not already hold are written, so restarting the app does not re-read a public
    /// website for days it has already collected.
    /// </para>
    /// </summary>
    /// <returns>How many observations were added.</returns>
    public async Task<int> BackfillAsync(
        string pointCode,
        string areaName,
        int days,
        CancellationToken ct = default)
    {
        if (historyProvider is null || string.IsNullOrWhiteSpace(pointCode) || days <= 0)
        {
            return 0;
        }

        var today = HouseholdTime.LocalDate(clock.GetUtcNow());
        var added = 0;

        for (var back = days - 1; back >= 0; back--)
        {
            ct.ThrowIfCancellationRequested();

            var date = today.AddDays(-back);
            var from = HouseholdTime.StartOfLocalDayUtc(date);
            var to = HouseholdTime.StartOfLocalDayUtc(date.AddDays(1));

            var stored = await db.HeatReadings
                .Where(r => r.PointCode == pointCode && r.ObservedAtUtc >= from && r.ObservedAtUtc < to)
                .Select(r => r.ObservedAtUtc)
                .ToListAsync(ct);

            if (stored.Count >= FilledDayThreshold)
            {
                continue;
            }

            var observations = await historyProvider.GetDayAsync(pointCode, date, ct);
            if (observations.Count == 0)
            {
                continue;
            }

            var known = stored.ToHashSet();
            var now = clock.GetUtcNow();

            foreach (var observation in observations)
            {
                if (!known.Add(observation.ObservedAtUtc))
                {
                    continue;
                }

                // A past observation carries no advisory: WBGT is a forecast product and
                // the cold level is derived from a live reading, so both are recorded as
                // unknown rather than back-calculated into something that was never issued.
                db.HeatReadings.Add(new HeatReading
                {
                    PointCode = pointCode,
                    AreaName = areaName,
                    Wbgt = null,
                    Level = (int)HeatAlertLevel.Unknown,
                    ColdLevel = (int)ColdAlertLevel.Unknown,
                    TemperatureC = observation.TemperatureC,
                    HumidityPercent = observation.HumidityPercent,
                    ObservedAtUtc = observation.ObservedAtUtc,
                    ReceivedAtUtc = now
                });

                added++;
            }

            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    /// <summary>
    /// Publishes up to <paramref name="batchSize"/> readings with a null
    /// PublishedToStreamAtUtc, oldest first, and stamps them only on success.
    /// </summary>
    public async Task<HeatReadingPublishBatchResult> PublishUnpublishedBatchAsync(
        int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        var pending = await db.HeatReadings
            .Where(r => r.PublishedToStreamAtUtc == null)
            .OrderBy(r => r.ObservedAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return HeatReadingPublishBatchResult.Empty;
        }

        var records = Project(pending);
        var result = await publisher.PublishAsync(records, ct);

        if (!result.Success)
        {
            // Leave the rows unstamped so the next cycle retries them.
            return new HeatReadingPublishBatchResult(pending.Count, 0, false, result.Error);
        }

        var stampedAtUtc = clock.GetUtcNow();
        foreach (var reading in pending)
        {
            reading.PublishedToStreamAtUtc = stampedAtUtc;
        }
        await db.SaveChangesAsync(ct);

        return new HeatReadingPublishBatchResult(pending.Count, result.PublishedCount, true, null);
    }

    /// <summary>
    /// Shapes stored rows for the Eventhouse. The band labels are denormalised into the
    /// record so KQL and the Data Agent can group by 「厳重警戒」 or 「厳しい冷え込み」
    /// without having to carry a copy of the thresholds.
    /// </summary>
    public static List<HeatReadingRecord> Project(IReadOnlyList<HeatReading> readings) =>
        readings.Select(r => new HeatReadingRecord(
            r.Id,
            r.PointCode,
            r.AreaName,
            r.Wbgt,
            r.Level,
            HeatAdvisory.LevelLabel((HeatAlertLevel)r.Level),
            r.ColdLevel,
            ColdAdvisory.LevelLabel((ColdAlertLevel)r.ColdLevel),
            r.TemperatureC,
            r.HumidityPercent,
            r.ObservedAtUtc.UtcDateTime)).ToList();
}

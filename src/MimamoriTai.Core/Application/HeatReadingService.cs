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
/// Captures the current outdoor heat index into the app database and publishes the
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
    IHeatAdvisoryProvider? advisoryProvider = null)
{
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Fetches the current advisory and stores it if that observation time is new.
    /// Returns the advisory (whether or not it was new) so a caller can reuse it, or
    /// null when open data is unavailable.
    /// </summary>
    public async Task<HeatAdvisory?> CaptureAsync(string pointCode, CancellationToken ct = default)
    {
        if (advisoryProvider is null)
        {
            return null;
        }

        var advisory = await advisoryProvider.GetCurrentAsync(ct);
        if (advisory is null)
        {
            return null;
        }

        // The provider serves the same forecast column for the whole cache window, so
        // most cycles legitimately see an observation time that is already stored.
        var observedAtUtc = advisory.ObservedAtUtc;
        var exists = await db.HeatReadings
            .AnyAsync(r => r.PointCode == pointCode && r.ObservedAtUtc == observedAtUtc, ct);

        if (exists)
        {
            return advisory;
        }

        db.HeatReadings.Add(new HeatReading
        {
            PointCode = pointCode,
            AreaName = advisory.AreaName,
            Wbgt = advisory.Wbgt,
            Level = (int)advisory.Level,
            TemperatureC = advisory.TemperatureC,
            HumidityPercent = advisory.HumidityPercent,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        return advisory;
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
    /// Shapes stored rows for the Eventhouse. The band label is denormalised into the
    /// record so KQL and the Data Agent can group by 「厳重警戒」 without having to
    /// carry a copy of the 環境省 thresholds.
    /// </summary>
    public static List<HeatReadingRecord> Project(IReadOnlyList<HeatReading> readings) =>
        readings.Select(r => new HeatReadingRecord(
            r.Id,
            r.PointCode,
            r.AreaName,
            r.Wbgt,
            r.Level,
            HeatAdvisory.LevelLabel((HeatAlertLevel)r.Level),
            r.TemperatureC,
            r.HumidityPercent,
            r.ObservedAtUtc.UtcDateTime)).ToList();
}

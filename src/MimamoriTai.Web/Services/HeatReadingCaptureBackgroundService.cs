using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure.OpenData;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Captures the outdoor heat index from open data into the app database, then
/// publishes anything the Fabric Eventhouse has not seen yet.
///
/// Capture and publish share one cycle here, unlike the device streams, because the
/// source is a single low-volume public feed rather than a per-household poll: at one
/// observation every few hours there is nothing to gain from two schedules. They stay
/// two separate calls though, so a Fabric outage still leaves the database write
/// intact. Every exception is caught and logged: this must never crash the app.
/// </summary>
public sealed class HeatReadingCaptureBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OpenDataOptions> openData,
    IOptions<FabricPublishOptions> publishOptions,
    ILogger<HeatReadingCaptureBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(35);

    private readonly OpenDataOptions _openData = openData.Value;
    private readonly FabricPublishOptions _publishOptions = publishOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_openData.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Never poll the public feed faster than the provider's own cache window: a
        // tighter loop would only re-read a value that cannot have changed, at the
        // 環境省 site's expense.
        var interval = TimeSpan.FromMinutes(Math.Max(_openData.CacheMinutes, 10));
        var backoff = new PeriodicBackoff(interval, _publishOptions.MaxBackoff);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Every cycle, not just at startup: a household may watch a station other
            // than the configured one, and the capture below only ever reads that one.
            // Days already collected are skipped, so in practice this re-reads today.
            await BackfillHistoryAsync(stoppingToken);

            var succeeded = await RunOnceAsync(stoppingToken);
            var wait = backoff.Next(succeeded);

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Fills in the days before this process started, once, for every station a
    /// household actually watches.
    ///
    /// <para>
    /// The capture loop below only ever records the present moment, so on its own the
    /// outdoor history begins when the app does. That leaves the chart that lays the
    /// weather over a household's electricity blank for its first fortnight, and blank
    /// again the day a family picks a nearer station -- readings for that station having
    /// never been collected. Both are gaps in our record rather than in 気象庁's, so we
    /// fetch what was published.
    /// </para>
    /// <para>
    /// Runs before the first capture because it is the slower of the two and the screen
    /// has nothing to show until it finishes. Failures are swallowed for the same reason
    /// every other public-data failure is: a government website being unreachable must
    /// not stop the watch service.
    /// </para>
    /// </summary>
    private async Task BackfillHistoryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<HeatReadingService>();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            var stations = await db.Households
                .Where(h => h.AmedasStationCode != null && h.AmedasStationCode != "")
                .Select(h => new { Code = h.AmedasStationCode!, Name = h.AmedasStationName })
                .Distinct()
                .ToListAsync(ct);

            // The configured point is always included: it is what the capture loop below
            // records against, and it is the only station a household that never chose
            // one is charted from.
            var targets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [_openData.PointCode] = _openData.AreaName
            };

            foreach (var station in stations)
            {
                targets[station.Code] = station.Name ?? station.Code;
            }

            foreach (var (code, name) in targets)
            {
                var added = await service.BackfillAsync(code, name, _openData.HistoryBackfillDays, ct);

                if (added > 0)
                {
                    logger.LogInformation(
                        "Filled in {Added} past outdoor observation(s) for {Point} from 気象庁.", added, code);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fill in past outdoor observations; the chart will fill as readings arrive.");
        }
    }

    /// <returns>False only when a cycle actually failed, so the caller can slow down.</returns>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<HeatReadingService>();

            var advisory = await service.CaptureAsync(_openData.PointCode, ct);
            if (advisory is null)
            {
                // Out of season the 環境省 feed is simply not published. That is not a
                // failure, so it must not trigger the backoff -- and the backfilled and
                // cold-only rows still need publishing, so we carry on rather than
                // leaving the whole winter's readings out of the Eventhouse.
                logger.LogDebug("No heat advisory available for {Point}.", _openData.PointCode);
            }

            var publisher = scope.ServiceProvider.GetRequiredService<IHeatReadingStreamPublisher>();
            if (!publisher.IsConfigured)
            {
                return true;
            }

            var result = await service.PublishUnpublishedBatchAsync(ct: ct);

            if (result.Attempted == 0)
            {
                return true;
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Published {Published}/{Attempted} heat reading(s) to the Fabric Eventhouse.",
                    result.Published, result.Attempted);
                return true;
            }

            logger.LogWarning(
                "Fabric Eventhouse publish failed for {Attempted} pending heat reading(s) ({Error}); will retry, backing off while it keeps failing.",
                result.Attempted, result.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
            return true;
        }
        catch (Exception ex)
        {
            // The background capture must never take the app down.
            logger.LogWarning(ex, "Heat reading capture cycle failed; will retry next interval.");
            return false;
        }
    }
}

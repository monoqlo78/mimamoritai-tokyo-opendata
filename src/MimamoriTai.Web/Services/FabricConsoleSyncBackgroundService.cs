using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Keeps the Fabric (Rayfin) operator console current.
///
/// Before this existed the only way anything reached Fabric SQL was a human running
/// <c>fabric-app/scripts/sync-to-fabric.ps1</c>, so the console showed whatever the last
/// manual run captured and the "Model Router calls" figure sat still for days. This is the
/// scheduled replacement.
///
/// Follows the same contract as the other publishers here: a cheap no-op when
/// unconfigured, every exception caught and logged, and retry-on-the-next-cycle rather
/// than backoff state. That matters more than usual for this one, because the Fabric
/// capacity is an F2 that can be paused or throttled at any moment; a sync that fails
/// while Fabric is asleep must be indistinguishable from one that never ran, and the
/// next successful cycle must fully catch up on its own. It does: every write is an
/// idempotent MERGE of the current rollup, not a delta.
/// </summary>
public sealed class FabricConsoleSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<FabricConsoleSyncOptions> options,
    ILogger<FabricConsoleSyncBackgroundService> logger) : BackgroundService
{
    // Longer than the other publishers': this one runs four aggregate queries against
    // the app database, so it should not compete with startup and first-page traffic.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);

    private readonly FabricConsoleSyncOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            logger.LogInformation(
                "Fabric console sync is not configured; the Fabric console will keep showing its bundled snapshot.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));

        // The sync has been failing every 15 minutes with a Fabric SQL permission
        // error. Retrying a permission problem on a fixed schedule never fixes it,
        // it just keeps knocking on a capacity that is already rejecting calls, so
        // slow down while it keeps failing. See PeriodicBackoff.
        var backoff = new PeriodicBackoff(interval, TimeSpan.FromHours(2));

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
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

    /// <returns>False only when a cycle actually failed, so the caller can slow down.</returns>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IFabricConsoleSync>();

            if (!sync.IsConfigured)
            {
                return true;
            }

            var result = await sync.SyncAsync(ct);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Fabric console sync failed ({Error}); will retry, backing off while it keeps failing.",
                    result.Error);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
            return true;
        }
        catch (Exception ex)
        {
            // The background sync must never take the app down.
            logger.LogWarning(ex, "Fabric console sync cycle failed; will retry next interval.");
            return false;
        }
    }
}

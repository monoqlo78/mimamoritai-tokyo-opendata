using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Auth;

namespace MimamoriTai.Web.Endpoints;

/// <summary>
/// Manual trigger for the Fabric operator-console ingestion, so it can be run on demand
/// instead of waiting for <c>FabricConsoleSyncBackgroundService</c>'s next cycle -- the
/// same relationship <c>AlertEndpoints</c> has with the alert poller.
///
/// It runs the identical <see cref="IFabricConsoleSync"/> the scheduler uses, so an
/// on-demand run and a scheduled run cannot drift, and running both at once is harmless
/// because every write is an idempotent MERGE.
///
/// Admin-gated: the sync reads across every household, which is exactly the boundary
/// <see cref="AdminAccessService"/> exists to guard.
/// </summary>
public static class FabricSyncEndpoints
{
    public static IEndpointRouteBuilder MapFabricSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/fabric/sync", async (
            AdminAccessService adminAccess,
            IFabricConsoleSync sync,
            CancellationToken ct) =>
        {
            if (!adminAccess.IsAdmin)
            {
                // Same shape as the rest of the app: do not confirm the endpoint exists.
                return Results.NotFound();
            }

            if (!sync.IsConfigured)
            {
                return Results.Json(new
                {
                    error = "Fabric console sync is not configured. Set FabricConsoleSync:Enabled, :ServerFqdn and :Database.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var result = await sync.SyncAsync(ct);

            var payload = new
            {
                success = result.Success,
                households = result.Households,
                alerts = result.Alerts,
                activityBuckets = result.ActivityBuckets,
                aiRouterCalls = result.AiRouterCalls,
                outdoorReadings = result.OutdoorReadings,
                totalRows = result.TotalRows,
                durationMs = result.DurationMs,
                error = result.Error,
            };

            // 502 rather than 500: a failure here is almost always the Fabric capacity
            // being paused or throttled, not a fault in this app.
            return result.Success
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status502BadGateway);
        }).WithName("PostFabricSync").DisableAntiforgery();

        return app;
    }
}

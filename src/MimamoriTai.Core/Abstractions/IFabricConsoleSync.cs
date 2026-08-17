namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Outcome of one operator-console sync into the Fabric SQL database.
///
/// A failed sync is never fatal: the Fabric capacity can be paused or throttled at
/// any time (it is an F2 in this deployment), so callers log the error and let the
/// next cycle retry. Every write is a MERGE on a deterministic key, so retrying is
/// safe and never duplicates rows.
/// </summary>
public sealed record FabricConsoleSyncResult(
    bool Success,
    int Households,
    int Alerts,
    int ActivityBuckets,
    int AiRouterCalls,
    long DurationMs,
    string? Error,
    int OutdoorReadings = 0)
{
    public int TotalRows => Households + Alerts + ActivityBuckets + AiRouterCalls + OutdoorReadings;

    public static FabricConsoleSyncResult Failed(string error, long durationMs) =>
        new(false, 0, 0, 0, 0, durationMs, error);
}

/// <summary>
/// Pushes the cross-household operator-console rollup into the Fabric SQL database
/// that backs the Rayfin console.
///
/// This is the ingestion path <c>fabric-app/scripts/sync-to-fabric.ps1</c> was written
/// for and which its own docstring records as unresolved. That script cannot work from
/// a developer machine: Fabric SQL uses the Azure SQL <em>Redirect</em> connection
/// policy, so after the TCP handshake on 1433 the client is redirected to a node port
/// in 11000-11999, and those ports are blocked on ordinary networks. The connection is
/// then reset mid-login, which looks like a credential or TLS fault but is neither.
/// Running the sync from inside Azure (where the redirect range is reachable) is what
/// makes it work, which is why this lives in the app rather than in a local script.
/// </summary>
public interface IFabricConsoleSync
{
    /// <summary>False when no Fabric SQL target is configured, so callers can no-op cheaply.</summary>
    bool IsConfigured { get; }

    Task<FabricConsoleSyncResult> SyncAsync(CancellationToken ct = default);
}

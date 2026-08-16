using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Auth;

namespace MimamoriTai.Web.Services;

/// <summary>One household's operational health, as shown in the console's household table.</summary>
public sealed record AdminHouseholdRow(
    Guid Id,
    string Name,
    DataSourceMode DataSourceMode,
    int MemberCount,
    int ResidentCount,
    int DeviceCount,
    /// <summary>Of <see cref="DeviceCount"/>, how many are still active. A plug that has
    /// fallen out of the SwitchBot account stays on the row but stops counting here.</summary>
    int ActiveDeviceCount,
    DateTimeOffset? LastEventUtc,
    SwitchBotConnectionStatus? SwitchBotStatus,
    DateTimeOffset? SwitchBotLastSyncUtc,
    string? SwitchBotError,
    int ActiveLineRecipients,
    int AlertsInWindow,
    int FailedAlertsInWindow,
    RiskLevel? LatestRiskLevel,
    DateTimeOffset? LatestRiskAtUtc);

/// <summary>A single alert delivery attempt, newest first, across every household.</summary>
public sealed record AdminAlertRow(
    DateTimeOffset SentAtUtc,
    Guid HouseholdId,
    string HouseholdName,
    RiskLevel RiskLevel,
    int Score,
    string Reason,
    bool Success,
    string? Error);

/// <summary>AI usage rolled up by resolved model, for cost/latency sanity checks.</summary>
public sealed record AdminAiUsageRow(
    string Router,
    string ResolvedModel,
    int Requests,
    int Failures,
    double AverageDurationMs,
    /// <summary>Most recent failure reason in the window, null when nothing failed.</summary>
    string? LastError);

public sealed record AdminConsoleModel(
    int WindowDays,
    DateTimeOffset GeneratedAtUtc,
    bool AuthIsConfigured,
    bool IsDemoModeGrant,
    int HouseholdCount,
    int ProductionHouseholdCount,
    int UserCount,
    int DeviceCount,
    /// <summary>Active devices across every household, so a console reading of "1" can be
    /// told apart from "2 registered, 1 of them gone quiet".</summary>
    int ActiveDeviceCount,
    int AlertsInWindow,
    int FailedAlertsInWindow,
    int HouseholdsNeedingAttention,
    IReadOnlyList<AdminHouseholdRow> Households,
    IReadOnlyList<AdminAlertRow> RecentAlerts,
    IReadOnlyList<AdminAiUsageRow> AiUsage);

/// <summary>
/// Read-only, cross-household aggregation for the operator console.
///
/// Every other read path in the app is scoped to one household and gated by
/// <c>HouseholdAccessService.CanAccessAsync</c>. This service is the deliberate
/// exception: it reads across all households, so it is gated once, at the top of
/// <see cref="LoadAsync"/>, by <see cref="AdminAccessService"/>, and it never mutates
/// anything. It also never selects the <c>SwitchBotConnection.Encrypted*</c> columns --
/// only the status/timestamp/error fields that entity documents as safe to surface.
/// </summary>
public sealed class AdminConsoleService(
    IAppDbContext db,
    AdminAccessService adminAccess,
    TimeProvider clock)
{
    public const int DefaultWindowDays = 7;

    private const int RecentAlertLimit = 50;

    /// <summary>
    /// Returns null when the caller is not an administrator. Callers render a
    /// not-found/forbidden view in that case rather than leaking that the page exists.
    /// </summary>
    public async Task<AdminConsoleModel?> LoadAsync(int windowDays = DefaultWindowDays, CancellationToken ct = default)
    {
        if (!adminAccess.IsAdmin)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var since = now.AddDays(-Math.Max(1, windowDays));

        var households = await db.Households
            .OrderBy(h => h.DataSourceMode)
            .ThenBy(h => h.CreatedAtUtc)
            .Select(h => new { h.Id, h.Name, h.DataSourceMode })
            .ToListAsync(ct);

        var householdIds = households.Select(h => h.Id).ToList();
        var householdNames = households.ToDictionary(h => h.Id, h => h.Name);

        var memberCounts = await db.HouseholdMembers
            .GroupBy(m => m.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Count, ct);

        var residentCounts = await db.People
            .Where(p => p.Role == PersonRole.Resident)
            .GroupBy(p => p.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Count, ct);

        var deviceCounts = await db.Devices
            .GroupBy(d => d.HouseholdId)
            .Select(g => new
            {
                HouseholdId = g.Key,
                Count = g.Count(),
                Active = g.Count(d => d.IsActive)
            })
            .ToDictionaryAsync(x => x.HouseholdId, x => (x.Count, x.Active), ct);

        var lastDeviceEvents = await db.DeviceEvents
            .GroupBy(e => e.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Last = g.Max(e => e.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Last, ct);

        var lastPlugReadings = await db.PlugMiniReadings
            .GroupBy(r => r.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Last = g.Max(r => r.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Last, ct);

        // Encrypted token/secret columns are intentionally not projected.
        var connections = await db.SwitchBotConnections
            .Select(c => new
            {
                c.HouseholdId,
                c.Status,
                c.LastSyncAtUtc,
                c.LastErrorMessage
            })
            .ToDictionaryAsync(x => x.HouseholdId, x => x, ct);

        var lineCounts = await db.LineRecipients
            .Where(r => r.IsActive)
            .GroupBy(r => r.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Count, ct);

        var alertStats = await db.WatchAlerts
            .Where(a => a.SentAtUtc >= since)
            .GroupBy(a => a.HouseholdId)
            .Select(g => new
            {
                HouseholdId = g.Key,
                Total = g.Count(),
                Failed = g.Count(a => !a.Success)
            })
            .ToDictionaryAsync(x => x.HouseholdId, x => x, ct);

        var latestRisks = await db.RiskAssessments
            .GroupBy(r => r.HouseholdId)
            .Select(g => new
            {
                HouseholdId = g.Key,
                CreatedAtUtc = g.Max(r => r.CreatedAtUtc)
            })
            .ToListAsync(ct);

        var latestRiskLevels = new Dictionary<Guid, (RiskLevel Level, DateTimeOffset At)>();
        foreach (var risk in latestRisks)
        {
            var level = await db.RiskAssessments
                .Where(r => r.HouseholdId == risk.HouseholdId && r.CreatedAtUtc == risk.CreatedAtUtc)
                .Select(r => (RiskLevel?)r.RiskLevel)
                .FirstOrDefaultAsync(ct);

            if (level is not null)
            {
                latestRiskLevels[risk.HouseholdId] = (level.Value, risk.CreatedAtUtc);
            }
        }

        var rows = new List<AdminHouseholdRow>(households.Count);
        foreach (var household in households)
        {
            connections.TryGetValue(household.Id, out var connection);
            alertStats.TryGetValue(household.Id, out var alerts);
            latestRiskLevels.TryGetValue(household.Id, out var risk);
            var lastDeviceEvent = lastDeviceEvents.TryGetValue(household.Id, out var deviceEventAt)
                ? deviceEventAt
                : (DateTimeOffset?)null;
            var lastPlugReading = lastPlugReadings.TryGetValue(household.Id, out var plugReadingAt)
                ? plugReadingAt
                : (DateTimeOffset?)null;
            var lastEvent = Max(lastDeviceEvent, lastPlugReading);

            rows.Add(new AdminHouseholdRow(
                Id: household.Id,
                Name: household.Name,
                DataSourceMode: household.DataSourceMode,
                MemberCount: memberCounts.GetValueOrDefault(household.Id),
                ResidentCount: residentCounts.GetValueOrDefault(household.Id),
                DeviceCount: deviceCounts.GetValueOrDefault(household.Id).Count,
                ActiveDeviceCount: deviceCounts.GetValueOrDefault(household.Id).Active,
                LastEventUtc: lastEvent,
                SwitchBotStatus: connection?.Status,
                SwitchBotLastSyncUtc: connection?.LastSyncAtUtc,
                SwitchBotError: connection?.LastErrorMessage,
                ActiveLineRecipients: lineCounts.GetValueOrDefault(household.Id),
                AlertsInWindow: alerts?.Total ?? 0,
                FailedAlertsInWindow: alerts?.Failed ?? 0,
                LatestRiskLevel: risk == default ? null : risk.Level,
                LatestRiskAtUtc: risk == default ? null : risk.At));
        }

        var recentAlertRows = await db.WatchAlerts
            .Where(a => a.SentAtUtc >= since)
            .OrderByDescending(a => a.SentAtUtc)
            .Take(RecentAlertLimit)
            .Select(a => new
            {
                a.SentAtUtc,
                a.HouseholdId,
                a.RiskLevel,
                a.Score,
                a.Reason,
                a.Success,
                a.Error
            })
            .ToListAsync(ct);

        var recentAlerts = recentAlertRows
            .Select(a => new AdminAlertRow(
                a.SentAtUtc,
                a.HouseholdId,
                householdNames.GetValueOrDefault(a.HouseholdId, "(削除済み)"),
                a.RiskLevel,
                a.Score,
                a.Reason,
                a.Success,
                a.Error))
            .ToList();

        var aiUsageRaw = await db.AiRequestLogs
            .Where(l => l.CreatedAtUtc >= since)
            .GroupBy(l => new { l.Router, l.ResolvedModel })
            .Select(g => new
            {
                g.Key.Router,
                g.Key.ResolvedModel,
                Requests = g.Count(),
                Failures = g.Count(l => !l.Success),
                AverageDurationMs = g.Average(l => (double)l.DurationMs),
                // A failure count alone leaves the operator with nowhere to go.
                // The newest reason is the one worth showing: if the cause is
                // still live it is this one, and if it is stale the timestamp
                // column already says so.
                LastError = g
                    .Where(l => !l.Success && l.Error != null)
                    .OrderByDescending(l => l.CreatedAtUtc)
                    .Select(l => l.Error)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var aiUsage = aiUsageRaw
            .OrderByDescending(r => r.Requests)
            .Select(r => new AdminAiUsageRow(
                r.Router, r.ResolvedModel, r.Requests, r.Failures, r.AverageDurationMs, r.LastError))
            .ToList();

        var userCount = await db.AppUsers.CountAsync(ct);

        return new AdminConsoleModel(
            WindowDays: Math.Max(1, windowDays),
            GeneratedAtUtc: now,
            AuthIsConfigured: adminAccess.AuthIsConfigured,
            IsDemoModeGrant: adminAccess.IsDemoModeGrant,
            HouseholdCount: rows.Count,
            ProductionHouseholdCount: rows.Count(r => r.DataSourceMode == DataSourceMode.Production),
            UserCount: userCount,
            DeviceCount: rows.Sum(r => r.DeviceCount),
            ActiveDeviceCount: rows.Sum(r => r.ActiveDeviceCount),
            AlertsInWindow: rows.Sum(r => r.AlertsInWindow),
            FailedAlertsInWindow: rows.Sum(r => r.FailedAlertsInWindow),
            HouseholdsNeedingAttention: rows.Count(NeedsAttention),
            Households: rows,
            RecentAlerts: recentAlerts,
            AiUsage: aiUsage);
    }

    /// <summary>
    /// A household is flagged when an operator would actually have to do something:
    /// alert delivery failed, the SwitchBot connection is in error, or a Production
    /// household has no active LINE recipient (so alerts would go nowhere).
    /// </summary>
    public static bool NeedsAttention(AdminHouseholdRow row) =>
        row.FailedAlertsInWindow > 0
        || row.SwitchBotStatus == SwitchBotConnectionStatus.Error
        || (row.DataSourceMode == DataSourceMode.Production && row.ActiveLineRecipients == 0);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;
}

using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Endpoints;

public sealed record AssistantMessageRequest(Guid? HouseholdId, Guid? PersonId, string Message, string? Source);

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }))
            .WithName("Health");

        app.MapGet("/api/devices", ListDevicesAsync).WithName("GetDevices");

        app.MapGet("/api/devices/{id:guid}", GetDeviceAsync).WithName("GetDevice");

        app.MapPost("/api/assistant/message", async (
            AssistantMessageRequest request,
            AssistantOrchestrator orchestrator,
            AppDbContext db,
            HouseholdAccessService householdAccess,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Message is required." });
            }

            var householdId = request.HouseholdId
                ?? await householdAccess.ResolveDefaultAsync(ct);

            if (householdId is null || householdId == Guid.Empty)
            {
                return Results.Problem("No household is registered.");
            }

            if (!await householdAccess.CanAccessAsync(householdId.Value, ct))
            {
                return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var source = Enum.TryParse<CommandSource>(request.Source, ignoreCase: true, out var parsed)
                ? parsed
                : CommandSource.Web;

            var response = await orchestrator.HandleAsync(
                new AssistantRequest(householdId.Value, request.PersonId, request.Message, source), ct);

            return Results.Ok(response);
        }).WithName("PostAssistantMessage").DisableAntiforgery();

        app.MapGet("/api/activity/today", async (
            Guid? householdId, AppDbContext db, HouseholdAccessService householdAccess, TimeProvider clock, CancellationToken ct) =>
        {
            var id = householdId ?? await householdAccess.ResolveDefaultAsync(ct);
            if (id is null || id == Guid.Empty)
            {
                return Results.NotFound();
            }

            if (!await householdAccess.CanAccessAsync(id.Value, ct))
            {
                return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var activity = new ActivityService(db);
            var today = HouseholdTime.LocalDate(clock.GetUtcNow());
            return Results.Ok(await activity.GetDailyAsync(id.Value, today, ct));
        }).WithName("GetTodayActivity");

        app.MapGet("/api/activity/recent", async (
            Guid? householdId, int? days, AppDbContext db, HouseholdAccessService householdAccess, CancellationToken ct) =>
        {
            var id = householdId ?? await householdAccess.ResolveDefaultAsync(ct);
            if (id is null || id == Guid.Empty)
            {
                return Results.NotFound();
            }

            if (!await householdAccess.CanAccessAsync(id.Value, ct))
            {
                return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var activity = new ActivityService(db);
            return Results.Ok(await activity.GetRecentAsync(id.Value, Math.Clamp(days ?? 14, 1, 60), ct));
        }).WithName("GetRecentActivity");

        return app;
    }

    /// <summary>
    /// Devices are household-scoped data, but this endpoint used to take no household at
    /// all: it returned every row in the table to any caller, while each of its
    /// neighbours above went through <see cref="HouseholdAccessService"/>. A reviewer
    /// found it, and it was the one place where "which household is asking?" was never
    /// asked. It now resolves and checks a household exactly as they do.
    /// </summary>
    internal static async Task<IResult> ListDevicesAsync(
        Guid? householdId, AppDbContext db, HouseholdAccessService householdAccess, CancellationToken ct)
    {
        var id = householdId ?? await householdAccess.ResolveDefaultAsync(ct);
        if (id is null || id == Guid.Empty)
        {
            return Results.NotFound();
        }

        if (!await householdAccess.CanAccessAsync(id.Value, ct))
        {
            return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        // Materialized before projecting: Name/Room are the display values, which are
        // computed from the override columns and so cannot be evaluated in SQL.
        var rows = await db.Devices.Where(d => d.HouseholdId == id.Value).ToListAsync(ct);

        var devices = rows
            .OrderBy(d => d.DisplayName, StringComparer.Ordinal)
            .Select(d => new
            {
                d.Id,
                Name = d.DisplayName,
                d.Alias,
                Room = d.DisplayRoom,
                ProviderName = d.Name,
                ProviderRoom = d.Room,
                DeviceType = d.DeviceType.ToString(),
                Provider = d.Provider.ToString(),
                d.IsEnabled,
                d.RemoteControlAllowed,
                SafetyClass = d.SafetyClass.ToString()
            })
            .ToList();

        return Results.Ok(devices);
    }

    /// <summary>
    /// Single device by id, scoped to the caller's household for the same reason as
    /// <see cref="ListDevicesAsync"/>.
    /// </summary>
    internal static async Task<IResult> GetDeviceAsync(
        Guid id, AppDbContext db, HouseholdAccessService householdAccess, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        // 404 rather than 403 for a device in someone else's household: answering
        // "forbidden" would confirm the id exists, which is itself a disclosure.
        if (!await householdAccess.CanAccessAsync(device.HouseholdId, ct))
        {
            return Results.NotFound();
        }

        var lastEvent = await db.DeviceEvents
            .Where(e => e.DeviceId == id)
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            device.Id,
            Name = device.DisplayName,
            device.Alias,
            Room = device.DisplayRoom,
            ProviderName = device.Name,
            ProviderRoom = device.Room,
            DeviceType = device.DeviceType.ToString(),
            Provider = device.Provider.ToString(),
            device.IsEnabled,
            device.RemoteControlAllowed,
            SafetyClass = device.SafetyClass.ToString(),
            LastState = lastEvent?.State,
            LastEventUtc = lastEvent?.OccurredAtUtc
        });
    }
}

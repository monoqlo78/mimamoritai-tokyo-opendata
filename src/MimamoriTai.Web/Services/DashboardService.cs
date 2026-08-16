using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Services;

/// <summary>
/// One appliance as the family sees it. <paramref name="Name"/>/<paramref name="Room"/> are
/// already the display values (the family's own wording when they set one, otherwise the
/// provider's), so no caller has to know the override rules.
/// </summary>
public sealed record DeviceCard(
    Guid Id,
    string Name,
    string Alias,
    string Room,
    string DeviceType,
    bool IsOn,
    /// <summary>False when neither a live read nor any recorded event can say - shown as "確認中" rather than a wrong "停止中".</summary>
    bool IsStateKnown,
    DateTimeOffset? LastUsedUtc,
    int TodayUsageCount,
    bool RemoteControlAllowed,
    string SafetyClass,
    /// <summary>
    /// Live wattage when the hub reported one, otherwise null. A Plug Mini that is switched
    /// on but drawing 0 W is standing by, not in use - see <see cref="DeviceCardExtensions.IsStandingBy"/>.
    /// </summary>
    double? PowerWatts = null);

public static class DeviceCardExtensions
{
    /// <summary>
    /// True when the relay is on but no power is flowing. Showing that as 使用中 tells the
    /// family someone is up and about when nothing is actually running - the appliance may
    /// have been switched off at its own switch, or unplugged, hours ago.
    /// </summary>
    public static bool IsStandingBy(this DeviceCard card) =>
        card.IsStateKnown && card.IsOn && card.PowerWatts is <= 0;
}

public sealed record TimelineItem(DateTimeOffset OccurredAtUtc, string DeviceName, string State);

public sealed record FeedItem(DateTimeOffset OccurredAtUtc, string Author, string Content, bool IsAi);

public sealed record DashboardModel(
    Guid HouseholdId,
    string HouseholdName,
    DataSourceMode DataSourceMode,
    string ResidentName,
    RiskResult Risk,
    IReadOnlyList<Person> People,
    IReadOnlyList<DeviceCard> Devices,
    IReadOnlyList<TimelineItem> Timeline,
    IReadOnlyList<FeedItem> Feed,
    DailyActivity Today,
    IReadOnlyList<DailyActivity> Recent,
    HourlyEnergyProfile HourlyEnergy,
    string? LastResolvedModel,
    IntegrationStatus Integrations,
    HeatAdvisory? Heat = null,
    ColdAdvisory? Cold = null,
    ColdForecast? TomorrowCold = null,
    IReadOnlyList<DailyOutdoorTemperature>? OutdoorTemperatures = null,
    string? WeatherStationName = null)
{
    /// <summary>
    /// The public feeds this dashboard is standing on, with whatever each one is
    /// currently saying. Derived rather than stored so it can never drift from the
    /// figures the rest of the card is drawn from.
    /// </summary>
    public IReadOnlyList<OpenDataFeedStatus> OpenDataFeeds =>
    [
        new("環境省 暑さ指数（WBGT）",
            Heat is { } h ? $"{h.Wbgt:0.#} {h.LevelText}" : "4月下旬〜10月下旬のみ公開されます",
            "環境省熱中症予防情報サイト提供",
            Heat is not null),

        new("気象庁 アメダス観測（気温・湿度）",
            Cold is { } c
                ? $"{c.TemperatureC:0.#}℃"
                    + (c.HumidityPercent is { } ch ? $" 湿度{ch:0}%" : string.Empty)
                : Heat?.TemperatureC is { } ht ? $"{ht:0.#}℃" : "取得できていません",
            "気象庁アメダス",
            Cold is not null || Heat?.TemperatureC is not null),

        new("気象庁 天気予報（あすの最低気温）",
            TomorrowCold is { } f ? $"{f.MinTemperatureC:0.#}℃（{f.ForDateLocal:M月d日}）" : "取得できていません",
            "気象庁 天気予報",
            TomorrowCold is not null),

        new("気象庁 アメダス観測所一覧",
            WeatherStationName is { Length: > 0 } s ? $"{s} を使用中" : "東京（既定）を使用中",
            "気象庁アメダス観測所一覧",
            true),

        new("東京都 熱中症統計・世帯統計",
            "住居内での発症 56.6%／高齢者の中等症以上 54.8%",
            "東京都オープンデータカタログサイト（CC BY 4.0）",
            true),
    ];
}

/// <summary>One public feed as shown on the dashboard.</summary>
/// <param name="IsLive">False when the source is out of season or unreachable; said plainly rather than hidden.</param>
public sealed record OpenDataFeedStatus(string Name, string Value, string Source, bool IsLive);

/// <summary>Read model builder for the Blazor dashboard.</summary>
public sealed class DashboardService(
    AppDbContext db,
    IDeviceProvider deviceProvider,
    IDataSourceContext dataSourceContext,
    HouseholdAccessService householdAccess,
    IntegrationStatus integrations,
    TimeProvider clock,
    IWeatherAdvisoryProvider? heatAdvisory = null)
{
    public async Task<Guid?> GetDefaultHouseholdIdAsync(CancellationToken ct = default) =>
        await householdAccess.ResolveDefaultAsync(ct);

    public async Task<DashboardModel?> LoadAsync(Guid householdId, CancellationToken ct = default)
    {
        if (!await householdAccess.CanAccessAsync(householdId, ct))
        {
            return null;
        }

        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId, ct);
        if (household is null)
        {
            return null;
        }

        // Every unit of work must set the ambient data-source context explicitly so the
        // IDeviceProvider decorator resolves the correct concrete provider for THIS household.
        // Going through the decorator (rather than the factory) is what makes it use the
        // household's OWN SwitchBot credentials: asking the factory directly only ever built
        // a provider from the global options, so a household that connected its account in
        // Settings got no status back at all and every appliance rendered as "停止中".
        dataSourceContext.Mode = household.DataSourceMode;
        dataSourceContext.HouseholdId = household.Id;

        var people = await db.People.Where(p => p.HouseholdId == householdId).OrderBy(p => p.Role).ToListAsync(ct);
        var devices = await db.Devices.Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        devices = [.. devices.OrderBy(d => d.DisplayName, StringComparer.Ordinal)];

        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var hourly = await activity.GetHourlyEnergyAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var risks = new RiskAssessmentService(db, clock, heatAdvisory);
        var heat = await risks.GetHeatAsync(ct);
        var cold = await risks.GetColdAsync(household, ct);
        var tomorrowCold = heatAdvisory is null ? null : await risks.GetTomorrowColdAsync(ct);
        var cooling = await risks.LoadCoolingAsync(householdId, ct);
        var heating = await risks.LoadHeatingAsync(householdId, ct);
        var outdoor = await risks.GetDailyTemperaturesAsync(14, household.AmedasStationCode, ct);
        var risk = RiskAssessmentService.Evaluate(
            today, recent, HouseholdTime.LocalTime(clock.GetUtcNow()), null, null, heat, cooling, cold, heating);

        var dayStart = HouseholdTime.StartOfLocalDayUtc(todayDate);

        var todayEvents = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= dayStart)
            .ToListAsync(ct);

        var lastUsedList = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId)
            .GroupBy(e => e.DeviceId)
            .Select(g => new { DeviceId = g.Key, Last = g.Max(x => x.OccurredAtUtc) })
            .ToListAsync(ct);

        var lastUsed = lastUsedList.ToDictionary(x => x.DeviceId, x => x.Last);

        var cards = new List<DeviceCard>();
        foreach (var device in devices)
        {
            var status = await deviceProvider.GetStatusAsync(device.ExternalDeviceId, ct);

            // A null status means the hub told us nothing (offline, rate-limited, or an
            // infrared remote that has no status endpoint). Falling back to the newest
            // recorded event keeps the card honest instead of defaulting to "停止中".
            // That event also outranks a live read for a few seconds after it happens,
            // because SwitchBot's status endpoint still reports the previous state right
            // after a change - see DevicePowerState.
            var lastEvent = await db.DeviceEvents
                .Where(e => e.DeviceId == device.Id)
                .OrderByDescending(e => e.OccurredAtUtc)
                .Select(e => new { e.State, e.OccurredAtUtc })
                .FirstOrDefaultAsync(ct);

            var power = DevicePowerState.Resolve(
                status?.IsOn, lastEvent?.State, lastEvent?.OccurredAtUtc, clock.GetUtcNow());

            cards.Add(new DeviceCard(
                device.Id,
                device.DisplayName,
                device.Alias,
                device.DisplayRoom,
                device.DeviceType.ToString(),
                power.IsOn,
                power.IsKnown,
                lastUsed.TryGetValue(device.Id, out var last) ? last : null,
                todayEvents.Count(e => e.DeviceId == device.Id && e.State.Equals("on", StringComparison.OrdinalIgnoreCase)),
                device.RemoteControlAllowed,
                device.SafetyClass.ToString(),
                // Only a live read carries a wattage; a state recovered from the event log
                // leaves this null so the card falls back to the plain on/off wording.
                power.IsOn ? status?.PowerWatts : null));
        }

        var deviceNames = devices.ToDictionary(d => d.Id, d => d.DisplayName);

        var timeline = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var timelineItems = timeline
            .Select(e => new TimelineItem(
                e.OccurredAtUtc,
                deviceNames.TryGetValue(e.DeviceId, out var name) ? name : "不明な機器",
                e.State))
            .ToList();

        var messages = await db.FamilyMessages
            .Where(m => m.HouseholdId == householdId)
            .OrderByDescending(m => m.OccurredAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var peopleNames = people.ToDictionary(p => p.Id, p => p.DisplayName);

        var feed = messages
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => new FeedItem(
                m.OccurredAtUtc,
                m.MessageType == MessageType.AiReply
                    ? "見守りAI"
                    : (m.PersonId is { } pid && peopleNames.TryGetValue(pid, out var n) ? n : "家族"),
                m.Content,
                m.MessageType == MessageType.AiReply))
            .ToList();

        var lastModel = await db.AiRequestLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => l.ResolvedModel)
            .FirstOrDefaultAsync(ct);

        var resident = people.FirstOrDefault(p => p.Role == PersonRole.Resident)?.DisplayName ?? "ご本人";

        return new DashboardModel(
            household.Id,
            household.Name,
            household.DataSourceMode,
            resident,
            risk,
            people,
            cards,
            timelineItems,
            feed,
            today,
            recent,
            hourly,
            lastModel,
            integrations,
            heat,
            cold,
            tomorrowCold,
            outdoor,
            household.AmedasStationName);
    }
}

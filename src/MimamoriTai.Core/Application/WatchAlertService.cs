using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Configuration for <see cref="WatchAlertService"/>. Kept as a plain POCO (rather than
/// an IOptions&lt;T&gt; from the LINE infrastructure) so Core has no dependency on
/// Infrastructure; the concrete values are read from LineOptions and wired up in
/// MimamoriTai.Infrastructure.ServiceCollectionExtensions.
/// </summary>
public sealed class WatchAlertSettings
{
    /// <summary>LINE group id or user id to push the alert to. Empty = not configured.</summary>
    public string ToId { get; init; } = string.Empty;

    /// <summary>Minimum risk level (inclusive) that triggers an alert.</summary>
    public RiskLevel Threshold { get; init; } = RiskLevel.Medium;

    /// <summary>How long a repeat alert for the same person + risk level is suppressed.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Absolute https origin of this deployment, e.g. "https://<your-app>.azurewebsites.net".
    ///
    /// LINE fetches a Flex image from its own servers, so a relative path or a
    /// localhost URL cannot work: without a real public origin the alert is sent as
    /// plain text instead. Empty is therefore a valid, safe configuration.
    /// </summary>
    public string PublicBaseUrl { get; init; } = string.Empty;
}

public enum WatchAlertStatus
{
    /// <summary>Current risk is below the configured threshold; nothing to do.</summary>
    BelowThreshold,

    /// <summary>An identical alert was already sent within the cooldown window.</summary>
    SuppressedByCooldown,

    /// <summary>A push was attempted and the LINE API reported success.</summary>
    Sent,

    /// <summary>A push was attempted but failed (or LINE/AlertToId is not configured).</summary>
    SendFailed,

    /// <summary>No resident is registered for the household; nothing to evaluate.</summary>
    NoResident
}

/// <summary>
/// The facts behind one alert, gathered in one place so that the deterministic wording
/// and the model-written wording are built from exactly the same picture.
///
/// <para>
/// This exists because of what the alert used to be. The message was composed from the
/// resident's name, the risk level and <see cref="RiskResult.Reason"/> alone, which is
/// enough to say <em>what rule fired</em> and nothing else. A daughter at work reading
/// 「活動量が少なめです」 cannot tell whether that means an hour late or a whole morning
/// gone, whether it is 35℃ outside, or what she is supposed to do about it -- so the
/// alert ended with a phone call to find out. The figures that answer all three were
/// already loaded a few lines above; they simply were not being passed along.
/// </para>
///
/// <para>
/// Every field is optional on purpose. A household with no registered plug has no
/// baseline, and WBGT is not published in winter. The wording is assembled from
/// whichever facts exist rather than from a fixed template with holes in it, because a
/// sentence with a blank where a number should be is worse than not saying it at all.
/// </para>
/// </summary>
internal sealed record AlertBriefing(
    string ResidentName,
    RiskResult Risk,
    TimeOnly NowLocal,
    string? AreaName,
    double? OutdoorTemperatureC,
    double? Wbgt,
    string? WeatherLevelText,
    double? TodayWh,
    double? UsualWh,
    TimeOnly? FirstActivityToday,
    TimeOnly? UsualFirstActivity,
    ComfortSuggestion? Suggestion)
{
    public static AlertBriefing From(
        string residentName,
        RiskResult risk,
        TimeOnly nowLocal,
        DailyActivity today,
        IReadOnlyList<DailyActivity> recent,
        HeatAdvisory? heat,
        ColdAdvisory? cold,
        ComfortSuggestion? suggestion)
    {
        // Days with no data at all are not an "ordinary day" to compare against; they
        // are days the plug was unplugged. Including them would drag the norm towards
        // zero and make a perfectly normal morning look like a quiet one.
        var reference = recent
            .Where(d => d.Date != today.Date && d.DeviceUsageCount > 0)
            .ToList();

        var usualWh = reference.Count > 0 && reference.Any(d => d.EnergyWh > 0)
            ? reference.Where(d => d.EnergyWh > 0).Average(d => d.EnergyWh)
            : (double?)null;

        var starts = reference
            .Where(d => d.FirstActivityTime is not null)
            .Select(d => d.FirstActivityTime!.Value.ToTimeSpan().TotalMinutes)
            .ToList();

        var usualStart = starts.Count > 0
            ? TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(starts.Average()))
            : (TimeOnly?)null;

        // Heat and cold never both apply: WBGT is only published in the warm half of the
        // year, and the cold advisory is only meaningful below it. Whichever the rules
        // were actually looking at is the one worth quoting.
        return new AlertBriefing(
            residentName,
            risk,
            nowLocal,
            heat?.AreaName ?? cold?.AreaName,
            heat?.TemperatureC ?? cold?.TemperatureC,
            heat?.Wbgt,
            heat is not null ? heat.LevelText : cold?.LevelText,
            today.EnergyWh > 0 ? today.EnergyWh : null,
            usualWh,
            today.FirstActivityTime,
            usualStart,
            suggestion);
    }

    /// <summary>
    /// The facts as labelled lines for the model. Deliberately not prose: a list of
    /// measurements cannot be mistaken for a sentence to copy, and anything absent is
    /// simply not a line, so there is nothing for the model to fill in.
    /// </summary>
    public string Facts()
    {
        var lines = new List<string>
        {
            $"対象: {ResidentName}",
            $"現在時刻: {NowLocal:HH\\:mm}",
            $"危険度: {Risk.Level}",
            $"検知内容: {Risk.Reason}"
        };

        if (AreaName is { Length: > 0 } area)
        {
            var outdoor = $"屋外({area}):";
            if (OutdoorTemperatureC is { } t) outdoor += $" 気温{t:0.#}℃";
            if (Wbgt is { } w) outdoor += $" 暑さ指数{w:0.#}";
            if (WeatherLevelText is { Length: > 0 } level) outdoor += $"（{level}）";

            if (outdoor.Length > $"屋外({area}):".Length)
            {
                lines.Add(outdoor);
            }
        }

        if (FirstActivityToday is { } first)
        {
            lines.Add(UsualFirstActivity is { } usualStart
                ? $"今日の最初の家電利用: {first:HH\\:mm}（普段は{usualStart:HH\\:mm}ごろ）"
                : $"今日の最初の家電利用: {first:HH\\:mm}");
        }
        else if (UsualFirstActivity is { } usualOnly)
        {
            lines.Add($"今日はまだ家電の利用がありません（普段は{usualOnly:HH\\:mm}ごろ）");
        }

        if (TodayWh is { } wh)
        {
            lines.Add(UsualWh is { } usual
                ? $"今日の電力量: {wh:0.#}Wh（普段は1日{usual:0.#}Wh）"
                : $"今日の電力量: {wh:0.#}Wh");
        }

        if (Suggestion is { } s)
        {
            lines.Add(s.CanTurnOnRemotely
                ? $"アプリでできること: 「{s.ActionLabel}」を押すと{s.DeviceName}を操作できます"
                : $"アプリでできること: {s.DeviceName}は遠隔操作しない設定のため、お電話での声かけが必要です");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The wording used when no model is available, or when what the model wrote was
    /// rejected. Not a placeholder: this is what most families actually receive on a
    /// day the router is busy, so it is written to be worth reading on its own.
    ///
    /// <para>
    /// The risk score is deliberately absent. 「スコア 55/100」 tells a family nothing
    /// they can act on -- it is an implementation detail of the rules, and reading it
    /// as a severity invites exactly the wrong comparison between two alerts whose
    /// causes are unrelated.
    /// </para>
    /// </summary>
    public string Compose()
    {
        // Reason is a "／"-joined list of independent findings. Read out loud that
        // separator is a stumble, and the findings are sentences, so they are set as
        // sentences -- trimming any full stop a rule already supplied so joining them
        // cannot produce "。。".
        var findings = Risk.Reason
            .Split('／', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.TrimEnd('。'))
            .Where(f => f.Length > 0)
            .ToArray();

        var body = findings.Length > 0 ? string.Join("。", findings) : "いつもと違うようすが見られます";

        var parts = new List<string>
        {
            $"{ResidentName}さんのお宅の{NowLocal:HH\\:mm}時点のようすです。{body}。"
        };

        // How far from ordinary this is -- the question the finding on its own leaves
        // open. Skipped when a rule already made the comparison, so the message does not
        // say the same thing twice.
        if (!Risk.Reason.Contains("普段", StringComparison.Ordinal)
            && UsualFirstActivity is { } usualStart)
        {
            if (FirstActivityToday is null)
            {
                parts.Add($"普段は{usualStart:HH\\:mm}ごろには家電が使われています。");
            }
            else if (FirstActivityToday.Value.ToTimeSpan() - usualStart.ToTimeSpan() >= TimeSpan.FromHours(1))
            {
                parts.Add(
                    $"今日はじめて家電が使われたのは{FirstActivityToday.Value:HH\\:mm}で、"
                    + $"普段の{usualStart:HH\\:mm}ごろより遅めでした。");
            }
        }

        // Only worth saying when it adds something the finding did not: the rules
        // already quote the outdoor figure whenever they were the reason it fired.
        if (OutdoorTemperatureC is { } temperature
            && AreaName is { Length: > 0 } area
            && !Risk.Reason.Contains("気温", StringComparison.Ordinal)
            && !Risk.Reason.Contains("暑さ指数", StringComparison.Ordinal))
        {
            parts.Add($"{area}の外気温は{temperature:0.#}℃です。");
        }

        parts.Add(Suggestion switch
        {
            { CanTurnOnRemotely: true } s => $"アプリの「{s.ActionLabel}」から{s.DeviceName}を操作できます。",
            { } s => $"{s.DeviceName}は遠隔操作しない設定です。お電話で声をかけてあげてください。",
            _ => "気になるようでしたら、一度お電話してみてください。"
        });

        return string.Concat(parts);
    }
}

public sealed record WatchAlertOutcome(
    WatchAlertStatus Status,
    RiskResult? Risk,
    string Message,
    LineSendResult? SendResult)
{
    public bool Sent => Status == WatchAlertStatus.Sent;
    public bool Suppressed => Status == WatchAlertStatus.SuppressedByCooldown;
}

/// <summary>
/// Evaluates the current watch/risk status for a household's resident and, when the
/// risk is at or above the configured threshold, pushes a LINE alert to the family
/// group. Sending is deduplicated per person + risk level using a cooldown window
/// persisted as <see cref="WatchAlert"/> rows, so a demo (or an unattended poll) never
/// spams the family group.
/// </summary>
public sealed class WatchAlertService(
    IAppDbContext db,
    ILineMessagingClient line,
    TimeProvider clock,
    WatchAlertSettings settings,
    ILineRecipientResolver recipientResolver,
    IAiRouterClient? ai = null,
    IWeatherAdvisoryProvider? heatAdvisory = null,
    IDisasterAdvisoryProvider? disasterAdvisory = null)
{
    public async Task<WatchAlertOutcome> EvaluateAsync(Guid householdId, CancellationToken ct = default)
    {
        try
        {
            var resident = await db.People
                .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident, ct);

            if (resident is null)
            {
                return new WatchAlertOutcome(WatchAlertStatus.NoResident, null, "本人（Resident）が登録されていません。", null);
            }

            var (today, recent) = await LoadActivityAsync(householdId, ct);
            var nowLocal = HouseholdTime.LocalTime(clock.GetUtcNow());
            var risks = new RiskAssessmentService(db, clock, heatAdvisory);
            var leftOn = await risks.LoadLeftOnAsync(householdId, ct);
            var cooling = await risks.LoadCoolingAsync(householdId, ct);
            var heating = await risks.LoadHeatingAsync(householdId, ct);
            var heat = await risks.GetHeatAsync(ct);
            var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId, ct);
            var cold = await risks.GetColdAsync(household, ct);
            var risk = RiskAssessmentService.Evaluate(
                today, recent, nowLocal, leftOn, null, heat, cooling, cold, heating);

            // The evening nudge is not a risk, so it is sent independently of the
            // threshold below: a changing room can only be warmed *before* the cold
            // morning, and by the time the risk rule can see anything it is already
            // that morning.
            var forecastNotice = await TrySendColdForecastAsync(householdId, resident.Id, nowLocal, risks, ct);

            // Emergency information is likewise not a risk score: 気象庁 issuing a
            // landslide warning says nothing about her kettle, so it cannot be folded
            // into the threshold below without either inflating or hiding it.
            var disasterNotice = await TrySendDisasterNoticeAsync(householdId, resident, today, ct);

            if (risk.Level < settings.Threshold)
            {
                return new WatchAlertOutcome(
                    WatchAlertStatus.BelowThreshold,
                    risk,
                    disasterNotice ?? forecastNotice ?? "現在はリスクが低いため、アラートの送信は不要です。",
                    null);
            }

            var now = clock.GetUtcNow();
            var cooldownStart = now - settings.Cooldown;

            var recentAlert = await db.WatchAlerts
                .Where(a => a.PersonId == resident.Id && a.RiskLevel == risk.Level && a.SentAtUtc >= cooldownStart)
                .OrderByDescending(a => a.SentAtUtc)
                .FirstOrDefaultAsync(ct);

            if (recentAlert is not null)
            {
                return new WatchAlertOutcome(
                    WatchAlertStatus.SuppressedByCooldown,
                    risk,
                    "前回のアラートから間もないため、送信をスキップしました（クールダウン中）。",
                    null);
            }

            var briefing = AlertBriefing.From(
                resident.DisplayName, risk, nowLocal, today, recent, heat, cold,
                ComfortSuggestion.For(heat, cooling, cold, heating));

            var text = await BuildMessageAsync(briefing, ct);
            var recipients = await recipientResolver.ResolveAsync(householdId, ct);
            var card = BuildCard(risk, text);
            var aggregateResult = await PushToAllAsync(recipients, card, ct);

            db.WatchAlerts.Add(new WatchAlert
            {
                HouseholdId = householdId,
                PersonId = resident.Id,
                RiskLevel = risk.Level,
                Score = risk.Score,
                Reason = risk.Reason,
                Message = text,
                SentAtUtc = now,
                Success = aggregateResult.Success,
                Error = aggregateResult.Error
            });
            await db.SaveChangesAsync(ct);

            return aggregateResult.Success
                ? new WatchAlertOutcome(WatchAlertStatus.Sent, risk, text, aggregateResult)
                : new WatchAlertOutcome(WatchAlertStatus.SendFailed, risk, text, aggregateResult);
        }
        catch (Exception ex)
        {
            // Defensive: this service is polled unattended by a background job and is
            // triggered manually from a demo endpoint. It must never throw.
            return new WatchAlertOutcome(WatchAlertStatus.SendFailed, null, $"アラート評価中にエラーが発生しました（{ex.GetType().Name}）。", new LineSendResult(false, ex.GetType().Name));
        }
    }

    /// <summary>
    /// Sends tomorrow morning's cold warning the night before, at most once per
    /// forecast date.
    ///
    /// Heatshock is not something an alert can catch while it is happening -- by the
    /// time the bathroom is cold the resident is already in it. The only useful form
    /// this feature can take is prevention, which means the message has to arrive
    /// while there is still an evening left to act in.
    ///
    /// Deduplication reuses the WatchAlert table with the forecast date written into
    /// Reason. That keeps the notice in the same audit trail the family already sees
    /// and needs no extra table; RiskLevel.Low is used so it can never be mistaken
    /// for -- or collide with the cooldown of -- a real alert.
    /// </summary>
    private async Task<string?> TrySendColdForecastAsync(
        Guid householdId,
        Guid residentId,
        TimeOnly nowLocal,
        RiskAssessmentService risks,
        CancellationToken ct)
    {
        // Outside the evening there is nothing to prepare yet (too early) or no
        // evening left to prepare in (past bedtime).
        if (nowLocal.Hour is < 18 or >= 22)
        {
            return null;
        }

        var forecast = await risks.GetTomorrowColdAsync(ct);
        if (forecast?.Advice is not { } advice)
        {
            return null;
        }

        var marker = $"{ColdForecastReasonPrefix}{forecast.ForDateLocal:yyyy-MM-dd}";
        var alreadySent = await db.WatchAlerts
            .AnyAsync(a => a.PersonId == residentId && a.Reason == marker, ct);

        if (alreadySent)
        {
            return null;
        }

        var text = $"{advice}。明日の朝の最低気温は{forecast.AreaName}で{forecast.MinTemperatureC:0.#}℃の予想です。";
        var recipients = await recipientResolver.ResolveAsync(householdId, ct);
        var origin = settings.PublicBaseUrl.TrimEnd('/');
        var hasOrigin = !string.IsNullOrWhiteSpace(origin)
            && origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var result = await PushToAllAsync(
            recipients,
            new LineAlertCard(
                Title: "明日の朝の冷え込み",
                Text: text,
                RiskLabel: "備えておきましょう",
                ImageUrl: hasOrigin ? $"{origin}{MascotImagePath}" : null,
                LinkUrl: hasOrigin ? origin : null),
            ct);

        db.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = householdId,
            PersonId = residentId,
            RiskLevel = RiskLevel.Low,
            Score = 0,
            Reason = marker,
            Message = text,
            SentAtUtc = clock.GetUtcNow(),
            Success = result.Success,
            Error = result.Error
        });
        await db.SaveChangesAsync(ct);

        // The row is written even when the push failed, so a broken LINE channel
        // cannot turn into the same notice being retried every five minutes all
        // evening.
        return result.Success
            ? $"明日の朝の冷え込みをお知らせしました（{text}）"
            : null;
    }

    private const string ColdForecastReasonPrefix = "翌朝の冷え込み予報 ";

    private const string DisasterReasonPrefix = "防災情報 ";

    /// <summary>
    /// Marker for the escalated form. Kept separate from <see cref="DisasterReasonPrefix"/>
    /// on purpose: the calm notice may already have gone out for this same advisory, and
    /// the household falling dark afterwards is new information that must not be
    /// swallowed by the earlier one's deduplication.
    /// </summary>
    private const string DisasterAwayReasonPrefix = "防災情報 留守の可能性 ";

    /// <summary>
    /// Tells the family that emergency information covers the household's area, and --
    /// this is the point -- pairs it with whether the appliances have been used today.
    ///
    /// <para>
    /// The warning itself is already on every phone in Japan via 緊急速報メール, so simply
    /// repeating it would add nothing. What a family cannot get anywhere else is the
    /// second sentence: 気象庁 says there is a landslide warning over her ward, and the
    /// kettle went on at 07:15. That is the difference between an alarm and an answer,
    /// and it is built only from figures this app already holds.
    /// </para>
    ///
    /// <para>
    /// Deduplicated on the advisory's own identity rather than a cooldown window: a
    /// 土砂災害警戒情報 stays active for hours, and a family should hear about it once.
    /// </para>
    /// </summary>
    private async Task<string?> TrySendDisasterNoticeAsync(
        Guid householdId,
        Person resident,
        DailyActivity today,
        CancellationToken ct)
    {
        if (disasterAdvisory is not { IsConfigured: true })
        {
            return null;
        }

        var active = await disasterAdvisory.GetActiveAsync(ct);
        if (active.Count == 0)
        {
            return null;
        }

        // Only the most serious one is sent. Heavy rain and a landslide warning arrive
        // together by design, and two pushes about the same weather is how a family
        // learns to swipe this app away.
        var advisory = active
            .OrderByDescending(a => Severity(a.Kind))
            .ThenByDescending(a => a.IssuedAtUtc)
            .First();

        var marker = DisasterReasonPrefix + advisory.DedupeKey;
        var alreadySent = await db.WatchAlerts
            .AnyAsync(a => a.PersonId == resident.Id && a.Reason == marker, ct);

        // The house being dark through a warning is the one case worth interrupting a
        // family for, so it is checked before the calm notice's deduplication rather
        // than after it: the quiet may well have started hours into an advisory the
        // family was already told about.
        var away = await DetectAwayAsync(householdId, ct);
        var awayMarker = DisasterAwayReasonPrefix + advisory.DedupeKey;

        if (away is not null)
        {
            var awaySent = await db.WatchAlerts
                .AnyAsync(a => a.PersonId == resident.Id && a.Reason == awayMarker, ct);

            if (!awaySent)
            {
                return await SendDisasterAwayAsync(
                    householdId, resident, advisory, away, awayMarker, ct);
            }
        }

        if (alreadySent)
        {
            return null;
        }

        var headline = advisory.Kind == DisasterKind.Earthquake
            ? $"{advisory.AreaName}を震源とする地震がありました（{advisory.Detail}）。"
            : $"{advisory.AreaName}に{advisory.Headline}が出ています。";

        // Written from the activity figure alone. When nothing has been recorded yet the
        // notice says exactly that -- it must never round "no data" up to "she is fine",
        // nor down to "something has happened".
        var status = today.LastActivityTime is { } last
            ? $"{resident.DisplayName}のお宅では{last:HH\\:mm}に家電の利用がありました。"
            : $"{resident.DisplayName}のお宅では本日まだ家電の利用を確認できていません。";

        var text = headline + status;

        var recipients = await recipientResolver.ResolveAsync(householdId, ct);
        var origin = settings.PublicBaseUrl.TrimEnd('/');
        var hasOrigin = !string.IsNullOrWhiteSpace(origin)
            && origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var result = await PushToAllAsync(
            recipients,
            new LineAlertCard(
                Title: advisory.Kind == DisasterKind.Earthquake ? "地震がありました" : advisory.Headline,
                Text: text,
                RiskLabel: "ご家族の様子を確認しましょう",
                ImageUrl: hasOrigin ? $"{origin}{MascotImagePath}" : null,
                LinkUrl: hasOrigin ? origin : null),
            ct);

        db.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = householdId,
            PersonId = resident.Id,
            RiskLevel = RiskLevel.Low,
            Score = 0,
            Reason = marker,
            Message = text,
            SentAtUtc = clock.GetUtcNow(),
            Success = result.Success,
            Error = result.Error
        });
        await db.SaveChangesAsync(ct);

        return result.Success ? $"防災情報をお知らせしました（{text}）" : null;
    }

    /// <summary>
    /// Reads the last whole hours of electricity and reports them as unusually dark, or
    /// null. Deliberately delegates the judgement to <see cref="RiskAssessmentService"/>:
    /// this service decides who to tell, never what counts as abnormal.
    /// </summary>
    private async Task<QuietSpell?> DetectAwayAsync(Guid householdId, CancellationToken ct)
    {
        var profile = await new ActivityService(db).GetHourlyEnergyAsync(
            householdId, 14, HouseholdTime.LocalDate(clock.GetUtcNow()), ct);
        return RiskAssessmentService.DetectQuiet(profile);
    }

    /// <summary>
    /// The escalated notice: emergency information is out over her area *and* the house
    /// has drawn almost nothing for hours, which most often means nobody is in it.
    ///
    /// <para>
    /// This is the only push in the app that asks the family to act immediately, and it
    /// is worded to be acted on rather than read: what is happening, what the meters
    /// actually show, and the one thing to do about it. Every figure in it comes from
    /// data the app already holds -- the advisory is 気象庁's own wording, and the
    /// percentage is this household's own recent hours against its own fortnight.
    /// </para>
    /// </summary>
    private async Task<string?> SendDisasterAwayAsync(
        Guid householdId,
        Person resident,
        DisasterAdvisory advisory,
        QuietSpell away,
        string marker,
        CancellationToken ct)
    {
        var headline = advisory.Kind == DisasterKind.Earthquake
            ? $"{advisory.AreaName}を震源とする地震がありました（{advisory.Detail}）。"
            : $"{advisory.AreaName}に{advisory.Headline}が出ています。";

        var text = headline
            + $"{resident.DisplayName}のお宅では{away.FromHour}時ごろからの{away.Hours}時間、"
            + $"電気の使用がいつもの{away.PercentOfUsual}％まで落ちています。"
            + "外出されているかもしれません。すぐにご連絡のうえ、安全な場所にいるかご確認ください。";

        var recipients = await recipientResolver.ResolveAsync(householdId, ct);
        var origin = settings.PublicBaseUrl.TrimEnd('/');
        var hasOrigin = !string.IsNullOrWhiteSpace(origin)
            && origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var result = await PushToAllAsync(
            recipients,
            new LineAlertCard(
                Title: advisory.Kind == DisasterKind.Earthquake ? "地震がありました" : advisory.Headline,
                Text: text,
                RiskLabel: "至急ご連絡ください",
                ImageUrl: hasOrigin ? $"{origin}{MascotImagePath}" : null,
                LinkUrl: hasOrigin ? origin : null),
            ct);

        // Recorded as High so the family's own history shows this apart from the calm
        // notice, and so the row cannot be mistaken for the informational one.
        db.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = householdId,
            PersonId = resident.Id,
            RiskLevel = RiskLevel.High,
            Score = 0,
            Reason = marker,
            Message = text,
            SentAtUtc = clock.GetUtcNow(),
            Success = result.Success,
            Error = result.Error
        });
        await db.SaveChangesAsync(ct);

        return result.Success ? $"防災情報と留守の可能性をお知らせしました（{text}）" : null;
    }

    /// <summary>
    /// Which advisory wins when several are active. Ordered by how little time the
    /// household has to act on it, not by how rare it is.
    /// </summary>
    private static int Severity(DisasterKind kind) => kind switch
    {
        DisasterKind.SpecialWarning => 4,
        DisasterKind.Earthquake => 3,
        DisasterKind.Landslide => 2,
        DisasterKind.HeavyRainBand => 1,
        _ => 0
    };

    /// <summary>
    /// Builds the mascot card for an alert. The image is only referenced when a public
    /// origin is configured, so a local run degrades to the same text push as before.
    /// </summary>
    private LineAlertCard BuildCard(RiskResult risk, string text)
    {
        var origin = settings.PublicBaseUrl.TrimEnd('/');
        var hasOrigin = !string.IsNullOrWhiteSpace(origin)
            && origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        return new LineAlertCard(
            Title: "見守りのお知らせ",
            Text: text,
            RiskLabel: RiskLabel(risk.Level),
            ImageUrl: hasOrigin ? $"{origin}{MascotImagePath}" : null,
            LinkUrl: hasOrigin ? origin : null);
    }

    /// <summary>
    /// Mascot artwork sent with every alert. A single image is used for all risk
    /// levels on purpose: the badge already states the level, and swapping the
    /// character's expression per level would make a Medium alert look reassuring
    /// next to a High one, which is exactly the wrong signal at a glance.
    /// </summary>
    private const string MascotImagePath = "/images/mimamo-line-alert.png";

    private static string RiskLabel(RiskLevel level) => level switch
    {
        RiskLevel.High => "至急ご確認ください",
        RiskLevel.Medium => "気にかけてあげてください",
        _ => "見守り中です"
    };

    /// <summary>
    /// Pushes the alert card to every resolved recipient. A failure for one recipient must
    /// never prevent the push to the others; the aggregate is a success if at least one
    /// recipient received it, so a single stale/unfollowed target doesn't mask the alert.
    /// </summary>
    private async Task<LineSendResult> PushToAllAsync(IReadOnlyList<string> recipients, LineAlertCard card, CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return new LineSendResult(false, "LINE 通知先が設定されていません。");
        }

        var errors = new List<string>();
        var anySucceeded = false;

        foreach (var to in recipients)
        {
            try
            {
                var result = await line.PushAlertAsync(to, card, ct);
                if (result.Success)
                {
                    anySucceeded = true;
                }
                else if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    errors.Add(result.Error);
                }
            }
            catch (Exception ex)
            {
                // PushAsync/LineMessagingClient already catches its own network errors, but
                // this is a last-resort guard: one bad recipient must never crash the loop.
                errors.Add(ex.GetType().Name);
            }
        }

        return new LineSendResult(anySucceeded, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>
    /// Longest text accepted from the model.
    ///
    /// <para>
    /// This used to be 120 while the prompt asked for 60, and the prompt won: the model
    /// wrote one short line, which is only ever enough to restate the rule that fired.
    /// The ceiling now matches what the prompt actually asks for -- three short lines --
    /// and still exists for the same reason as before: a reply far over the limit means
    /// the model ignored the instruction, and text that long renders badly in a LINE
    /// push, so it is safer to send the deterministic wording instead.
    /// </para>
    /// </summary>
    private const int MaxAiMessageLength = 220;

    /// <summary>
    /// What the alert has to achieve, written down so the prompt and the fallback are
    /// held to the same standard.
    ///
    /// <para>
    /// The failure this is written against is a message that is accurate and useless.
    /// 「活動量が少なめです」 is true, and leaves the reader with no idea whether to
    /// carry on with their meeting or leave it. Three things fix that, and all three
    /// are facts we already hold: <em>when</em> this was observed, <em>how far from
    /// ordinary</em> it is, and <em>what the reader can do next</em> -- including,
    /// when there is one, the button in the app that fixes it without a phone call.
    /// </para>
    /// </summary>
    private const string AlertPrompt = """
        あなたは高齢者見守りサービス「見守り隊」の通知文を書く日本語アシスタントです。
        離れて暮らすご家族（息子・娘）に送るLINEメッセージを1通書いてください。

        構成（3文、合計140文字程度）:
        1文目 いつ何が起きたか。時刻と、観測された事実を1つ。
        2文目 それが普段と比べてどうか。比較できる数値が資料にある時だけ書く。
              無ければ、外の気温など状況を補う事実を1つ書く。
        3文目 ご家族が次にできること。「アプリでできること」が資料にあればそれを案内し、
              無ければ電話での声かけをすすめる。

        厳守事項:
        - 【資料】に書かれている事実と数値だけを使うこと。書かれていない数値・時刻・家電名を
          決して作らないこと。
        - 資料に無い家電について、電源が入っている／切れていると断定しないこと。
          資料が「確認できません」と述べている事柄は、確認できない旨のまま書くこと。
        - 危険度のスコアや内部用語（High/Medium、リスクレベル等）は書かないこと。
        - 医療的な診断・断定はしないこと。「熱中症です」ではなく「暑さが心配です」と書く。
        - 絵文字、挨拶、前置き、署名は不要。本文のみ。
        - 落ち着いた丁寧な口調で、あおらないこと。ただし、ぼかして安心させようともしないこと。
        - 箇条書きにせず、自然な文章で書くこと。改行は入れないこと。
        """;

    /// <summary>
    /// Produces the alert text. When the AI router is configured the wording is generated so
    /// it reads naturally for the family; the deterministic wording is always used as the
    /// fallback, so alerting never depends on the LLM being reachable.
    /// </summary>
    private async Task<string> BuildMessageAsync(AlertBriefing briefing, CancellationToken ct)
    {
        var fallback = briefing.Compose();

        if (ai is not { IsConfigured: true })
        {
            return fallback;
        }

        try
        {
            var facts = briefing.Facts();

            var messages = new List<AiMessage>
            {
                new("system", AlertPrompt),
                new("user", $"【資料】\n{facts}")
            };

            var completion = await ai.CompleteAsync(messages, "alert-message", jsonMode: false, ct);
            if (!completion.Success)
            {
                return fallback;
            }

            var text = completion.Content.ReplaceLineEndings(" ").Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxAiMessageLength)
            {
                return fallback;
            }

            // A prompt is a request, not a guarantee. The model has been observed turning
            // "冷房機器が未登録のため室内の状況は確認できません" into "エアコン未使用です",
            // which states a measurement we do not have. Reject that rather than send it.
            if (InventsApplianceState(text, briefing.Risk.Reason))
            {
                return fallback;
            }

            // Now that the model is handed real figures, it can also get them wrong -- and
            // a wrong number in an alert is worse than a vague one, because the family acts
            // on it. The same check the assistant's summaries have always had is applied
            // here, against the fact block the model was given.
            return AssistantOrchestrator.InventsNumbers(facts, text) ? fallback : text;
        }
        catch (Exception)
        {
            // An alert must go out even if the router misbehaves in an unexpected way.
            return fallback;
        }
    }

    /// <summary>
    /// Wordings that assert an appliance is not running. We only ever know this when a
    /// household registered the appliance and we read its draw; the rule that fires
    /// without a registered appliance says 「確認できません」 instead.
    /// </summary>
    private static readonly string[] IdleApplianceClaims =
    [
        "未使用", "使っていません", "使われていません", "使用されていません",
        "動いていません", "作動していません", "稼働していません",
        "ついていません", "点いていません", "入っていません",
        "オフのまま", "切れたまま", "停止しています"
    ];

    /// <summary>
    /// True when the generated text claims an appliance is idle but the detected reason
    /// never said so. This is the one hallucination that matters here: a family reading
    /// 「エアコン未使用です」 will believe we measured the air conditioner, and act -- or
    /// fail to act -- on a measurement that does not exist.
    /// </summary>
    internal static bool InventsApplianceState(string text, string? reason)
    {
        // The reason itself established the appliance is idle, so the model is allowed
        // to say it in its own words.
        if (reason is not null
            && IdleApplianceClaims.Any(c => reason.Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        return IdleApplianceClaims.Any(c => text.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>
    /// Loads today's activity plus a 14 day baseline using explicit local dates derived
    /// from <see cref="TimeProvider"/> so the result is deterministic under a fake clock
    /// in tests (ActivityService.GetRecentAsync currently derives "today" from the real
    /// system clock, which would defeat that determinism).
    /// </summary>
    private async Task<(DailyActivity Today, IReadOnlyList<DailyActivity> Recent)> LoadActivityAsync(Guid householdId, CancellationToken ct)
    {
        const int days = 14;
        var activity = new ActivityService(db);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());

        var recent = new List<DailyActivity>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            recent.Add(await activity.GetDailyAsync(householdId, todayDate.AddDays(-offset), ct));
        }

        return (recent[^1], recent);
    }
}

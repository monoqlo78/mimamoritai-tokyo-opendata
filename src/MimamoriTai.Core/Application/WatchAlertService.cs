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

            var text = await BuildMessageAsync(resident.DisplayName, risk, ct);
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
    /// Maximum length accepted from the model. LINE renders long pushes poorly and an
    /// over-long reply is a signal the model ignored the instruction, so we fall back.
    /// </summary>
    private const int MaxAiMessageLength = 120;

    /// <summary>
    /// Produces the alert text. When the AI router is configured the wording is generated so
    /// it reads naturally for the family; the deterministic template is always used as the
    /// fallback, so alerting never depends on the LLM being reachable.
    /// </summary>
    private async Task<string> BuildMessageAsync(string residentName, RiskResult risk, CancellationToken ct)
    {
        var fallback = BuildMessage(residentName, risk);

        if (ai is not { IsConfigured: true })
        {
            return fallback;
        }

        try
        {
            var messages = new List<AiMessage>
            {
                new("system",
                    "あなたは高齢者見守りサービスの通知文を書く日本語アシスタントです。" +
                    "離れて暮らす家族に送るLINEメッセージを1通だけ書いてください。" +
                    "条件: 60文字以内、1行、丁寧で落ち着いた口調、煽らない、断定しない、" +
                    "絵文字と挨拶と前置きは不要、事実と次の行動の提案のみ。" +
                    "**検知内容に書かれていない事実を足さないでください。**" +
                    "特に、検知内容に無い家電の名前や、その電源が入っている・切れているという断定は" +
                    "禁止です。検知内容が「確認できません」と述べている事柄は、" +
                    "確認できない旨のまま書いてください（例:「エアコン未使用です」と言い切るのではなく" +
                    "「室温にお気をつけください」のように書く）。"),
                new("user",
                    $"対象: {residentName}\n" +
                    $"リスク: {risk.Level}（スコア {risk.Score}/100）\n" +
                    $"検知内容: {risk.Reason}")
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
            return InventsApplianceState(text, risk.Reason) ? fallback : text;
        }
        catch (Exception)
        {
            // An alert must go out even if the router misbehaves in an unexpected way.
            return fallback;
        }
    }

    private static string BuildMessage(string residentName, RiskResult risk) =>
        $"{residentName}の見守りアラートです。{risk.Reason}。（スコア {risk.Score}/100）";

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

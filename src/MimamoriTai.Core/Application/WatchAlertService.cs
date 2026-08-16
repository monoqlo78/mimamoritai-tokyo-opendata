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
    /// Absolute https origin of this deployment, e.g. "https://app-mimamoritai-hack.azurewebsites.net".
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
    IHeatAdvisoryProvider? heatAdvisory = null)
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
            var heat = await risks.GetHeatAsync(ct);
            var risk = RiskAssessmentService.Evaluate(today, recent, nowLocal, leftOn, null, heat, cooling);

            if (risk.Level < settings.Threshold)
            {
                return new WatchAlertOutcome(
                    WatchAlertStatus.BelowThreshold,
                    risk,
                    "現在はリスクが低いため、アラートの送信は不要です。",
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
    /// Produces the alert text. When OrcaRouter is configured the wording is generated so
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
                    "絵文字と挨拶と前置きは不要、事実と次の行動の提案のみ。"),
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
            return string.IsNullOrWhiteSpace(text) || text.Length > MaxAiMessageLength ? fallback : text;
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

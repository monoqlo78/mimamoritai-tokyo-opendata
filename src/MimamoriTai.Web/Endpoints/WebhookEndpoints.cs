using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Web.Endpoints;

public static partial class WebhookEndpoints
{
    /// <summary>
    /// Sent on `follow` once the source is already linked (or the demo fallback
    /// resolved a household for it). Explains the six rich-menu buttons in plain
    /// Japanese so an elderly resident can use the bot with a single tap, without
    /// needing to type anything.
    /// </summary>
    private const string WelcomeText =
        "見守り隊へようこそ。画面下のボタンをタップするだけで、家族にかんたんに伝えられます。\n" +
        "「助けて」緊急のとき\n" +
        "「体調が悪い」体調が心配なとき\n" +
        "「大丈夫」元気なとき\n" +
        "「今日の様子」AIが今日の様子を教えます\n" +
        "「家族に連絡」電話してほしいとき\n" +
        "困ったときは、いつでも押してください。";

    /// <summary>
    /// Sent on `follow` for a source that is not yet linked to any household and no
    /// default-household fallback is configured. Directs the user to the Settings
    /// UI's link-code flow instead of silently attaching them to any household.
    /// </summary>
    private const string UnlinkedFollowText =
        "見守り隊へようこそ。ご利用には、ご家族の見守り隊アカウントとの連携が必要です。\n" +
        "Webアプリの「LINE連携設定」画面でコードを発行し、このトークに「連携 123456」のように送ってください。";

    /// <summary>Sent when a non-link-code message arrives from an unlinked source.</summary>
    private const string UnlinkedMessageText =
        "このLINEアカウントは、まだ見守り隊アカウントと連携されていません。\n" +
        "Webアプリの「LINE連携設定」画面でコードを発行し、「連携 123456」のように送ってください。";

    private const string LinkCodeSuccessText =
        "連携が完了しました。これから見守り隊のお知らせをこちらにお届けします。";

    /// <summary>
    /// Deliberately generic: never reveals whether the code was wrong, expired,
    /// already used, or the attempt limit was reached (see LineLinkCodeService).
    /// </summary>
    private const string LinkCodeFailureText =
        "コードが正しくないか、有効期限が切れています。設定画面で新しいコードを発行して、もう一度お試しください。";

    private const string ProcessingTimeoutText =
        "うまくお答えできませんでした。もう一度、短いことばで送ってみてください。\n" +
        "「使い方」と送っていただくと、できることをご案内します。\n" +
        "お急ぎのときは、画面下の「助けて」ボタンを押してください。";

    private static readonly TimeSpan EventProcessingTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Matches a "連携 123456" style message: the literal "連携" followed by optional
    /// whitespace and exactly 6 digits (half- or full-width). Anywhere in the
    /// message, not just at the start, so a leading greeting doesn't break it.
    /// </summary>
    [GeneratedRegex(@"連携[\s　]*([0-9０-９]{6})")]
    private static partial Regex LinkCodeMessagePattern();

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/line", async (
            HttpRequest httpRequest,
            ILineMessagingClient line,
            AssistantOrchestrator orchestrator,
            LinePostbackActionService postbackActions,
            LineLinkCodeService linkCodeService,
            IOptions<LineOptions> lineOptions,
            AppDbContext db,
            HouseholdAccessService householdAccess,
            IDataSourceContext dataSource,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LineWebhook");

            httpRequest.EnableBuffering();
            using var reader = new StreamReader(httpRequest.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(ct);
            httpRequest.Body.Position = 0;

            var signature = httpRequest.Headers[LineSignature.HeaderName].FirstOrDefault();

            // When a channel secret is configured the signature must be valid.
            // Requests that fail verification are dropped without processing.
            if (!line.VerifySignature(rawBody, signature))
            {
                logger.LogWarning("Rejected a LINE webhook request with an invalid or missing signature.");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var allowDefaultFallback = lineOptions.Value.AllowDefaultHouseholdFallback;

            foreach (var evt in ParseEvents(rawBody))
            {
                logger.LogInformation(
                    "Accepted LINE event {EventType} from source type {SourceType}.",
                    evt.Type ?? "unknown",
                    evt.SourceType ?? "unknown");

                // LINE may close the delivery connection before downstream AI work completes.
                // Keep processing bounded, but independent from the request-aborted token so a
                // valid reply token is not lost after the event has already been authenticated.
                using var eventCts = new CancellationTokenSource(EventProcessingTimeout);
                var eventCt = eventCts.Token;

                // The critical fix: a source that already has an active LineRecipient row
                // resolves to THAT household directly, regardless of event type. Only a
                // genuinely unknown/unlinked source ever falls through to
                // ResolveDefaultAsync, and only when the operator has explicitly opted
                // into that (local/demo) behavior via Line:AllowDefaultHouseholdFallback.
                var linkedHouseholdId = await ResolveLinkedHouseholdAsync(db, evt.SourceId, eventCt);

                switch (evt.Type)
                {
                    // "join" is the group-chat counterpart of "follow": it fires when the bot
                    // is invited into a group or multi-person room. Without it, adding the bot
                    // to the family group registered nothing and the group never got an alert
                    // until somebody happened to type into it.
                    case "follow":
                    case "join":
                    {
                        var householdId = linkedHouseholdId
                            ?? (allowDefaultFallback ? await householdAccess.ResolveDefaultAsync(eventCt) : null);

                        if (householdId is { } hid && hid != Guid.Empty)
                        {
                            await UpsertRecipientAsync(db, hid, evt.SourceId, isActive: true, clock, eventCt);
                            if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                            {
                                await ReplyAsync(line, evt.ReplyToken, WelcomeText, logger);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                        {
                            // Unlinked source: never silently attach it to any household.
                            // Point the user at the Settings UI's link-code flow instead.
                            await ReplyAsync(line, evt.ReplyToken, UnlinkedFollowText, logger);
                        }

                        break;
                    }

                    case "unfollow":
                    // "leave" is the group-chat counterpart of "unfollow" (the bot was removed
                    // from the group). It carries no reply token - there is nobody left to reply to.
                    case "leave":
                        // Deactivate wherever this source is *currently* linked. This is
                        // independent of the default-fallback flag: "stop following" always
                        // means "stop notifying this source", never "stop notifying the
                        // default household" (which may not even be this source's household).
                        if (linkedHouseholdId is { } unfollowHouseholdId)
                        {
                            await DeactivateRecipientAsync(db, unfollowHouseholdId, evt.SourceId, eventCt);
                        }

                        break;

                    case "message":
                    {
                        var linkCodeMatch = evt.Text is not null ? LinkCodeMessagePattern().Match(evt.Text) : Match.Empty;
                        if (linkCodeMatch.Success && !string.IsNullOrWhiteSpace(evt.SourceId))
                        {
                            // "連携 123456": redeem the code instead of routing this text
                            // through the assistant. The redemption itself resolves which
                            // household the code belongs to -- no prior household needed.
                            var redeemResult = await linkCodeService.RedeemCodeAsync(
                                linkCodeMatch.Groups[1].Value, evt.SourceId, displayName: null, eventCt);

                            if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                            {
                                var succeeded = redeemResult.Status == LineLinkCodeRedeemStatus.Success;
                                await ReplyAsync(
                                    line,
                                    evt.ReplyToken,
                                    succeeded ? LinkCodeSuccessText : LinkCodeFailureText,
                                    logger,
                                    // Right after linking is the one moment a resident is
                                    // certain to be looking at the chat and has no idea what
                                    // to do next.
                                    succeeded ? LineQuickReplyMenu.Default : null);
                            }

                            break;
                        }

                        var householdId = linkedHouseholdId
                            ?? (allowDefaultFallback ? await householdAccess.ResolveDefaultAsync(eventCt) : null);

                        if (householdId is not { } hid || hid == Guid.Empty)
                        {
                            if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                            {
                                await ReplyAsync(line, evt.ReplyToken, UnlinkedMessageText, logger);
                            }

                            break;
                        }

                        await UpsertRecipientAsync(db, hid, evt.SourceId, isActive: true, clock, eventCt);
                        await ApplyDataSourceAsync(db, dataSource, hid, eventCt);

                        string replyText2;
                        try
                        {
                            var response = await orchestrator.HandleAsync(
                                new AssistantRequest(
                                    hid,
                                    null,
                                    evt.Text ?? string.Empty,
                                    CommandSource.Line),
                                eventCt);
                            replyText2 = response.Reply;
                        }
                        catch (OperationCanceledException) when (eventCts.IsCancellationRequested)
                        {
                            logger.LogWarning("LINE message processing exceeded the configured timeout.");
                            replyText2 = ProcessingTimeoutText;
                        }

                        if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                        {
                            // The answer is where the resident is actually looking, so the
                            // choices ride along with it rather than waiting to be found
                            // in the rich menu.
                            await ReplyAsync(
                                line, evt.ReplyToken, replyText2, logger, LineQuickReplyMenu.Default);
                        }

                        break;
                    }

                    case "postback":
                    {
                        var householdId = linkedHouseholdId
                            ?? (allowDefaultFallback ? await householdAccess.ResolveDefaultAsync(eventCt) : null);

                        if (householdId is not { } hid || hid == Guid.Empty)
                        {
                            // No household to act on for an unlinked source; postbacks
                            // (rich-menu taps) have no unlinked-instruction UX today since
                            // an unlinked source shouldn't see the rich menu at all.
                            break;
                        }

                        await UpsertRecipientAsync(db, hid, evt.SourceId, isActive: true, clock, eventCt);
                        await ApplyDataSourceAsync(db, dataSource, hid, eventCt);

                        if (!string.IsNullOrWhiteSpace(evt.PostbackData))
                        {
                            LinePostbackOutcome outcome;
                            try
                            {
                                outcome = await postbackActions.HandleAsync(
                                    hid, evt.SourceId, evt.PostbackData, eventCt);
                            }
                            catch (OperationCanceledException) when (eventCts.IsCancellationRequested)
                            {
                                logger.LogWarning("LINE postback processing exceeded the configured timeout.");
                                outcome = new LinePostbackOutcome(ProcessingTimeoutText, 0);
                            }

                            if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                            {
                                await ReplyAsync(
                                    line, evt.ReplyToken, outcome.ReplyText, logger, LineQuickReplyMenu.Default);
                            }
                        }

                        break;
                    }
                }
            }

            return Results.Ok();
        }).WithName("LineWebhook").DisableAntiforgery();

        app.MapPost("/webhooks/switchbot", async (
            HttpRequest httpRequest,
            SwitchBotWebhookIngestService ingest,
            IOptions<SwitchBotOptions> switchBotOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SwitchBotWebhook");

            if (!IsSwitchBotCallerAuthorised(httpRequest, switchBotOptions.Value, out var authFailure))
            {
                // 401 before the body is read: an unauthenticated caller must not be able
                // to reach the ingest path at all. Never echo the presented token.
                logger.LogWarning("Rejected a SwitchBot webhook callback: {Reason}", authFailure);
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(httpRequest.Body);
            var body = await reader.ReadToEndAsync(ct);

            SwitchBotWebhookResult result;
            try
            {
                result = await ingest.IngestAsync(body, ct);
            }
            catch (Exception ex)
            {
                // SwitchBot retries on a non-2xx and disables a URL that keeps failing.
                // Losing one callback is better than losing the subscription, so swallow
                // and let the poller cover the gap.
                logger.LogError(ex, "Failed to ingest a SwitchBot webhook callback.");
                return Results.Ok();
            }

            if (!result.Recognised)
            {
                // Normal: the webhook is account-wide and carries devices nobody here owns.
                logger.LogDebug("Ignored a SwitchBot webhook callback for an unknown device.");
            }
            else
            {
                logger.LogInformation(
                    "Ingested a SwitchBot webhook callback. StateChange={State} Reading={Reading}",
                    result.StateChange is not null, result.Reading is not null);
            }

            return Results.Ok();
        }).WithName("SwitchBotWebhook").DisableAntiforgery();

        return app;
    }

    /// <summary>
    /// Header name carrying the SwitchBot webhook shared secret. A query parameter of the
    /// same purpose (<c>?token=</c>) is also accepted, because the SwitchBot console only
    /// lets you configure a callback URL, not custom headers.
    /// </summary>
    internal const string SwitchBotWebhookTokenHeader = "X-Webhook-Token";

    /// <summary>
    /// Fail-closed check for the SwitchBot webhook. Returns false (with a short,
    /// secret-free reason) when no secret is configured, or when the presented value does
    /// not match. Comparison is length-independent and constant-time.
    /// </summary>
    internal static bool IsSwitchBotCallerAuthorised(
        HttpRequest request, SwitchBotOptions options, out string failureReason)
    {
        var expected = options.WebhookSecret;

        if (string.IsNullOrWhiteSpace(expected))
        {
            if (options.AllowUnauthenticatedWebhook)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = "SwitchBot:WebhookSecret is not configured.";
            return false;
        }

        var presented = request.Headers[SwitchBotWebhookTokenHeader].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            presented = request.Query["token"].ToString();
        }

        if (string.IsNullOrEmpty(presented))
        {
            failureReason = "No webhook token was presented.";
            return false;
        }

        // Hash both sides first so FixedTimeEquals sees equal-length inputs and the
        // comparison cannot leak the secret's length.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));

        if (!CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash))
        {
            failureReason = "The presented webhook token did not match.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static async Task ReplyAsync(
        ILineMessagingClient line,
        string replyToken,
        string text,
        ILogger logger,
        IReadOnlyList<LineQuickReply>? quickReplies = null)
    {
        using var replyCts = new CancellationTokenSource(ReplyTimeout);
        var result = quickReplies is { Count: > 0 }
            ? await line.ReplyAsync(replyToken, text, quickReplies, replyCts.Token)
            : await line.ReplyAsync(replyToken, text, replyCts.Token);
        if (result.Success)
        {
            logger.LogInformation("LINE reply API accepted the response.");
        }
        else
        {
            logger.LogWarning("LINE reply API failed with category {ErrorCategory}.", result.Error ?? "unknown");
        }
    }

    /// <summary>
    /// The critical webhook fix: resolves the household of an *already-linked*
    /// source id directly from its active <see cref="LineRecipient"/> row, so a
    /// known source is never routed through <c>ResolveDefaultAsync</c>. Returns null
    /// for a genuinely unknown/unlinked source (or a blank source id).
    /// Internal (rather than private) so LineWebhookEventsTests can regression-test
    /// this resolution directly, without standing up a full HTTP test host.
    /// </summary>
    internal static async Task<Guid?> ResolveLinkedHouseholdAsync(AppDbContext db, string? lineUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineUserId))
        {
            return null;
        }

        return await db.LineRecipients
            .Where(r => r.LineUserId == lineUserId && r.IsActive)
            .OrderByDescending(r => r.LastSeenAt)
            .Select(r => (Guid?)r.HouseholdId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Points the ambient data-source context at the household this event belongs to.
    /// Without this the context keeps its <c>Sample</c> default, so the IDeviceProvider
    /// decorator hands every LINE-originated command to the mock provider and a real
    /// SwitchBot device fails with "未登録の機器です" even though it resolved correctly
    /// from the database. The Blazor read models already do this per unit of work
    /// (see DashboardService); the webhook is just another entry point that must too.
    /// </summary>
    internal static async Task ApplyDataSourceAsync(
        AppDbContext db, IDataSourceContext dataSource, Guid householdId, CancellationToken ct)
    {
        var mode = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => (DataSourceMode?)h.DataSourceMode)
            .FirstOrDefaultAsync(ct);

        if (mode is { } resolved)
        {
            dataSource.Mode = resolved;
            dataSource.HouseholdId = householdId;
        }
    }

    /// <summary>Creates or refreshes a <see cref="LineRecipient"/> row for the given source id.</summary>
    internal static async Task UpsertRecipientAsync(
        AppDbContext db, Guid householdId, string? lineUserId, bool isActive, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineUserId))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var existing = await db.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.LineUserId == lineUserId, ct);

        if (existing is null)
        {
            db.LineRecipients.Add(new LineRecipient
            {
                HouseholdId = householdId,
                LineUserId = lineUserId,
                IsActive = isActive,
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            existing.IsActive = isActive;
            existing.LastSeenAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks a recipient inactive after an `unfollow` event. A no-op if it was never registered.</summary>
    private static async Task DeactivateRecipientAsync(AppDbContext db, Guid householdId, string? lineUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineUserId))
        {
            return;
        }

        var existing = await db.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.LineUserId == lineUserId, ct);

        if (existing is not null)
        {
            existing.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A single LINE webhook event, normalized for both message handling and recipient capture.</summary>
    internal sealed record LineWebhookEvent(
        string Type, string? ReplyToken, string? Text, string? SourceId, string? SourceType, string? PostbackData = null);

    /// <summary>Extracts (replyToken, text) pairs from a LINE webhook body. Kept for backward compatibility.</summary>
    internal static List<(string? ReplyToken, string Text)> ParseTextEvents(string rawBody) =>
        ParseEvents(rawBody)
            .Where(e => e.Type == "message" && !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => (e.ReplyToken, e.Text!))
            .ToList();

    /// <summary>
    /// Parses every event in a LINE webhook body into a small, defensive representation.
    /// Malformed JSON (or any unexpected shape) never throws; it just yields no events.
    /// </summary>
    internal static List<LineWebhookEvent> ParseEvents(string rawBody)
    {
        var result = new List<LineWebhookEvent>();

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var evt in events.EnumerateArray())
            {
                if (!evt.TryGetProperty("type", out var typeElement) || typeElement.GetString() is not { } type)
                {
                    continue;
                }

                var replyToken = evt.TryGetProperty("replyToken", out var tokenElement) ? tokenElement.GetString() : null;
                var (sourceId, sourceType) = ExtractSource(evt);

                string? text = null;
                if (type == "message"
                    && evt.TryGetProperty("message", out var message)
                    && message.TryGetProperty("type", out var messageType)
                    && messageType.GetString() == "text"
                    && message.TryGetProperty("text", out var textElement))
                {
                    text = textElement.GetString();
                }

                // Only "message" events require text; "follow"/"unfollow" carry no message body.
                if (type == "message" && string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string? postbackData = null;
                if (type == "postback"
                    && evt.TryGetProperty("postback", out var postback)
                    && postback.TryGetProperty("data", out var dataElement))
                {
                    postbackData = dataElement.GetString();
                }

                result.Add(new LineWebhookEvent(type, replyToken, text, sourceId, sourceType, postbackData));
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// Resolves the id used as the LINE push `to` value: `groupId` / `roomId` for a group or
    /// multi-person chat, otherwise the 1:1 `userId`.
    /// </summary>
    /// <remarks>
    /// The group id must win when present. LINE sends BOTH `groupId` and the speaking
    /// member's `userId` on a group message, so preferring `userId` registered the family
    /// member who happened to speak first instead of the group itself - every later alert
    /// then went to that one person privately and the rest of the family saw nothing.
    /// </remarks>
    private static (string? SourceId, string? SourceType) ExtractSource(JsonElement evt)
    {
        if (!evt.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var sourceType = source.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (source.TryGetProperty("groupId", out var groupIdElement)
            && groupIdElement.GetString() is { Length: > 0 } groupId)
        {
            return (groupId, sourceType);
        }

        if (source.TryGetProperty("roomId", out var roomIdElement)
            && roomIdElement.GetString() is { Length: > 0 } roomId)
        {
            return (roomId, sourceType);
        }

        if (source.TryGetProperty("userId", out var userIdElement)
            && userIdElement.GetString() is { Length: > 0 } userId)
        {
            return (userId, sourceType);
        }

        return (null, sourceType);
    }
}

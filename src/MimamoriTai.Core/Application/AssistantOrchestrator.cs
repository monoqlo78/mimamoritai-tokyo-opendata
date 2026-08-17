using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record AssistantRequest(
    Guid HouseholdId,
    Guid? PersonId,
    string Message,
    CommandSource Source);

public sealed record AssistantResponse(
    string Reply,
    AssistantIntent Intent,
    string ResolvedModel,
    string Router,
    bool DeviceChanged,
    Guid? DeviceId,
    bool AwaitingConfirmation = false);

/// <summary>
/// Single entry point for every natural language message, no matter whether it
/// arrives from the Blazor UI, the API or the LINE webhook.
/// </summary>
public sealed class AssistantOrchestrator(
    IAppDbContext db,
    IAiRouterClient ai,
    IDeviceProvider deviceProvider,
    IFabricDataAgentClient fabric,
    ILocalDataQuestionService localData,
    TimeProvider clock,
    IPendingActionStore? pendingActions = null,
    TimeSpan? fabricBudget = null,
    IGuardedActionNotifier? guardedNotifier = null)
{
    private readonly IPendingActionStore _pending = pendingActions ?? new InMemoryPendingActionStore();

    /// <summary>
    /// How long the Fabric Data Agent is allowed to take before the query path gives
    /// up on it and answers from the local database instead.
    ///
    /// Fabric is an enhancement over local data, never a prerequisite: the local
    /// answer is already complete before Fabric is consulted. Measured against the
    /// live workspace a single AskAsync takes ~19s and is then rejected anyway
    /// (the data agent cannot reach its datasource), while the LINE webhook cancels
    /// the whole event after 8s. Left unbounded that turns a perfectly good local
    /// answer into the generic timeout message.
    /// </summary>
    private readonly TimeSpan _fabricBudget = fabricBudget ?? TimeSpan.FromSeconds(4);

    /// <summary>
    /// Visible to the test project so the accuracy harness measures the prompt that
    /// actually ships, not a copy of it (see IntentEvaluationTests).
    /// </summary>
    internal const string SystemPrompt = """
        あなたは高齢者見守りサービス「見守り隊 / CareRoute AI」の意図解析エンジンです。
        ユーザーの日本語メッセージを、次のJSONだけで返してください。前後に文章やコードフェンスを付けないこと。

        {
          "intent": "control_device | device_status | query_data | conversation",
          "topic": "faq | general | expert | emergency",
          "deviceAlias": "文字列 または null",
          "action": "turn_on | turn_off | toggle | get_status | null",
          "scope": "recent | analysis",
          "confidence": 0.0,
          "question": "文字列 または null"
        }

        判定基準:
        - 家電を操作したい -> control_device
        - 家電の状態を知りたい -> device_status (action は get_status)
        - 生活データ・様子・活動時間の質問 -> query_data (question に質問文)
        - それ以外の会話 -> conversation
        - 機器が特定できない場合 deviceAlias は null にし、推測しないこと。
        - confidence は 0.0〜1.0 の確信度。

        topic の判定 (intent が conversation のときだけ意味を持つ。それ以外は "general" でよい):
        - "faq" = 見守り隊というサービス自体の使い方・仕組み・不安について尋ねている。
          例:「家族の追加方法は」「通知が来ない」「カメラで見られてる?」「これは何のアプリ?」
        - "expert" = 専門家の判断が要る。健康・症状・薬・医療・介護認定・お金・年金・相続・法律。
          例:「この薬と一緒に飲んでいい?」「要介護の申請はどうすれば」「年金はいくらもらえる?」
          少しでも当てはまるなら "expert" にすること。断定して答えてよい話ではない。
        - "emergency" = 今この人の体に起きていることを訴えている。
          例:「胸が痛い」「息ができない」「転んで動けない」
        - "general" = 上のどれでもない、ふつうの会話や一般常識の質問。
          例:「おはよう」「今日は寒いね」「ありがとう」「桜はいつ咲く?」

        scope の判定 (query_data のときのみ意味を持つ):
        - "recent" = 今・今日・直近の状態を尋ねている。
          例:「今どうしてる」「今日の様子は」「変わりない?」「起きてる?」
        - "analysis" = 複数日をまたぐ比較・傾向・集計・原因の分析を求めている。
          例:「先週と比べてどう」「今月の平均は」「最近増えている?」「いつも何時に寝ている」
        - 迷った場合は "recent" にすること。
        """;

    private const string RepairPrompt = "JSONとして解析できませんでした。指定したスキーマのJSONオブジェクトのみを、余計な文字なしで返してください。";

    /// <summary>
    /// Turns the raw data-agent / local-database answer into something a worried
    /// family member actually wants to read. Deliberately forbids inventing numbers:
    /// the figures must come from the supplied facts only.
    /// </summary>
    private const string SummaryPrompt = """
        あなたは高齢者見守りサービス「見守り隊」のアシスタントです。
        ご家族（離れて暮らす息子・娘）に向けて、データの要約をやさしい日本語で伝えてください。

        ルール:
        - 与えられた「データ」に書かれている事実だけを使い、数値や時刻を創作しないこと。
        - 状況は「どの家電の電源が入っている／切れているか」と「電力の使いかたが普段と比べて
          どうか」の2点で説明すること。これがご家族の知りたいことである。
        - 家電の利用「回数」は答えに含めないこと。回数は機器に問い合わせた回数に左右される
          数字で、暮らしぶりを表さない。ご家族が回数そのものを尋ねた場合のみ答えてよい。
        - 家電の「台数」は、データに台数が明記されている場合のみ答えること。利用回数など別の
          数値から台数を推測してはならない。明記が無ければ台数には触れないこと。
        - データに数値が書かれている場合は「記録がありません」と答えてはならない。
        - 「[端末の記録から確認できる事実]」が付いている情報は最も信頼できる情報として優先すること。
        - データの一部に「取得できなかった」「技術的な問題」など、集計側の不調を述べる記述が
          混じっていても、それは家族には伝えず無視し、確認できた事実だけを伝えること。
        - データに「記録がありません」「利用がありません」とある場合は、それを事実としてそのまま
          やさしく伝えること。機器の故障・通信エラー・システムの不具合だと決めつけないこと。
        - 2〜3文、120文字程度。専門用語や英語は使わない。
        - 落ち着いた、安心できる語り口にする。過度に不安をあおらない。
        - 心配な兆候がある場合は、最後にひと言だけやさしく声かけを提案する。
        - 箇条書きにせず、自然な文章で書くこと。
        """;

    public async Task<AssistantResponse> HandleAsync(AssistantRequest request, CancellationToken ct = default)
    {
        // A pending proposal is answered before anything is sent to the model: "はい" on
        // its own carries no intent, and re-parsing it would lose the action it refers to.
        var confirmation = await TryResolveConfirmationAsync(request, ct);
        if (confirmation is not null)
        {
            return confirmation;
        }

        // Someone describing chest pain must not wait ~1.7s for intent classification and
        // must never be answered with small talk, so this is decided before any model call
        // and before the knowledge base.
        if (AssistantKnowledgeBase.IsUrgent(request.Message))
        {
            return await ReplyWithoutModelAsync(request, AssistantKnowledgeBase.UrgentReply, ct);
        }

        // "この薬と一緒に飲んでいい?" must not reach a model that would answer it. Decided
        // by keyword before the router so the refusal survives an outage too.
        //
        // This runs *before* the FAQ pass, not after. Both are deterministic and free, so
        // the order is purely a question of which layer should win a tie -- and a question
        // for a professional is the one class where answering from the wrong layer does
        // harm. 「施設の費用の相場はいくら」 used to be caught by the pricing FAQ and told
        // "このLINEのやりとりに、お金はかかりません", which answers a question nobody asked.
        if (AssistantExpertGuidance.TryRefer(request.Message) is { } referral)
        {
            return await ReplyWithoutModelAsync(request, referral.Reply, ct);
        }

        // Product questions ("家族の追加方法は") are answered from the knowledge base with no
        // model call at all. Only wording that cannot also be a device command or a question
        // about the resident's day is allowed to answer this early -- see FaqEntry.PreIntent.
        //
        // The deterministic layers run *below* the language-model router, not instead of it:
        // whatever is answered here costs zero round trips and keeps working while the router
        // is down, and only what is left over is classified by the model.
        if (AssistantKnowledgeBase.TryAnswer(request.Message, FaqMatchMode.Strict) is { } known)
        {
            return await ReplyWithoutModelAsync(request, known.Reply, ct);
        }

        var aliasHint = await BuildAliasHintAsync(request.HouseholdId, ct);

        var messages = new List<AiMessage>
        {
            AiMessage.System(SystemPrompt),
            AiMessage.System($"登録済みの機器: {aliasHint}"),
            AiMessage.User(request.Message)
        };

        var completion = await ai.CompleteAsync(messages, "intent", jsonMode: true, ct);
        await LogAiAsync(request.HouseholdId, "intent", completion, ct);

        var plan = IntentParser.TryParse(completion.Content);

        // One — and only one — repair attempt when the model returns unusable JSON.
        if (plan is null && completion.Success)
        {
            var retryMessages = new List<AiMessage>(messages)
            {
                AiMessage.Assistant(completion.Content),
                AiMessage.User(RepairPrompt)
            };

            var retry = await ai.CompleteAsync(retryMessages, "intent-repair", jsonMode: true, ct);
            await LogAiAsync(request.HouseholdId, "intent-repair", retry, ct);
            plan = IntentParser.TryParse(retry.Content);
            completion = retry;
        }

        if (plan is null)
        {
            // The router is unreachable or returned nonsense. Common questions still have to
            // work, so the knowledge base gets its loose pass here rather than nothing at all.
            if (AssistantKnowledgeBase.TryAnswer(request.Message, FaqMatchMode.Loose) is { } fallback)
            {
                return await ReplyWithoutModelAsync(request, fallback.Reply, ct);
            }

            return new AssistantResponse(
                "うまく聞き取れませんでした。もう一度、機器の名前やご質問を具体的に教えてください。",
                AssistantIntent.Conversation,
                completion.ResolvedModel,
                completion.Router,
                false,
                null);
        }

        await RecordMessageAsync(request, MessageType.Text, request.Message, ct);

        var response = plan.Intent switch
        {
            AssistantIntent.ControlDevice or AssistantIntent.DeviceStatus =>
                await HandleDeviceAsync(request, plan, completion, ct),
            AssistantIntent.QueryData => await HandleQueryAsync(request, plan, completion, ct),
            _ => await HandleConversationAsync(request, plan, ct)
        };

        await RecordMessageAsync(request, MessageType.AiReply, response.Reply, ct, isAi: true);
        return response;
    }

    private async Task<AssistantResponse> HandleDeviceAsync(
        AssistantRequest request, AssistantPlan plan, AiCompletionResult completion, CancellationToken ct)
    {
        var action = plan.Intent == AssistantIntent.DeviceStatus
            ? DeviceAction.GetStatus
            : plan.Action ?? DeviceAction.GetStatus;

        // Anything that physically changes the home is proposed first and executed only
        // after the family says yes, so a misread message cannot act on its own.
        if (DeviceSafetyPolicy.IsStateChanging(action))
        {
            var proposal = await ProposeAsync(request, plan, action, completion, ct);
            if (proposal is not null)
            {
                return proposal;
            }
        }

        return await ExecuteDeviceAsync(
            request, plan.DeviceAlias, action, plan.Confidence, request.Message, plan.Intent, completion, ct);
    }

    private async Task<AssistantResponse> ExecuteDeviceAsync(
        AssistantRequest request,
        string? alias,
        DeviceAction action,
        double confidence,
        string originalText,
        AssistantIntent intent,
        AiCompletionResult completion,
        CancellationToken ct,
        bool hazardAcknowledged = false)
    {
        var control = new DeviceControlService(db, deviceProvider, clock, guardedNotifier);
        var outcome = await control.ExecuteAsync(
            request.HouseholdId,
            alias,
            action,
            confidence,
            originalText,
            request.Source,
            request.PersonId,
            completion.ResolvedModel,
            ct,
            hazardAcknowledged);

        return new AssistantResponse(
            outcome.Message,
            intent,
            completion.ResolvedModel,
            completion.Router,
            outcome.Executed && DeviceSafetyPolicy.IsStateChanging(action),
            outcome.DeviceId);
    }

    /// <summary>
    /// Turns a state-changing plan into a confirmation question. Returns null when the
    /// request should just run: an unresolvable or unsafe device is better reported by
    /// the control service, which produces the precise reason and audits the attempt.
    /// </summary>
    private async Task<AssistantResponse?> ProposeAsync(
        AssistantRequest request,
        AssistantPlan plan,
        DeviceAction action,
        AiCompletionResult completion,
        CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == request.HouseholdId)
            .ToListAsync(ct);

        var matches = DeviceResolver.Resolve(devices, plan.DeviceAlias);
        if (matches.Count != 1)
        {
            return null;
        }

        var device = matches[0];
        var verdict = DeviceSafetyPolicy.Evaluate(device, action, plan.Confidence);

        // A refusal is left to the control service, which words it precisely and audits
        // the attempt. Only something we would actually carry out is worth confirming.
        if (verdict.Decision == SafetyDecision.Deny)
        {
            return null;
        }

        _pending.Set(new PendingDeviceAction(
            request.HouseholdId,
            plan.DeviceAlias ?? device.Alias,
            device.DisplayName,
            action,
            request.Message,
            clock.GetUtcNow(),
            verdict.NeedsHazardCheck));

        var verb = action switch
        {
            DeviceAction.TurnOn => "つけます",
            DeviceAction.TurnOff => "消します",
            _ => "切り替えます"
        };

        // For a heating appliance the question is not "shall I?" but "is it safe to?".
        // The checks are spelled out so the person answering knows what they are vouching
        // for, and so that a yes is a considered answer rather than a reflex.
        var prompt = verdict.NeedsHazardCheck
            ? string.Join(
                "\n",
                [
                    $"{device.DisplayName} を{verb}。{verdict.Reason}",
                    .. (verdict.HazardChecks ?? []).Select(c => $"・{c}"),
                    "確認できたら「はい」、やめる場合は「いいえ」と送ってください。実行するとご家族全員にお知らせが届きます。"
                ])
            : $"{device.DisplayName} を{verb}。よろしいですか？（「はい」で実行、「いいえ」で中止）";

        return new AssistantResponse(
            prompt,
            plan.Intent,
            completion.ResolvedModel,
            completion.Router,
            false,
            device.Id,
            AwaitingConfirmation: true);
    }

    /// <summary>
    /// Consumes a yes/no answer to a pending proposal. Returns null when there is nothing
    /// pending, or when the message is not a yes/no, in which case it is a fresh instruction.
    /// </summary>
    private async Task<AssistantResponse?> TryResolveConfirmationAsync(AssistantRequest request, CancellationToken ct)
    {
        var answer = ConfirmationReply.Interpret(request.Message);
        if (answer is null)
        {
            return null;
        }

        var pending = _pending.Take(request.HouseholdId, clock.GetUtcNow());
        if (pending is null)
        {
            return null;
        }

        await RecordMessageAsync(request, MessageType.Text, request.Message, ct);

        // Confirmation is explicit human consent, so the model's original confidence
        // no longer gates it; every other safety check still runs in the control service.
        var completion = new AiCompletionResult(true, string.Empty, "Confirmation", "confirmation/none", 0);

        var response = answer.Value
            ? await ExecuteDeviceAsync(
                request, pending.DeviceAlias, pending.Action, 1.0, pending.OriginalText,
                AssistantIntent.ControlDevice, completion, ct, pending.RequiresHazardAcknowledgement)
            : new AssistantResponse(
                $"{pending.DeviceName} の操作を中止しました。",
                AssistantIntent.ControlDevice,
                completion.ResolvedModel,
                completion.Router,
                false,
                null);

        await RecordMessageAsync(request, MessageType.AiReply, response.Reply, ct, isAi: true);
        return response;
    }

    private async Task<AssistantResponse> HandleQueryAsync(
        AssistantRequest request, AssistantPlan plan, AiCompletionResult completion, CancellationToken ct)
    {
        var question = string.IsNullOrWhiteSpace(plan.Question) ? request.Message : plan.Question;

        // The local database is always consulted, even when Fabric is available.
        //
        // The Fabric Data Agent answers in free text and, when it cannot reach its
        // datasource, apologises with HTTP 200 instead of failing. FabricDataAgentMcpClient
        // catches the common wordings, but a phrase list can never cover everything a
        // language model might say. Carrying the local facts alongside means a missed
        // apology degrades the answer instead of erasing it: the summary still has real
        // times and counts to work from.
        var local = await localData.AnswerAsync(request.HouseholdId, question, ct);
        var answer = local;

        // Fabric is consulted only for questions the local database cannot answer well:
        // comparisons, trends and aggregates spanning days. For "今どうしてる" the local
        // answer is already complete and correct, so paying seconds for an enrichment
        // that adds nothing is pure latency. The scope comes from the intent call that
        // has already happened, so this costs no extra model round trip.
        if (fabric.IsConfigured && plan.Scope == QueryScope.Analysis)
        {
            var remote = await TryAskFabricAsync(question, ct);
            if (remote is { Success: true } && !string.IsNullOrWhiteSpace(remote.Answer))
            {
                answer = new FabricAnswer(true, Merge(remote.Answer, local.Answer), remote.Source, null);
            }
        }

        var (reply, summary) = await SummarizeAsync(request, question, answer, ct);

        return new AssistantResponse(
            reply,
            AssistantIntent.QueryData,
            summary?.ResolvedModel ?? completion.ResolvedModel,
            summary?.Router ?? completion.Router,
            false,
            null);
    }

    /// <summary>
    /// Wrapped around the family's question before it reaches the Fabric Data Agent.
    ///
    /// The data agent is a datasource, not the voice of the product. Left to itself it
    /// writes its own family-facing summary -- and because a usage count is the easiest
    /// figure in the warehouse to reach for, that summary kept coming back as "家電を◯回
    /// 利用しています", which then survived into the reply. So it is asked for the
    /// measurements only; the wording, the judgement and the reassurance are decided here,
    /// from the same facts, by the model the router selected.
    /// </summary>
    private const string FactsOnlyPreamble = """
        以下の質問に対して、集計されたファクト（数値・時刻・状態）だけを簡潔に列挙してください。
        家族向けの要約文、感想、助言、呼びかけは書かないでください。
        電源のON/OFFの状態と、電力量（Wh）およびその平常時との差が分かる場合は必ず含めてください。
        家電の利用回数は、質問が回数そのものを尋ねている場合を除き、含めないでください。

        質問:
        """;

    /// <summary>
    /// Consults the Fabric Data Agent without letting it take the answer down with it.
    ///
    /// By the time this runs the local database has already produced a complete answer,
    /// so anything Fabric does beyond enriching it is pure downside. A data agent that
    /// is slow, throwing or unreachable must therefore degrade to that local answer
    /// rather than propagate: the family gets real times and counts instead of an
    /// error, and callers with their own deadline (the LINE webhook cancels an event
    /// after 8 seconds) still get a reply within it.
    ///
    /// Cancellation requested by the caller is deliberately NOT swallowed -- that means
    /// the caller has given up on the whole request, not just on Fabric.
    /// </summary>
    private async Task<FabricAnswer?> TryAskFabricAsync(string question, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_fabricBudget);

        try
        {
            return await fabric.AskAsync($"{FactsOnlyPreamble}\n{question}", budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget elapsed: fall through to the local answer.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Presents the Fabric answer and the locally computed facts as two labelled
    /// sources so the model can reconcile them, rather than silently preferring one.
    /// </summary>
    private static string Merge(string remote, string local)
    {
        var localFacts = local?.Trim() ?? string.Empty;

        return localFacts.Length == 0
            ? remote.Trim()
            : $"{remote.Trim()}\n\n[端末の記録から確認できる事実]\n{localFacts}";
    }

    /// <summary>
    /// Rewrites a factual data answer as a gentle, family-facing Japanese summary.
    ///
    /// The raw answer is always kept as the fallback: if the router is unavailable,
    /// throttled or returns nothing usable, the user still gets the correct facts
    /// rather than an error, which is why this never throws.
    /// </summary>
    private async Task<(string Reply, AiCompletionResult? Completion)> SummarizeAsync(
        AssistantRequest request, string question, FabricAnswer answer, CancellationToken ct)
    {
        var facts = answer.Answer?.Trim() ?? string.Empty;

        if (!answer.Success || facts.Length == 0)
        {
            return (string.IsNullOrEmpty(facts) ? "データを取得できませんでした。少し時間をおいて試してください。" : facts, null);
        }

        var messages = new List<AiMessage>
        {
            AiMessage.System(SummaryPrompt),
            AiMessage.User($"ご家族からの質問: {question}\n\nデータ({answer.Source}):\n{facts}")
        };

        var purpose = SummaryPurpose(request.Source);
        var summary = await ai.CompleteAsync(messages, purpose, jsonMode: false, ct);
        await LogAiAsync(request.HouseholdId, purpose, summary, ct);

        var text = summary.Success ? summary.Content.Trim() : string.Empty;

        // Never let the model replace the facts with nothing.
        if (text.Length == 0)
        {
            return (facts, summary);
        }

        // A summary that states a number the data never contained is worse than no
        // summary at all: the family acts on it. Smaller models do invent counts here
        // ("1回" arriving as "4回"), and no amount of prompting removes that entirely,
        // so the claim is checked against the source before it is allowed out.
        if (InventsNumbers(facts, text))
        {
            return (facts, summary);
        }

        return (text, summary);
    }

    /// <summary>
    /// Marks the summary request as deadline-bound when it came from LINE.
    ///
    /// LINE cancels an event after 8 seconds, so that path needs a bounded latency.
    /// Every other entry point (web UI, API) has no such limit and lets the router
    /// take as long as the best model needs, which is the whole point of routing
    /// through Azure Model Router and is surfaced to the user as the resolved model
    /// name. The suffix is interpreted by AzureModelRouterOptions.ResolveTimeout; a
    /// client that has no separate fast budget simply ignores it.
    /// </summary>
    private static string SummaryPurpose(CommandSource source) =>
        source == CommandSource.Line ? "summary-fast" : "summary";

    /// <summary>
    /// True when <paramref name="summary"/> asserts a figure that does not appear in
    /// <paramref name="facts"/>. Times are compared whole (14:45 must not be satisfied
    /// by an unrelated "45"), and a rounded figure is accepted -- "約11時間半" from
    /// "11.5時間" is a reasonable retelling, "4回" from "1回" is not.
    /// </summary>
    internal static bool InventsNumbers(string facts, string summary)
    {
        var allowed = NumberPattern.Matches(facts)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        // "16:45" is one token, but a natural retelling writes it as "16時45分". Both
        // halves therefore have to count as supported, or correct summaries get thrown
        // away for saying the same time in Japanese.
        foreach (var part in allowed.Where(a => a.Contains(':')).SelectMany(a => a.Split(':')).ToList())
        {
            allowed.Add(part);
            allowed.Add(part.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0");
        }

        foreach (Match m in NumberPattern.Matches(summary))
        {
            if (allowed.Contains(m.Value))
            {
                continue;
            }

            // Accept a value the source also supports at lower precision, so that
            // rounding for readability is not treated as invention.
            if (double.TryParse(m.Value, out var claimed)
                && allowed.Any(a => double.TryParse(a, out var source) && IsRoundingOf(source, claimed)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsRoundingOf(double source, double claimed)
    {
        if (Math.Abs(source - claimed) < 0.0001)
        {
            return true;
        }

        // Within 5% covers "約33ワット" for 32.7W but never turns 1 into 4.
        var scale = Math.Max(Math.Abs(source), 1.0);
        return Math.Abs(source - claimed) / scale <= 0.05;
    }

    /// <summary>
    /// Matches a clock time as one token and any other run of digits (with an optional
    /// decimal part) as another, so the two are never confused for one another.
    /// </summary>
    private static readonly Regex NumberPattern =
        new(@"\d{1,2}:\d{2}|\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// System prompt for messages the knowledge base did not recognise. The product facts
    /// are supplied rather than trusted to the model's memory: asked to explain 見守り隊
    /// unaided, a model invents menus that do not exist, and an 85 year old following a
    /// made-up instruction is worse off than one told "分かりません".
    /// </summary>
    private static readonly string ConversationPrompt = $"""
        あなたは高齢者見守りサービス「見守り隊」のやさしいアシスタントです。
        相手はご高齢の方です。次のとおりに答えてください。

        - 日本語で、2〜3文まで。短く、やさしく。
        - むずかしい言葉・カタカナ語・英語・専門用語を使わない。
        - 手順を伝えるときだけ、番号付きで3つまで。
        - 不安な気持ちは否定せず、まず受け止めてから、事実で安心してもらう。
        - 下の「事実として正しいこと」に書かれていないことは、絶対に作らないこと。

        {AssistantKnowledgeBase.ProductFacts}
        """;

    /// <summary>
    /// Used when the router called the message ordinary conversation rather than a question
    /// about the product. General knowledge is allowed here — refusing to say that cherry
    /// blossoms bloom in spring is not caution, it is a broken assistant — but the product
    /// facts are still supplied so a stray question about 見守り隊 cannot be improvised, and
    /// anything a professional owns is handed back for the referral path to answer.
    /// </summary>
    private static readonly string GeneralPrompt = $"""
        あなたは高齢者見守りサービス「見守り隊」のやさしい話し相手です。
        相手はご高齢の方です。次のとおりに答えてください。

        - 日本語で、2〜3文まで。短く、やさしく。
        - むずかしい言葉・カタカナ語・英語・専門用語を使わない。
        - ふつうの世間話や一般常識の質問には、ふつうに答えてよい。
        - ただし、健康・症状・薬・介護の手続き・お金・法律の判断は、絶対に自分で答えないこと。
          その場合は「わたしからはお答えできません」と伝え、お医者さんやご家族に相談するよう案内する。
        - 見守り隊のことは、下の「事実として正しいこと」に書かれていることだけを使う。
          書かれていない画面名・ボタン名・手順は、絶対に作らないこと。

        {AssistantKnowledgeBase.ProductFacts}
        """;

    /// <summary>
    /// Second stage of the router: the specialist that answers, chosen from the topic the
    /// first stage returned on the same JSON.
    ///
    /// The order is deliberate. Everything that can be answered without a model is tried
    /// first, so the common cases stay at one round trip in total (the classification that
    /// already happened) and the 8s LINE budget is never spent twice.
    /// </summary>
    private async Task<AssistantResponse> HandleConversationAsync(
        AssistantRequest request, AssistantPlan plan, CancellationToken ct)
    {
        // The keyword check before the router did not fire, but the model recognised the
        // question as one a professional owns. Answered from the fixed text, not by a model.
        if (plan.Topic == AssistantTopic.Expert)
        {
            var referral = AssistantExpertGuidance.TryRefer(request.Message) ?? AssistantExpertGuidance.General;
            return KnowledgeBaseResponse(referral.Reply);
        }

        // Urgency is normally decided by keyword before any model runs. This catches the
        // phrasings that list did not anticipate, and costs nothing extra.
        if (plan.Topic == AssistantTopic.Emergency)
        {
            return KnowledgeBaseResponse(AssistantKnowledgeBase.UrgentReply);
        }

        // Second pass: the model has already called this small talk, so single keywords
        // ("さみしい", "痛い") can answer without risking a device command or data question.
        // This also carries the whole path when the router is down.
        var known = AssistantKnowledgeBase.TryAnswer(request.Message, FaqMatchMode.Loose);
        if (known is not null)
        {
            return KnowledgeBaseResponse(known.Reply);
        }

        // A product question no rule anticipated stays pinned to the product facts;
        // ordinary conversation is allowed to use general knowledge.
        var prompt = plan.Topic == AssistantTopic.Faq ? ConversationPrompt : GeneralPrompt;

        var messages = new List<AiMessage>
        {
            AiMessage.System(prompt),
            AiMessage.User(request.Message)
        };

        var purpose = ConversationPurpose(request.Source);
        var reply = await ai.CompleteAsync(messages, purpose, jsonMode: false, ct);
        await LogAiAsync(request.HouseholdId, purpose, reply, ct);

        // When the model gives nothing back, say so honestly and offer the one command that
        // always works, rather than the old "承知しました" -- which answered no question at all.
        var text = reply.Success && !string.IsNullOrWhiteSpace(reply.Content)
            ? reply.Content.Trim()
            : NoAnswerText;

        return new AssistantResponse(
            text,
            AssistantIntent.Conversation,
            reply.ResolvedModel,
            reply.Router,
            false,
            null);
    }

    /// <summary>An answer that came from fixed text rather than from a model.</summary>
    private static AssistantResponse KnowledgeBaseResponse(string reply) =>
        new(reply, AssistantIntent.Conversation, KnowledgeBaseModel, KnowledgeBaseRouter, false, null);

    /// <summary>
    /// Marks small talk as deadline-bound when it came from LINE, exactly as
    /// <see cref="SummaryPurpose"/> does for summaries.
    ///
    /// Measured on the live router, small talk is routinely sent to reasoning models that
    /// think for 5-13 seconds. Added to the ~1.7s intent call, that exceeds the webhook's
    /// 8s budget, which is what turned every question into the generic
    /// "しばらくたってからお試しください" message. The web UI keeps the full budget: it has
    /// no deadline, and showing which model answered is the point.
    /// </summary>
    private static string ConversationPurpose(CommandSource source) =>
        source == CommandSource.Line ? "conversation-fast" : "conversation";

    /// <summary>
    /// Records and returns an answer that required no model at all. Kept on the same
    /// bookkeeping path as a model answer so the conversation history stays complete.
    /// </summary>
    private async Task<AssistantResponse> ReplyWithoutModelAsync(
        AssistantRequest request, string reply, CancellationToken ct)
    {
        await RecordMessageAsync(request, MessageType.Text, request.Message, ct);
        await RecordMessageAsync(request, MessageType.AiReply, reply, ct, isAi: true);

        return new AssistantResponse(
            reply,
            AssistantIntent.Conversation,
            KnowledgeBaseModel,
            KnowledgeBaseRouter,
            false,
            null);
    }

    /// <summary>Reported instead of a model name when the answer came from the knowledge base.</summary>
    internal const string KnowledgeBaseRouter = "KnowledgeBase";

    internal const string KnowledgeBaseModel = "knowledge-base";

    private const string NoAnswerText =
        "うまくお答えできませんでした。「使い方」と送っていただくと、できることをご案内します。";

    /// <summary>
    /// The alias vocabulary handed to the planning model. The family's own name for a
    /// device is listed alongside the provider label so a request phrased with either one
    /// resolves - the resolver accepts both, and the hint must not narrow that.
    /// </summary>
    private async Task<string> BuildAliasHintAsync(Guid householdId, CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .Select(d => new { d.Alias, d.Name, d.DisplayNameOverride })
            .ToListAsync(ct);

        return devices.Count == 0
            ? "(なし)"
            : string.Join(", ", devices.Select(d => string.IsNullOrWhiteSpace(d.DisplayNameOverride)
                ? $"{d.Alias}({d.Name})"
                : $"{d.Alias}({d.DisplayNameOverride}／{d.Name})"));
    }

    private async Task RecordMessageAsync(
        AssistantRequest request, MessageType type, string content, CancellationToken ct, bool isAi = false)
    {
        db.FamilyMessages.Add(new FamilyMessage
        {
            HouseholdId = request.HouseholdId,
            PersonId = isAi ? null : request.PersonId,
            Source = request.Source,
            MessageType = type,
            Content = content,
            OccurredAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }

    private async Task LogAiAsync(Guid householdId, string purpose, AiCompletionResult result, CancellationToken ct)
    {
        db.AiRequestLogs.Add(new AiRequestLog
        {
            HouseholdId = householdId,
            Purpose = purpose,
            Router = result.Router,
            ResolvedModel = result.ResolvedModel,
            DurationMs = result.DurationMs,
            Success = result.Success,
            // Only on failure: a reason next to a success reads as a warning that
            // is not one. Truncated to the column width so an unusually long
            // reason can never fail the write and lose the whole log row.
            Error = result.Success ? null : Truncate(result.Error, 256),
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            TotalTokens = result.TotalTokens,
            CreatedAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];
}

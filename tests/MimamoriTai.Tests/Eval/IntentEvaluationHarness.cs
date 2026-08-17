using System.Text.Json;
using System.Text.Json.Serialization;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests.Eval;

/// <summary>
/// One labelled utterance. <see cref="Scope"/> is null unless <see cref="Intent"/> is
/// query_data, and <see cref="Topic"/> only carries meaning when the intent is conversation.
/// </summary>
public sealed record IntentCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>What the app decided, and which layer decided it.</summary>
public sealed record IntentDecision(string Intent, string Topic, string? Scope, string Layer);

/// <summary>
/// Reproduces the routing decision <see cref="AssistantOrchestrator"/> makes, minus the
/// database and the reply generation.
///
/// Measuring the language model on its own would report a number the product never
/// experiences: urgent wording, product FAQs and questions for a professional are settled
/// by deterministic code *before* anything is sent to a model. The accuracy that matters
/// is the accuracy of that whole ladder, so the harness walks the same rungs in the same
/// order and uses the shipped <see cref="AssistantOrchestrator.SystemPrompt"/> rather than
/// a copy of it.
/// </summary>
public static class IntentEvaluationHarness
{
    /// <summary>
    /// The aliases a household would realistically have registered. The router is told the
    /// device list, so an evaluation without one would be easier than production.
    /// </summary>
    public const string AliasHint =
        "living-light(リビングの照明), bedroom-light(寝室の照明), aircon(エアコン), tv(テレビ), fan(扇風機), heater(ヒーター)";

    private static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<IntentCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Eval", "intent-cases.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var cases = document.RootElement.GetProperty("cases").Deserialize<List<IntentCase>>(CaseJson);
        return cases ?? throw new InvalidOperationException($"評価セットを読み込めませんでした: {path}");
    }

    /// <summary>
    /// Runs one utterance through the same ladder as the orchestrator.
    /// <paramref name="ai"/> is only consulted for what the deterministic layers leave over.
    /// </summary>
    public static async Task<IntentDecision> ClassifyAsync(
        IAiRouterClient ai, string message, CancellationToken ct = default)
    {
        // 1. Urgency. Decided first so a symptom never waits on -- or is softened by -- a model.
        if (AssistantKnowledgeBase.IsUrgent(message))
        {
            return new IntentDecision("conversation", "emergency", null, "rule:urgent");
        }

        // 2. Anything a professional should answer. Ahead of the FAQ pass for the same
        //    reason the orchestrator puts it there: this is the tie the safety class wins.
        if (AssistantExpertGuidance.TryRefer(message) is not null)
        {
            return new IntentDecision("conversation", "expert", null, "rule:expert");
        }

        // 3. Product FAQ, strict match only.
        if (AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict) is not null)
        {
            return new IntentDecision("conversation", "faq", null, "rule:faq");
        }

        // 4. Whatever is left goes to the router, exactly as the orchestrator sends it.
        var messages = new List<AiMessage>
        {
            AiMessage.System(AssistantOrchestrator.SystemPrompt),
            AiMessage.System($"登録済みの機器: {AliasHint}"),
            AiMessage.User(message)
        };

        var completion = await ai.CompleteAsync(messages, "intent", jsonMode: true, ct);
        var plan = IntentParser.TryParse(completion.Content);

        if (plan is null && completion.Success)
        {
            var retryMessages = new List<AiMessage>(messages)
            {
                AiMessage.Assistant(completion.Content),
                AiMessage.User("JSONとして解析できませんでした。指定したスキーマのJSONオブジェクトのみを、余計な文字なしで返してください。")
            };

            var retry = await ai.CompleteAsync(retryMessages, "intent-repair", jsonMode: true, ct);
            plan = IntentParser.TryParse(retry.Content);
            completion = retry;
        }

        // Unparseable output is a miss, not an exception: the user gets a re-ask, which is
        // wrong for every label in the set. A failed call is reported separately from bad
        // JSON -- a timeout says nothing about how well the model classifies.
        if (plan is null)
        {
            return new IntentDecision(
                "unparsed", "unparsed", null, completion.Success ? "model:unparsed" : "model:error");
        }

        return new IntentDecision(
            ToWire(plan.Intent),
            ToWire(plan.Topic),
            plan.Intent == AssistantIntent.QueryData ? ToWire(plan.Scope) : null,
            "model");
    }

    /// <summary>
    /// True when a miss is one that could hurt someone: a symptom or a question for a
    /// professional answered as anything else. Tracked separately because a set of 100 can
    /// hide two of these behind a respectable overall score.
    /// </summary>
    public static bool IsUnsafeMiss(IntentCase expected, IntentDecision actual) =>
        expected.Topic is "emergency" or "expert" && actual.Topic != expected.Topic;

    public static bool IsCorrect(IntentCase expected, IntentDecision actual)
    {
        if (actual.Intent != expected.Intent)
        {
            return false;
        }

        // Topic is only meaningful for conversation; scope only for query_data.
        if (expected.Intent == "conversation" && actual.Topic != expected.Topic)
        {
            return false;
        }

        if (expected.Intent == "query_data" && actual.Scope != expected.Scope)
        {
            return false;
        }

        return true;
    }

    private static string ToWire(AssistantIntent intent) => intent switch
    {
        AssistantIntent.ControlDevice => "control_device",
        AssistantIntent.DeviceStatus => "device_status",
        AssistantIntent.QueryData => "query_data",
        _ => "conversation"
    };

    private static string ToWire(AssistantTopic topic) => topic switch
    {
        AssistantTopic.Faq => "faq",
        AssistantTopic.Expert => "expert",
        AssistantTopic.Emergency => "emergency",
        _ => "general"
    };

    private static string ToWire(QueryScope scope) =>
        scope == QueryScope.Analysis ? "analysis" : "recent";
}

using System.Text.Json;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// DEMO ONLY. Deterministic rule-based stand-in for Azure Model Router so the whole product
/// stays demoable with no API key. It performs simple Japanese keyword matching and
/// emits exactly the same JSON contract the real model is asked to produce.
/// </summary>
public sealed class MockAiRouterClient : IAiRouterClient
{
    public const string MockModelName = "mock/local-rules";

    public bool IsConfigured => false;

    public string DisplayName => "MockAiRouter";

    public Task<AiCompletionResult> CompleteAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        var userMessage = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;

        // The purpose can carry a routing suffix (e.g. "summary-fast" for the LINE
        // deadline); the intent behind it is unchanged, so match on the base name.
        var basePurpose = purpose.EndsWith(AzureModelRouterOptions.FastSuffix, StringComparison.Ordinal)
            ? purpose[..^AzureModelRouterOptions.FastSuffix.Length]
            : purpose;

        var content = basePurpose switch
        {
            "summary" => BuildSummary(userMessage),
            _ when jsonMode => BuildIntentJson(userMessage),
            _ => BuildConversation(userMessage)
        };

        return Task.FromResult(new AiCompletionResult(true, content, DisplayName, MockModelName, 1));
    }

    /// <summary>
    /// Stand-in for the family-facing summary. With no model available the honest
    /// thing is to return the facts unchanged rather than invent friendly prose,
    /// so the demo never shows a number the data does not support.
    /// </summary>
    private static string BuildSummary(string prompt)
    {
        const string marker = "\n";
        var index = prompt.IndexOf("データ(", StringComparison.Ordinal);
        if (index < 0)
        {
            return prompt.Trim();
        }

        var newline = prompt.IndexOf(marker, index, StringComparison.Ordinal);
        return newline < 0 ? prompt.Trim() : prompt[(newline + 1)..].Trim();
    }

    private static string BuildIntentJson(string message)
    {
        var alias = ResolveAlias(message);

        var turnOff = ContainsAny(message, "消して", "けして", "オフ", "off", "切って");
        var turnOn = ContainsAny(message, "つけて", "点けて", "オン", "on", "点灯");
        var status = ContainsAny(message, "ついてる", "状態", "どうなってる", "点いてる");

        if (alias is not null && (turnOn || turnOff))
        {
            return Json("control_device", alias, turnOff ? "turn_off" : "turn_on", 0.95, null);
        }

        if (alias is not null && status)
        {
            return Json("device_status", alias, "get_status", 0.92, null);
        }

        if (ContainsAny(message, "どう", "様子", "何時", "回数", "活動", "昨日", "夜中", "最後", "何回", "少な"))
        {
            // Comparison / trend wording is what makes a data agent worth the wait;
            // "今日どう?" is answered from the local database alone.
            var scope = ContainsAny(message, "先週", "先月", "今週", "今月", "比べ", "比較", "平均", "傾向", "最近", "いつも", "推移", "変化")
                ? "analysis"
                : "recent";

            return Json("query_data", null, null, 0.9, message, scope);
        }

        if (alias is not null)
        {
            // A device was mentioned but the requested action is unclear: low confidence
            // keeps the safety policy from executing anything.
            return Json("control_device", alias, "toggle", 0.4, null);
        }

        return Json("conversation", null, null, 0.8, null, topic: ResolveTopic(message));
    }

    /// <summary>
    /// The first-stage routing decision the real model is asked for. Only meaningful for
    /// conversation: a device or data intent derives its own topic.
    ///
    /// Emergency and the professional fields are decided deterministically before anything
    /// reaches a model, so what is left for the mock to distinguish is "asking about this
    /// service" from "talking".
    /// </summary>
    private static string ResolveTopic(string message)
    {
        if (ContainsAny(message, "見守り", "このline", "アプリ", "使い方", "通知", "連携", "設定", "登録", "カメラ", "個人情報"))
        {
            return "faq";
        }

        return "general";
    }

    private static string? ResolveAlias(string message)
    {
        if (ContainsAny(message, "ストーブ", "ヒーター", "暖房", "heater"))
        {
            return "living-heater";
        }

        if (ContainsAny(message, "エアコン", "冷房", "クーラー", "aircon"))
        {
            return "living-ac";
        }

        if (ContainsAny(message, "寝室", "ベッドルーム", "bedroom"))
        {
            return "bedroom-light";
        }

        if (ContainsAny(message, "扇風機", "ファン", "fan"))
        {
            return "living-fan";
        }

        if (ContainsAny(message, "リビング", "居間", "living"))
        {
            return "living-light";
        }

        if (ContainsAny(message, "ライト", "照明", "電気", "light"))
        {
            return "living-light";
        }

        return null;
    }

    private static string BuildConversation(string message)
    {
        if (ContainsAny(message, "病院", "外出", "出かけ"))
        {
            return "了解しました。外出予定として家族にも共有します。";
        }

        if (ContainsAny(message, "ありがとう", "助かる"))
        {
            return "どういたしまして。いつでも声をかけてくださいね。";
        }

        return "承知しました。何かあればいつでも教えてください。";
    }

    private static string Json(string intent, string? alias, string? action, double confidence, string? question, string scope = "recent", string topic = "general") =>
        JsonSerializer.Serialize(new
        {
            intent,
            topic,
            deviceAlias = alias,
            action,
            scope,
            confidence,
            question
        });

    private static bool ContainsAny(string message, params string[] keywords) =>
        keywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
}

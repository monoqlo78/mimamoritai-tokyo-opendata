using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// Answers free-text questions about the operator console.
///
/// The model is never handed a database. It is handed a fixed list of figures built
/// here by <see cref="FabricSqlConsoleSync.BuildSnapshotAsync"/> — the same call that
/// fills the console's tables and charts — and is told, in the system prompt, that it
/// may not state anything the list does not contain. The list is returned to the
/// caller alongside the answer so an operator can check it line by line.
///
/// Reusing the sync's snapshot rather than querying again is deliberate: two queries
/// written months apart drift, and an answer that quietly disagrees with the chart
/// above it is worse than no answer.
/// </summary>
public sealed class ConsoleQuestionService(
    FabricSqlConsoleSync snapshotSource,
    IAiRouterClient ai,
    IAppDbContext db,
    TimeProvider clock,
    ILogger<ConsoleQuestionService> logger) : IConsoleQuestionService
{
    /// <summary>Longest question accepted. Long enough to ask properly, short enough to bound cost.</summary>
    public const int MaxQuestionLength = 400;

    /// <summary>
    /// What the console assistant must do, and in what order.
    ///
    /// <para>
    /// The rule that earns its place is the first one. Handed a snapshot and a question,
    /// a model will summarise the snapshot -- which is not an answer. An operator who
    /// asks 「今いちばん急ぐ世帯は？」 wants a household name in the first line, not a
    /// tour of the figures with the name somewhere in the fourth bullet. Answering
    /// first and then showing the evidence costs nothing and is the difference between
    /// a reply and a report.
    /// </para>
    /// </summary>
    private const string SystemPrompt = """
        あなたは高齢者見守りサービス「見守り隊」の運用コンソールのアシスタントです。
        日本語で、運用担当者に向けて簡潔に答えてください。

        答えかたの順序:
        - 1行目で質問にまっすぐ答える。世帯を尋ねられたら世帯名を、数を尋ねられたら数を、
          可否を尋ねられたら可否を、最初に書くこと。前置きや質問の言い換えは書かない。
        - 2行目以降で、その根拠を箇条書きで示す。各項目に世帯名か数値を必ず添えること。
        - 対応が必要なら、最後に「次にすること」を1行だけ書く。不要なら書かない。

        厳守事項:
        1. 【資料】に書かれている数値・世帯名・時刻だけを使うこと。書かれていない値を推測して書かない。
        2. 資料にない事柄を聞かれたら「この画面のデータからは分かりません」と答えること。
           そのうえで、資料から言える隣接した事実があれば1つだけ添えてよい。
        3. 「未計測」「記録なし」は 0 ではない。0 と書き換えない。
        4. 医療的な診断や断定はしない。気づいた点と、確認をおすすめする理由までにとどめる。
        5. 全体で400文字程度までにまとめること。
        6. 「デモデータ」と書かれた世帯・通知は動作確認用の作りものである。実際に人が住んでいるのは
           「実機」の世帯だけなので、優先して見るべき世帯・急ぎの対応の話は実機の世帯から答えること。
           デモデータに触れるのは、実機に該当がないときか、質問がデモを名指ししたときだけにし、
           その場合は必ず「デモデータ」と明記すること。件数の多さだけを理由に上位に挙げない。
        """;

    public bool IsConfigured => ai.IsConfigured;

    public async Task<ConsoleAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var trimmed = (question ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return ConsoleAnswer.Failed("質問を入力してください。", now);
        }

        if (trimmed.Length > MaxQuestionLength)
        {
            return ConsoleAnswer.Failed($"質問は{MaxQuestionLength}文字までにしてください。", now);
        }

        IReadOnlyList<string> evidence;
        try
        {
            evidence = BuildEvidence(await snapshotSource.BuildSnapshotAsync(ct), now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Console question could not read the snapshot.");
            return ConsoleAnswer.Failed("データの読み取りに失敗しました。時間をおいて試してください。", now);
        }

        var messages = new List<AiMessage>
        {
            AiMessage.System(SystemPrompt),
            AiMessage.User($"【資料】\n{string.Join("\n", evidence)}\n\n【質問】\n{trimmed}")
        };

        var started = Stopwatch.GetTimestamp();
        var result = await ai.CompleteAsync(messages, "console-question", jsonMode: false, ct);
        await LogCallAsync(result, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            logger.LogWarning(
                "Console question failed after {Elapsed}ms: {Error}",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.Error);

            // The evidence still goes back: the operator asked a question about the
            // fleet and these figures answer part of it even when the model did not.
            return new ConsoleAnswer(
                false,
                string.Empty,
                result.ResolvedModel,
                evidence,
                now,
                result.Error ?? "AI からの応答がありませんでした。");
        }

        return new ConsoleAnswer(true, result.Content.Trim(), result.ResolvedModel, evidence, now);
    }

    /// <summary>
    /// Records the call in the same log the console's "AI 呼び出し実績" chart is drawn from.
    ///
    /// Without this the console was the one caller missing from its own chart: an operator
    /// could ask ten questions and the routing table would still show zero, which reads as
    /// "the router is idle" rather than "this page does not report itself". No prompt and no
    /// answer text is written — counts, latency and the model the router picked only.
    ///
    /// A failure to write the log must never cost the operator their answer, so it is caught.
    /// </summary>
    private async Task LogCallAsync(AiCompletionResult result, CancellationToken ct)
    {
        try
        {
            db.AiRequestLogs.Add(new AiRequestLog
            {
                // No household: the question is asked about the fleet, not from one home.
                HouseholdId = null,
                Purpose = "console-question",
                Router = result.Router,
                ResolvedModel = result.ResolvedModel,
                DurationMs = result.DurationMs,
                Success = result.Success,
                Error = result.Success ? null : Truncate(result.Error, 256),
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens,
                CreatedAtUtc = clock.GetUtcNow()
            });

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Console question could not be recorded in the AI request log.");
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];

    // ------------------------------------------------------------------ evidence

    /// <summary>
    /// Flattens the console snapshot into the lines the model is allowed to use.
    ///
    /// Written as text rather than JSON on purpose: the units and the word 未計測 are
    /// what stop a missing reading from being read as a measured zero, and they survive
    /// a text line better than they survive a null in a JSON field.
    /// </summary>
    internal static IReadOnlyList<string> BuildEvidence(
        FabricSqlConsoleSync.Snapshot snapshot,
        DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"現在時刻(日本時間): {Local(now)}",
            $"登録世帯数: {snapshot.Households.Count}" +
            $"（実機{snapshot.Households.Count(h => h.DataSourceMode == DataSourceMode.Production)}件 /" +
            $" デモデータ{snapshot.Households.Count(h => h.DataSourceMode != DataSourceMode.Production)}件）"
        };

        // Which households are demonstrations. A sample household is seeded with months
        // of dramatic events, so left unmarked it out-shouts the real home that has one
        // quiet reading — and the console would recommend calling a fictional person.
        var demoHouseholds = snapshot.Households
            .Where(h => h.DataSourceMode != DataSourceMode.Production)
            .Select(h => h.Name)
            .ToHashSet(StringComparer.Ordinal);

        string DemoTag(string householdName) =>
            demoHouseholds.Contains(householdName) ? "［デモデータ］" : string.Empty;

        lines.Add("");
        lines.Add("[世帯ごとの状況]（実機の世帯を先に記載）");
        var households = snapshot.Households
            .OrderBy(h => h.DataSourceMode == DataSourceMode.Production ? 0 : 1)
            .ThenBy(h => h.Name, StringComparer.Ordinal);
        foreach (var h in households)
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"- {DemoTag(h.Name)}{h.Name}: 居住者{h.ResidentCount}名 / 家電{h.DeviceCount}台");
            sb.Append(CultureInfo.InvariantCulture, $" / 直近の通知{h.AlertsInWindow}件(うち送信失敗{h.FailedAlertsInWindow}件)");
            sb.Append(CultureInfo.InvariantCulture, $" / 最新リスク判定: {Risk(h.LatestRiskLevel)}");
            sb.Append(CultureInfo.InvariantCulture, $" / 最後の家電イベント: {LocalOrUnknown(h.LastEventUtc)}");
            sb.Append(CultureInfo.InvariantCulture, $" / 本日の電力使用量: {Wh(h.PowerTodayWh)}");
            sb.Append(CultureInfo.InvariantCulture, $" (平常時の目安 {(h.PowerBaselineWh is { } b ? Wh(b) : "未算出")}, 傾向 {Trend(h.PowerTrend)})");
            sb.Append(CultureInfo.InvariantCulture, $" / LINE通知先{h.ActiveLineRecipients}件");
            sb.Append(CultureInfo.InvariantCulture, $" / データ経路: {Mode(h.DataSourceMode)}");
            if (h.SwitchBotStatus is { } status && status != SwitchBotConnectionStatus.Connected)
            {
                sb.Append(CultureInfo.InvariantCulture, $" / SwitchBot接続: {status}");
            }

            lines.Add(sb.ToString());
        }

        lines.Add("");
        lines.Add("[最近の通知（新しい順・最大15件）]");
        var alerts = snapshot.Alerts
            .OrderByDescending(a => a.SentAtUtc)
            .Take(15)
            .ToList();
        if (alerts.Count == 0)
        {
            lines.Add("- 記録なし");
        }
        else
        {
            foreach (var a in alerts)
            {
                var delivery = a.Success ? "送信成功" : $"送信失敗({a.Error ?? "理由不明"})";
                lines.Add($"- {Local(a.SentAtUtc)} {DemoTag(a.HouseholdName)}{a.HouseholdName} {Risk(a.RiskLevel)}(スコア{a.Score}) {a.Reason} / {delivery}");
            }
        }

        lines.Add("");
        lines.Add("[屋外の公開観測データ（新しい順・最大12時間）]");
        var outdoor = snapshot.Outdoor
            .OrderByDescending(o => o.BucketStart)
            .Take(12)
            .ToList();
        if (outdoor.Count == 0)
        {
            lines.Add("- 記録なし");
        }
        else
        {
            foreach (var o in outdoor)
            {
                lines.Add(
                    $"- {o.BucketStart:MM/dd HH:mm} {o.AreaName}: 気温{Num(o.TemperatureC, "℃")}" +
                    $" (最低{Num(o.MinTemperatureC, "℃")} / 最高{Num(o.MaxTemperatureC, "℃")})" +
                    $" 湿度{Num(o.HumidityPercent, "%")} 暑さ指数WBGT{Num(o.MaxWbgt, "")}" +
                    $" 暑さ警戒レベル{o.HeatLevel} 寒さ警戒レベル{o.ColdLevel}");
            }
        }

        lines.Add("");
        lines.Add("[家電の稼働（世帯ごとの合計・観測期間内）]");
        var activity = snapshot.Activity
            .GroupBy(a => a.HouseholdName)
            .OrderBy(g => demoHouseholds.Contains(g.Key) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (activity.Count == 0)
        {
            lines.Add("- 記録なし");
        }
        else
        {
            foreach (var g in activity)
            {
                var metered = g.Where(a => a.EnergyWh is not null).ToList();
                var energy = metered.Count == 0
                    ? "未計測（電力計のない家電のみ）"
                    : Wh(metered.Sum(a => a.EnergyWh ?? 0));
                var devices = g.Select(a => a.DeviceName).Distinct(StringComparer.Ordinal).Take(6);
                lines.Add(
                    $"- {DemoTag(g.Key)}{g.Key}: イベント{g.Sum(a => a.EventCount)}回 / ON判定{g.Sum(a => a.OnCount)}回" +
                    $" / 電力{energy} / 対象家電: {string.Join("、", devices)}");
            }
        }

        lines.Add("");
        lines.Add("注記: 「未計測」はその値が観測されていないことを意味し、0 ではありません。" +
                  "暑さ指数(WBGT)は環境省が4月下旬〜10月下旬のみ配信するため、冬期は未計測が正常です。" +
                  "「［デモデータ］」は動作確認用に作られた世帯・通知で、実在の人物ではありません。");

        return lines;
    }

    // ------------------------------------------------------------------- helpers

    private static string Local(DateTimeOffset utc) =>
        HouseholdTime.ToLocal(utc).ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    private static string LocalOrUnknown(DateTimeOffset? utc) =>
        utc is { } value ? Local(value) : "記録なし";

    private static string Wh(double wh) =>
        wh <= 0 ? "0Wh" : $"{wh:0}Wh";

    private static string Num(double? value, string unit) =>
        value is { } v ? $"{v:0.#}{unit}" : "未計測";

    private static string Risk(RiskLevel? level) => level switch
    {
        RiskLevel.High => "高",
        RiskLevel.Medium => "中",
        RiskLevel.Low => "低",
        _ => "判定なし"
    };

    private static string Trend(PowerUsageTrend trend) => trend switch
    {
        PowerUsageTrend.Higher => "平常より多い",
        PowerUsageTrend.Lower => "平常より少ない",
        PowerUsageTrend.Typical => "平常どおり",
        _ => "比較できるデータが不足"
    };

    private static string Mode(DataSourceMode mode) => mode switch
    {
        DataSourceMode.Production => "実機",
        DataSourceMode.Sample => "サンプルデータ",
        _ => mode.ToString()
    };
}

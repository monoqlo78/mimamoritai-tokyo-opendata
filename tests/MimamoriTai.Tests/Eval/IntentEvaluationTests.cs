using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Infrastructure.Ai;

namespace MimamoriTai.Tests.Eval;

/// <summary>
/// Two different jobs live here.
///
/// The dataset checks run on every push: they cost nothing and stop the evaluation set
/// from rotting into something that would flatter the product (duplicate utterances,
/// labels that no longer exist, a class quietly dropping to two examples).
///
/// The accuracy run costs money and needs a real endpoint, so it only runs when
/// MIMAMORI_EVAL=1 and the router is configured. It writes docs/eval/intent-accuracy.md
/// so the reported number always has a dated, reproducible run behind it rather than a
/// figure typed into a slide.
/// </summary>
public class IntentEvaluationTests
{
    private static readonly string[] Intents = ["control_device", "device_status", "query_data", "conversation"];
    private static readonly string[] Topics = ["faq", "general", "expert", "emergency"];

    [Fact]
    public void DatasetHasHundredCasesWithUniqueIdsAndMessages()
    {
        var cases = IntentEvaluationHarness.LoadCases();

        Assert.Equal(100, cases.Count);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(cases.Count, cases.Select(c => c.Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DatasetLabelsAreValidAndInternallyConsistent()
    {
        foreach (var c in IntentEvaluationHarness.LoadCases())
        {
            Assert.Contains(c.Intent, Intents);
            Assert.Contains(c.Topic, Topics);

            // scope only carries meaning for query_data; a stray value elsewhere would be
            // silently ignored by the scorer and quietly weaken the set.
            if (c.Intent == "query_data")
            {
                Assert.True(c.Scope is "recent" or "analysis", $"{c.Id}: query_data には scope が必要です");
            }
            else
            {
                Assert.True(c.Scope is null, $"{c.Id}: {c.Intent} に scope は付けられません");
            }

            if (c.Intent != "conversation")
            {
                Assert.Equal("general", c.Topic);
            }
        }
    }

    [Fact]
    public void EveryClassHasEnoughExamplesToBeMeaningful()
    {
        var cases = IntentEvaluationHarness.LoadCases();

        // A class with one or two examples turns each miss into a 50-point swing, which is
        // not a measurement. Eight is the floor for the smallest class we care about.
        foreach (var group in cases.GroupBy(c => c.Intent == "conversation" ? $"conversation/{c.Topic}" : c.Intent))
        {
            Assert.True(group.Count() >= 8, $"{group.Key} の件数が {group.Count()} 件しかありません");
        }

        foreach (var group in cases.Where(c => c.Intent == "query_data").GroupBy(c => c.Scope))
        {
            Assert.True(group.Count() >= 8, $"query_data/{group.Key} の件数が {group.Count()} 件しかありません");
        }
    }

    /// <summary>
    /// Real accuracy against the live router. Opt-in: set MIMAMORI_EVAL=1 plus
    /// AzureModelRouter__Endpoint and AzureModelRouter__ApiKey.
    /// </summary>
    [Fact]
    public async Task MeasureAccuracyAgainstLiveRouter()
    {
        if (Environment.GetEnvironmentVariable("MIMAMORI_EVAL") != "1")
        {
            return;
        }

        var options = new AzureModelRouterOptions
        {
            Endpoint = Environment.GetEnvironmentVariable("AzureModelRouter__Endpoint") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("AzureModelRouter__ApiKey") ?? string.Empty,
            Deployment = Environment.GetEnvironmentVariable("AzureModelRouter__Deployment") ?? "model-router"
        };

        Assert.True(options.IsConfigured, "AzureModelRouter__Endpoint と __ApiKey を設定してください");

        using var http = new HttpClient { BaseAddress = new Uri(options.BuildBaseAddress()) };
        var client = new AzureModelRouterClient(
            http, Options.Create(options), NullLogger<AzureModelRouterClient>.Instance);

        var cases = IntentEvaluationHarness.LoadCases();
        var results = new List<(IntentCase Case, IntentDecision Decision, bool Correct)>();

        foreach (var c in cases)
        {
            var decision = await IntentEvaluationHarness.ClassifyAsync(client, c.Message);
            results.Add((c, decision, IntentEvaluationHarness.IsCorrect(c, decision)));
        }

        var report = BuildReport(results);
        var path = Path.Combine(RepoRoot(), "docs", "eval", "intent-accuracy.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));

        var unsafeMisses = results.Count(r => IntentEvaluationHarness.IsUnsafeMiss(r.Case, r.Decision));

        // The overall score is reported, not asserted -- it moves with the model. A symptom
        // or a medical question landing anywhere else is a different kind of failure and is
        // the one thing this run refuses to let through.
        Assert.True(unsafeMisses == 0, $"危険側の取りこぼしが {unsafeMisses} 件あります。{path} を確認してください");
    }

    private static string BuildReport(
        IReadOnlyList<(IntentCase Case, IntentDecision Decision, bool Correct)> results)
    {
        var correct = results.Count(r => r.Correct);
        var accuracy = (double)correct / results.Count * 100;
        var byModel = results.Count(r => r.Decision.Layer.StartsWith("model", StringComparison.Ordinal));

        var sb = new StringBuilder();
        sb.AppendLine("# 意図分類の正答率");
        sb.AppendLine();
        sb.AppendLine("<!-- IntentEvaluationTests.MeasureAccuracyAgainstLiveRouter が生成。手で書き換えないこと。 -->");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 実行日時 (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 評価件数: {results.Count} 件");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 正答: {correct} 件 / **正答率 {accuracy:0.0}%**");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- うち決定的ロジックで確定: {results.Count - byModel} 件、モデル呼び出し: {byModel} 件");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- 危険側の取りこぼし (emergency / expert を取り逃した件数): **{results.Count(r => IntentEvaluationHarness.IsUnsafeMiss(r.Case, r.Decision))} 件**");
        sb.AppendLine();

        sb.AppendLine("## クラス別");
        sb.AppendLine();
        sb.AppendLine("| クラス | 件数 | 正答 | 正答率 |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var group in results
            .GroupBy(r => r.Case.Intent == "conversation"
                ? $"conversation/{r.Case.Topic}"
                : r.Case.Intent == "query_data" ? $"query_data/{r.Case.Scope}" : r.Case.Intent)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var n = group.Count();
            var ok = group.Count(r => r.Correct);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {n} | {ok} | {(double)ok / n * 100:0.0}% |");
        }

        var misses = results.Where(r => !r.Correct).ToList();
        sb.AppendLine();
        sb.AppendLine("## 誤分類の内訳");
        sb.AppendLine();

        if (misses.Count == 0)
        {
            sb.AppendLine("なし。");
        }
        else
        {
            sb.AppendLine("| ID | 入力 | 期待 | 実際 | 判定した層 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (var m in misses)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {m.Case.Id} | {m.Case.Message} | {Describe(m.Case.Intent, m.Case.Topic, m.Case.Scope)} | {Describe(m.Decision.Intent, m.Decision.Topic, m.Decision.Scope)} | {m.Decision.Layer} |");
            }
        }

        return sb.ToString();
    }

    private static string Describe(string intent, string topic, string? scope) => intent switch
    {
        "conversation" => $"conversation/{topic}",
        "query_data" => $"query_data/{scope}",
        _ => intent
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MimamoriTai.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません");
    }
}

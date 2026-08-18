using System.Globalization;
using System.Text;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests.Eval;

/// <summary>
/// The false-alarm gate.
///
/// A monitoring service is not judged on how much it detects. It is judged on whether the
/// family still opens the notifications after a month, and that is decided by how often it
/// interrupted them over nothing. Detection rate is easy to claim and easy to inflate --
/// alert on everything and it reaches 100%. The number nobody publishes is what that costs
/// on the ordinary days in between, so it is the number measured here.
///
/// Unlike the intent evaluation, this one calls no model and no external API:
/// <see cref="MimamoriTai.Core.Application.RiskAssessmentService.Evaluate"/> is a pure
/// function, so the whole set runs in milliseconds on every push. A change that starts
/// waking a family on a quiet night turns the build red in the pull request that caused it.
/// </summary>
public class RiskEvaluationTests
{
    [Fact]
    public void ScenariosAreUniquelyIdentifiedAndLabelled()
    {
        var scenarios = RiskEvaluationHarness.LoadScenarios();

        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Label).Distinct(StringComparer.Ordinal).Count());

        foreach (var s in scenarios)
        {
            Assert.True(s.Kind is "normal" or "incident", $"{s.Id}: kind は normal か incident です");

            // The rationale is what stops a scenario from being quietly relabelled to make
            // a failing build green. A label with no stated reason is not a label.
            Assert.False(string.IsNullOrWhiteSpace(s.Rationale), $"{s.Id}: rationale がありません");
            Assert.True(TimeOnly.TryParse(s.Now, out _), $"{s.Id}: now を時刻として読めません");
        }
    }

    [Fact]
    public void BothHalvesAreBigEnoughToBeAMeasurement()
    {
        var scenarios = RiskEvaluationHarness.LoadScenarios();
        var normal = scenarios.Count(s => s.Kind == "normal");
        var incident = scenarios.Count(s => s.Kind == "incident");

        // "誤報ゼロ" from a handful of hand-picked quiet days is not a result. The quiet
        // half has to be the larger one, because quiet days are what a household mostly has.
        Assert.True(normal >= 25, $"平常日が {normal} 件しかありません");
        Assert.True(incident >= 15, $"異常日が {incident} 件しかありません");
        Assert.True(normal > incident, "平常日より異常日が多い評価セットは現実の比率を反映しません");
    }

    /// <summary>
    /// Runs every labelled day through the shipped rules, writes the report, and fails the
    /// build on either kind of error.
    /// </summary>
    [Fact]
    public void QuietDaysStaySilentAndIncidentsAreReported()
    {
        var outcomes = RiskEvaluationHarness.LoadScenarios()
            .Select(RiskEvaluationHarness.Run)
            .ToList();

        TryWriteReport(outcomes);

        var falseAlarms = outcomes.Where(o => o.IsFalseAlarm).ToList();
        var misses = outcomes.Where(o => o.IsMiss).ToList();

        Assert.True(
            falseAlarms.Count == 0,
            "何でもない日に通知が飛びました:\n" + string.Join(
                "\n", falseAlarms.Select(o => $"  {o.Scenario.Id} {o.Scenario.Label} → {o.Result.Level} ({o.Result.Score}点) {o.Result.Reason}")));

        Assert.True(
            misses.Count == 0,
            "見に行くべき日に通知が飛びませんでした:\n" + string.Join(
                "\n", misses.Select(o => $"  {o.Scenario.Id} {o.Scenario.Label} → {o.Result.Level} ({o.Result.Score}点) {o.Result.Reason}")));
    }

    /// <summary>
    /// Every alert carries a sentence the family reads on their phone. An alert that fires
    /// with the default "nothing unusual" text would be worse than no alert: it would tell
    /// them to go and look, and give them nothing to look for.
    /// </summary>
    [Fact]
    public void EveryAlertSaysWhatIsWrong()
    {
        foreach (var outcome in RiskEvaluationHarness.LoadScenarios().Select(RiskEvaluationHarness.Run))
        {
            if (!outcome.Notified)
            {
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(outcome.Result.Reason), $"{outcome.Scenario.Id}: 理由が空です");
            Assert.DoesNotContain("普段どおり", outcome.Result.Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Deliberately no timestamp in the body. The report is committed, so its git history is
    /// the dated record, and the file only changes when the product's behaviour changes --
    /// which makes a diff here something a reviewer has to explain.
    /// </summary>
    private static void TryWriteReport(IReadOnlyList<RiskOutcome> outcomes)
    {
        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var path = Path.Combine(root, "docs", "eval", "false-alarm-rate.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            File.WriteAllText(path, BuildReport(outcomes), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // A read-only checkout must not fail the gate; the assertions are the contract.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildReport(IReadOnlyList<RiskOutcome> outcomes)
    {
        var normal = outcomes.Where(o => o.Scenario.Kind == "normal").ToList();
        var incident = outcomes.Where(o => o.Scenario.Kind == "incident").ToList();
        var falseAlarms = normal.Count(o => o.IsFalseAlarm);
        var misses = incident.Count(o => o.IsMiss);

        var sb = new StringBuilder();
        sb.AppendLine("# 誤報率");
        sb.AppendLine();
        sb.AppendLine("<!-- RiskEvaluationTests が生成。手で書き換えないこと。 -->");
        sb.AppendLine();
        sb.AppendLine("見守りサービスの寿命を決めるのは検知率ではなく誤報率である。何でもない日に通知が飛べば");
        sb.AppendLine("家族は通知を切り、切られた瞬間に検知率はいくつであっても意味を失う。だからここでは");
        sb.AppendLine("「危険をどれだけ捕まえたか」と同じ重さで「何でもない日にどれだけ黙っていられたか」を測る。");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"判定は決定的な純関数なので、この表はLLMも外部APIも使わず毎pushでCIが再計算する（通知の閾値: {RiskEvaluationHarness.NotifyThreshold} 以上）。");
        sb.AppendLine();

        sb.AppendLine("| | 件数 | 通知した | 通知しなかった |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| 平常日（通知は不要） | {normal.Count} | **{falseAlarms}**（誤報） | {normal.Count - falseAlarms} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| 異常日（通知が必要） | {incident.Count} | {incident.Count - misses} | **{misses}**（見逃し） |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **誤報率 {(double)falseAlarms / Math.Max(1, normal.Count) * 100:0.0}%** ({falseAlarms}/{normal.Count})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **見逃し率 {(double)misses / Math.Max(1, incident.Count) * 100:0.0}%** ({misses}/{incident.Count})");
        sb.AppendLine();

        sb.AppendLine("## この数字の限界（先に書いておく）");
        sb.AppendLine();
        sb.AppendLine("1. これは実世帯の記録ではなく、手で書いた合成シナリオである。測っているのは");
        sb.AppendLine("   「規則が宣言どおりに振る舞うか」であって「現実の高齢者宅で誤報が出ないか」ではない。");
        sb.AppendLine("   後者を言うには実運用データが要る。");
        sb.AppendLine("2. 期待値は公開している閾値を踏まえて書いているので、これは仕様書と回帰ゲートを");
        sb.AppendLine("   兼ねるものであり、独立した第三者検証ではない。");
        sb.AppendLine("3. 価値は境界にある。閾値の10分手前の寝坊、猛暑日に8時間動くエアコン、寒い日に");
        sb.AppendLine("   6時間動くヒーター、就寝中の平坦な電力 — うっかりした変更がまず壊すのはここで、");
        sb.AppendLine("   壊れた瞬間にプルリクエストのCIが赤くなる。");
        sb.AppendLine();

        sb.AppendLine("## この評価が最初の実行で見つけた欠陥");
        sb.AppendLine();
        sb.AppendLine("表を緑にするために書いた道具ではない。最初に走らせた時点で、出荷済みの規則に");
        sb.AppendLine("実際の誤報が1件見つかった。");
        sb.AppendLine();
        sb.AppendLine("「活動量が普段より少ない」の判定が、**進行中の一日の集計を、終わった一日の平均と**");
        sb.AppendLine("**比べていた**。朝6時なら少なくて当たり前なので、全世帯で毎朝鳴る。しかも利用回数が");
        sb.AppendLine("1回以上あることが条件なので、鳴る引き金は**起きて照明を点けたこと**だった。");
        sb.AppendLine("毎朝鳴る通知は読まれなくなり、読まれなくなった通知は本当の日に効かない。");
        sb.AppendLine();
        sb.AppendLine("修正は `RiskAssessmentService.LowActivityEarliestHour`。一日がその判断に足るだけ");
        sb.AppendLine("進むまでこの比較を行わない。朝の異変は元々「起床が遅い」規則が直接見ている。");
        sb.AppendLine("n-31 / n-32 がこの欠陥を再現するケースとして残してある。");
        sb.AppendLine();

        AppendTable(sb, "## 平常日：黙っていられたか", normal);
        AppendTable(sb, "## 異常日：気づけたか", incident);

        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string heading, IReadOnlyList<RiskOutcome> outcomes)
    {
        sb.AppendLine(heading);
        sb.AppendLine();
        sb.AppendLine("| ID | 状況 | 家族にとって | 判定 | 点 | 結果 |");
        sb.AppendLine("| --- | --- | --- | --- | ---: | --- |");

        foreach (var o in outcomes.OrderBy(o => o.Scenario.Id, StringComparer.Ordinal))
        {
            var verdict = o.Scenario.Kind == "normal"
                ? o.Notified ? "**誤報**" : "OK（黙った）"
                : o.Notified ? "OK（通知）" : "**見逃し**";

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {o.Scenario.Id} | {o.Scenario.Label} | {o.Scenario.Rationale} | {Label(o.Result.Level)} | {o.Result.Score} | {verdict} |");
        }

        sb.AppendLine();
    }

    private static string Label(RiskLevel level) => level switch
    {
        RiskLevel.High => "高",
        RiskLevel.Medium => "中",
        _ => "低"
    };

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MimamoriTai.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}

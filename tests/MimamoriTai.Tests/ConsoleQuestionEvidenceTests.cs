using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// The console's question box answers from a fixed list of figures, and the model is
/// told it may use nothing else. These tests pin the two properties that make that
/// list safe to hand over: a reading that was never taken must not appear as a zero,
/// and every household on screen must be in the list, so a question about the quiet
/// one is answerable rather than answered by omission.
/// </summary>
public class ConsoleQuestionEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 3, 0, 0, TimeSpan.Zero);

    private static FabricSqlConsoleSync.Snapshot Snapshot(
        IReadOnlyList<FabricSqlConsoleSync.HouseholdRow>? households = null,
        IReadOnlyList<FabricSqlConsoleSync.AlertRow>? alerts = null,
        IReadOnlyList<FabricSqlConsoleSync.ActivityRow>? activity = null,
        IReadOnlyList<FabricSqlConsoleSync.OutdoorRow>? outdoor = null) =>
        new(Now, households ?? [], alerts ?? [], activity ?? [], [], outdoor ?? []);

    private static FabricSqlConsoleSync.HouseholdRow Household(
        string name,
        DataSourceMode mode = DataSourceMode.Production) =>
        new(Guid.NewGuid(), name, mode, 1, 1, 3, Now.AddMinutes(-20),
            SwitchBotConnectionStatus.Connected, null, 1, 0, 0, RiskLevel.Low);

    [Fact]
    public void Evidence_lists_every_household()
    {
        var lines = ConsoleQuestionService.BuildEvidence(
            Snapshot(households: [Household("佐藤家"), Household("鈴木家")]), Now);

        var text = string.Join("\n", lines);
        Assert.Contains("佐藤家", text, StringComparison.Ordinal);
        Assert.Contains("鈴木家", text, StringComparison.Ordinal);
        Assert.Contains("登録世帯数: 2", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sample household is seeded with months of dramatic events, so on volume alone it
    /// out-shouts the one real home that has a single quiet reading. Unless the evidence
    /// says which households are fictional, the console recommends calling on someone who
    /// does not exist — which is exactly what it did before this was added.
    /// </summary>
    [Fact]
    public void Demo_households_are_marked_and_listed_after_the_real_ones()
    {
        var lines = ConsoleQuestionService.BuildEvidence(
            Snapshot(households:
            [
                Household("見守り隊デモ世帯", DataSourceMode.Sample),
                Household("わが家")
            ]), Now);

        var text = string.Join("\n", lines);
        Assert.Contains("［デモデータ］見守り隊デモ世帯", text, StringComparison.Ordinal);
        Assert.DoesNotContain("［デモデータ］わが家", text, StringComparison.Ordinal);
        Assert.Contains("実機1件 / デモデータ1件", text, StringComparison.Ordinal);

        var real = lines.ToList().FindIndex(l => l.Contains("わが家", StringComparison.Ordinal));
        var demo = lines.ToList().FindIndex(l => l.Contains("見守り隊デモ世帯", StringComparison.Ordinal));
        Assert.True(real < demo, "実機の世帯がデモ世帯より先に並ぶこと");
    }

    [Fact]
    public void Alerts_and_activity_from_a_demo_household_carry_the_same_mark()
    {
        var demo = Household("見守り隊デモ世帯", DataSourceMode.Sample);
        var alert = new FabricSqlConsoleSync.AlertRow(
            Guid.NewGuid(), demo.HouseholdId, demo.Name, RiskLevel.High, 70,
            "活動量が少なめです", Success: true, Error: null, SentAtUtc: Now.AddHours(-3));
        var activity = new FabricSqlConsoleSync.ActivityRow(
            demo.HouseholdId, demo.Name, Guid.NewGuid(), "扇風機", "Fan",
            new DateTime(2026, 8, 1, 2, 0, 0, DateTimeKind.Utc), 9, 4, "Sample");

        var text = string.Join("\n", ConsoleQuestionService.BuildEvidence(
            Snapshot(households: [demo], alerts: [alert], activity: [activity]), Now));

        Assert.Contains("［デモデータ］見守り隊デモ世帯 高(スコア70)", text, StringComparison.Ordinal);
        Assert.Contains("［デモデータ］見守り隊デモ世帯: イベント9回", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmeasured_outdoor_readings_are_not_reported_as_zero()
    {
        // A winter row: AMeDAS publishes a temperature all year, 環境省 publishes WBGT
        // only in the warm months. The gap must read 未計測, never 0.
        var outdoor = new FabricSqlConsoleSync.OutdoorRow(
            "44132", "東京都 千代田区", new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc),
            TemperatureC: 4.2, MinTemperatureC: null, MaxTemperatureC: null,
            HumidityPercent: null, MaxWbgt: null, HeatLevel: 0, ColdLevel: 2, SampleCount: 6);

        var text = string.Join("\n", ConsoleQuestionService.BuildEvidence(
            Snapshot(outdoor: [outdoor]), Now));

        Assert.Contains("気温4.2℃", text, StringComparison.Ordinal);
        Assert.Contains("暑さ指数WBGT未計測", text, StringComparison.Ordinal);
        Assert.Contains("湿度未計測", text, StringComparison.Ordinal);
        Assert.DoesNotContain("暑さ指数WBGT0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmetered_appliances_are_reported_as_unmeasured_rather_than_zero_watt_hours()
    {
        var activity = new FabricSqlConsoleSync.ActivityRow(
            Guid.NewGuid(), "佐藤家", Guid.NewGuid(), "玄関ライト", "Light",
            new DateTime(2026, 8, 1, 2, 0, 0, DateTimeKind.Utc), 4, 2, "SwitchBot");

        var text = string.Join("\n", ConsoleQuestionService.BuildEvidence(
            Snapshot(activity: [activity]), Now));

        Assert.Contains("未計測（電力計のない家電のみ）", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_says_records_are_absent_rather_than_leaving_a_blank_section()
    {
        var text = string.Join("\n", ConsoleQuestionService.BuildEvidence(Snapshot(), Now));

        Assert.Contains("[最近の通知（新しい順・最大15件）]\n- 記録なし", text, StringComparison.Ordinal);
        Assert.Contains("[家電の稼働（世帯ごとの合計・観測期間内）]\n- 記録なし", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_alert_delivery_is_stated_with_its_reason()
    {
        var alert = new FabricSqlConsoleSync.AlertRow(
            Guid.NewGuid(), Guid.NewGuid(), "佐藤家", RiskLevel.High, 82,
            "室温が高い状態が続いています", Success: false, Error: "LINE 送信に失敗",
            SentAtUtc: Now.AddHours(-1));

        var text = string.Join("\n", ConsoleQuestionService.BuildEvidence(
            Snapshot(alerts: [alert]), Now));

        Assert.Contains("送信失敗(LINE 送信に失敗)", text, StringComparison.Ordinal);
        Assert.Contains(HouseholdTime.ToLocal(Now.AddHours(-1)).ToString("MM/dd HH:mm"), text, StringComparison.Ordinal);
    }
}

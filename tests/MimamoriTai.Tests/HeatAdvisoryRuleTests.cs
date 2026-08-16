using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// The heatstroke rule: outdoor open data (環境省 WBGT) crossed with whether anything in
/// the house is actually cooling. Roughly half of Tokyo's heatstroke ambulance calls
/// start indoors, which is the gap a camera-free watch service can close.
/// </summary>
public class HeatAdvisoryRuleTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static HeatAdvisory Advisory(double wbgt) => new(
        wbgt,
        HeatAdvisory.Classify(wbgt),
        32.5,
        70,
        DateTimeOffset.UtcNow,
        "東京",
        "出典：環境省熱中症予防情報サイト／気象庁");

    /// <summary>A normal day, so nothing the heat rule has to say about it.</summary>
    private static DailyActivity NormalDay() => new(Today, new TimeOnly(7, 0), new TimeOnly(20, 0), 8, 0, 1200);

    private static IReadOnlyList<DailyActivity> Baseline() =>
    [
        new(Today.AddDays(-1), new TimeOnly(7, 0), new TimeOnly(20, 0), 8, 0, 1200),
        new(Today.AddDays(-2), new TimeOnly(7, 10), new TimeOnly(20, 0), 8, 0, 1200),
        new(Today.AddDays(-3), new TimeOnly(6, 50), new TimeOnly(20, 0), 8, 0, 1200)
    ];

    [Theory]
    [InlineData(18.0, HeatAlertLevel.Safe)]
    [InlineData(21.0, HeatAlertLevel.Caution)]
    [InlineData(25.0, HeatAlertLevel.Warning)]
    [InlineData(28.0, HeatAlertLevel.SevereWarning)]
    [InlineData(31.0, HeatAlertLevel.Danger)]
    [InlineData(34.2, HeatAlertLevel.Danger)]
    public void Classify_Uses_The_Government_Bands(double wbgt, HeatAlertLevel expected) =>
        Assert.Equal(expected, HeatAdvisory.Classify(wbgt));

    [Fact]
    public void Silent_When_The_Index_Is_Below_Severe_Warning()
    {
        var (score, reason) = RiskAssessmentService.EvaluateHeat(
            Advisory(26.0),
            [new CoolingDevice("エアコン", IsOn: true, Watts: 0)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    [Fact]
    public void Silent_When_A_Cooling_Appliance_Is_Actually_Drawing_Power()
    {
        var (score, reason) = RiskAssessmentService.EvaluateHeat(
            Advisory(30.0),
            [new CoolingDevice("エアコン", IsOn: true, Watts: 420)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    /// <summary>
    /// The case the whole feature exists for: a plug that reports itself on because it is
    /// switched at the wall, while drawing nothing. That is an air conditioner turned off
    /// by its remote on a 厳重警戒 day.
    /// </summary>
    [Fact]
    public void Scores_When_The_Air_Conditioner_Is_Only_Standing_By()
    {
        var (score, reason) = RiskAssessmentService.EvaluateHeat(
            Advisory(29.4),
            [new CoolingDevice("エアコン", IsOn: true, Watts: 0)]);

        Assert.Equal(45, score);
        Assert.Contains("エアコン", reason);
        Assert.Contains("厳重警戒", reason);
    }

    [Fact]
    public void Reaches_High_On_Its_Own_At_The_Danger_Band()
    {
        var risk = RiskAssessmentService.Evaluate(
            NormalDay(),
            Baseline(),
            new TimeOnly(14, 0),
            heat: Advisory(31.5),
            cooling: [new CoolingDevice("エアコン", IsOn: false, Watts: 0)]);

        Assert.Equal(RiskLevel.High, risk.Level);
        Assert.Contains("危険", risk.Reason);
        Assert.Contains("熱中症", risk.Reason);
    }

    /// <summary>
    /// Never accuse a family of leaving the cooling off when they never told us they had
    /// any. Say the index, score nothing.
    /// </summary>
    [Fact]
    public void Mentions_But_Does_Not_Score_When_No_Cooling_Appliance_Is_Registered()
    {
        var (score, reason) = RiskAssessmentService.EvaluateHeat(Advisory(32.0), []);

        Assert.Equal(0, score);
        Assert.Contains("未登録", reason);
    }

    /// <summary>
    /// A plug that gives us no wattage still gets the benefit of the doubt: half a signal
    /// beats calling a working air conditioner idle.
    /// </summary>
    [Fact]
    public void Treats_On_With_Unknown_Wattage_As_Cooling()
    {
        var (score, reason) = RiskAssessmentService.EvaluateHeat(
            Advisory(32.0),
            [new CoolingDevice("エアコン", IsOn: true, Watts: null)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    [Fact]
    public void A_Fan_Counts_As_Cooling_But_An_Unrelated_Plug_Does_Not()
    {
        Assert.True(RiskAssessmentService.IsCooling(DeviceType.Fan));
        Assert.True(RiskAssessmentService.IsCooling(DeviceType.AirConditioner));
        Assert.False(RiskAssessmentService.IsCooling(DeviceType.Plug));
        Assert.False(RiskAssessmentService.IsCooling(DeviceType.Heater));
    }

    /// <summary>An unknown index must leave the existing rules exactly as they were.</summary>
    [Fact]
    public void No_Advisory_Changes_Nothing()
    {
        var withoutHeat = RiskAssessmentService.Evaluate(NormalDay(), Baseline(), new TimeOnly(14, 0));
        var withNullHeat = RiskAssessmentService.Evaluate(
            NormalDay(), Baseline(), new TimeOnly(14, 0),
            heat: null,
            cooling: [new CoolingDevice("エアコン", IsOn: true, Watts: 0)]);

        Assert.Equal(withoutHeat.Level, withNullHeat.Level);
        Assert.Equal(withoutHeat.Score, withNullHeat.Score);
        Assert.Equal(withoutHeat.Reason, withNullHeat.Reason);
    }

    /// <summary>
    /// A cooling appliance is only ever an air conditioner in a hot room. Guarded, not
    /// Safe, because we do not know what else shares that socket.
    /// </summary>
    [Fact]
    public void Air_Conditioner_Is_Guarded_For_The_Assistant()
    {
        Assert.Equal(SafetyClass.Guarded, DeviceSafetyPolicy.Classify(DeviceType.AirConditioner));
    }
}

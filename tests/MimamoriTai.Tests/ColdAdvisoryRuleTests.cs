using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// The cold half of the watch. The heat rule only works for five months of the year --
/// the 環境省 暑さ指数 feed is not published from late October to late April -- so the
/// year-round 気象庁 temperature carries the same idea through winter: it is cold
/// outside, and nothing in the house is heating.
///
/// <para>
/// The harm being watched for is not discomfort. It is the bathroom: undressing in a
/// cold changing room and then getting into a hot bath is what kills older people in
/// Japanese winters, and a resident economising on heating is exactly the person it
/// happens to.
/// </para>
/// </summary>
public class ColdAdvisoryRuleTests
{
    private static ColdAdvisory Cold(double temperatureC) => new(
        temperatureC,
        ColdAdvisory.Classify(temperatureC),
        48,
        DateTimeOffset.UtcNow,
        "東京",
        "出典：気象庁");

    [Theory]
    [InlineData(21.0, ColdAlertLevel.Mild)]
    [InlineData(15.0, ColdAlertLevel.Mild)]
    [InlineData(12.4, ColdAlertLevel.Chilly)]
    [InlineData(10.0, ColdAlertLevel.Chilly)]
    [InlineData(7.5, ColdAlertLevel.Cold)]
    [InlineData(5.0, ColdAlertLevel.Cold)]
    [InlineData(2.0, ColdAlertLevel.SevereCold)]
    [InlineData(-3.0, ColdAlertLevel.SevereCold)]
    public void Classify_Bands_On_The_Temperature_A_Room_Cannot_Hold(
        double temperatureC, ColdAlertLevel expected) =>
        Assert.Equal(expected, ColdAdvisory.Classify(temperatureC));

    /// <summary>
    /// A cool day is not a health event. Saying something about every one of them is how
    /// a service teaches a family to stop reading it.
    /// </summary>
    [Fact]
    public void Silent_On_A_Merely_Chilly_Day()
    {
        var (score, reason) = RiskAssessmentService.EvaluateCold(
            Cold(12.0),
            [new HeatingDevice("エアコン", IsOn: false, Watts: 0)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    [Fact]
    public void Silent_When_A_Heater_Is_Actually_Drawing_Power()
    {
        var (score, reason) = RiskAssessmentService.EvaluateCold(
            Cold(3.0),
            [new HeatingDevice("エアコン", IsOn: true, Watts: 640)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    /// <summary>
    /// The case the feature exists for: cold enough that a room cannot hold the 18℃ the
    /// WHO housing guidelines call the lower safe limit, and the heating is drawing
    /// nothing. Without a camera, the electricity is the only way to see that.
    /// </summary>
    [Fact]
    public void Scores_When_Nothing_Is_Heating_On_A_Cold_Day()
    {
        var (score, reason) = RiskAssessmentService.EvaluateCold(
            Cold(7.0),
            [new HeatingDevice("エアコン", IsOn: true, Watts: 0)]);

        Assert.Equal(45, score);
        Assert.Contains("エアコン", reason);
    }

    /// <summary>
    /// Below 5℃ this stands on its own, the way 危険 does on the heat side: a fall in the
    /// bath does not give us a second signal to wait for.
    /// </summary>
    [Fact]
    public void Severe_Cold_Alone_Reaches_High()
    {
        var (score, _) = RiskAssessmentService.EvaluateCold(
            Cold(1.5),
            [new HeatingDevice("ヒーター", IsOn: false, Watts: 0)]);

        Assert.Equal(60, score);
    }

    /// <summary>
    /// A household that never registered a heater gets the temperature, not an accusation
    /// about an appliance we were never told about. Same contract as the heat rule.
    /// </summary>
    [Fact]
    public void Informs_Without_Scoring_When_No_Heating_Is_Registered()
    {
        var (score, reason) = RiskAssessmentService.EvaluateCold(Cold(2.0), []);

        Assert.Equal(0, score);
        Assert.Contains("未登録", reason);
    }

    [Fact]
    public void Nothing_To_Say_Without_Open_Data()
    {
        var (score, reason) = RiskAssessmentService.EvaluateCold(
            null,
            [new HeatingDevice("エアコン", IsOn: true, Watts: 0)]);

        Assert.Equal(0, score);
        Assert.Null(reason);
    }

    /// <summary>
    /// Two hours is the right patience for a kettle and the wrong one for a heater in
    /// February. Nagging a resident for keeping warm is how a watch service earns being
    /// switched off.
    /// </summary>
    [Fact]
    public void Heating_Is_Given_Longer_Before_It_Counts_As_Left_On_When_It_Is_Cold()
    {
        var noon = new TimeOnly(12, 0);

        Assert.Equal(
            RiskAssessmentService.WarmingLeftOnLimit,
            RiskAssessmentService.LeftOnLimit(DeviceType.Heater, noon, ColdAlertLevel.Cold));

        Assert.Equal(
            RiskAssessmentService.HeatLeftOnLimit,
            RiskAssessmentService.LeftOnLimit(DeviceType.Heater, noon, ColdAlertLevel.Chilly));
    }

    /// <summary>
    /// The relaxation is a longer leash, not a removed one. A heater running through the
    /// night is still a fire risk on the coldest night of the year.
    /// </summary>
    [Fact]
    public void Cold_Weather_Does_Not_Remove_The_Left_On_Check_Entirely()
    {
        Assert.True(RiskAssessmentService.WarmingLeftOnLimit < TimeSpan.FromHours(24));
        Assert.True(RiskAssessmentService.WarmingLeftOnLimit > RiskAssessmentService.HeatLeftOnLimit);
    }

    /// <summary>
    /// An air conditioner is the heating in most Japanese homes, so it has to appear on
    /// both lists or half the country has no registered heater.
    /// </summary>
    [Fact]
    public void An_Air_Conditioner_Counts_As_Both_Cooling_And_Heating()
    {
        Assert.True(RiskAssessmentService.IsCooling(DeviceType.AirConditioner));
        Assert.True(RiskAssessmentService.IsHeating(DeviceType.AirConditioner));
        Assert.False(RiskAssessmentService.IsHeating(DeviceType.Fan));
    }

    /// <summary>
    /// Advice is written for the person, not about the weather: something they can do in
    /// the next few minutes. Below the acting threshold it says nothing at all.
    /// </summary>
    [Fact]
    public void Advice_Speaks_Only_When_There_Is_Something_To_Do()
    {
        Assert.Null(Cold(18.0).Advice);
        Assert.NotNull(Cold(12.0).Advice);
        Assert.Contains("脱衣所", Cold(1.0).Advice);
    }

    /// <summary>
    /// A changing room can only be warmed before the cold morning, not during it. So the
    /// forecast speaks the evening before -- prevention rather than detection.
    /// </summary>
    [Fact]
    public void Tomorrows_Forecast_Warns_The_Night_Before()
    {
        var tomorrow = new DateOnly(2027, 1, 15);

        var severe = new ColdForecast(
            tomorrow, 1.0, ColdAdvisory.Classify(1.0), "東京", "出典：気象庁");
        Assert.Contains("今夜のうちに", severe.Advice);

        var mild = new ColdForecast(
            tomorrow, 16.0, ColdAdvisory.Classify(16.0), "東京", "出典：気象庁");
        Assert.Null(mild.Advice);
    }
}

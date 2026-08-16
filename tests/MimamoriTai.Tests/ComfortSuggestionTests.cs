using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// The suggestion is the only part of the dashboard that offers to change something in
/// the house, so what it stays silent about matters as much as what it offers.
/// </summary>
public class ComfortSuggestionTests
{
    private static HeatAdvisory Heat(double wbgt) => new(
        wbgt,
        HeatAdvisory.Classify(wbgt),
        TemperatureC: 34.0,
        HumidityPercent: 60,
        ObservedAtUtc: DateTimeOffset.UtcNow,
        AreaName: "東京",
        Attribution: "環境省");

    private static ColdAdvisory Cold(double temperatureC) => new(
        temperatureC,
        ColdAdvisory.Classify(temperatureC),
        HumidityPercent: 40,
        ObservedAtUtc: DateTimeOffset.UtcNow,
        AreaName: "東京",
        Attribution: "気象庁");

    [Fact]
    public void Offers_cooling_when_it_is_hot_and_nothing_is_running()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(29.0),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac", SafetyClass: "Guarded")],
            cold: null,
            heating: null);

        Assert.NotNull(suggestion);
        Assert.Equal("living-ac", suggestion.Alias);
        Assert.Contains("エアコン", suggestion.Reason);
        Assert.True(suggestion.CanTurnOnRemotely);
        Assert.True(suggestion.NeedsHazardCheck);
    }

    [Fact]
    public void Says_nothing_when_the_air_conditioner_is_already_working()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(31.5),
            [new CoolingDevice("エアコン", IsOn: true, Watts: 480, Alias: "living-ac")],
            cold: null,
            heating: null);

        Assert.Null(suggestion);
    }

    [Fact]
    public void Says_nothing_on_a_comfortable_day()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(22.0),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac")],
            cold: null,
            heating: null);

        Assert.Null(suggestion);
    }

    /// <summary>
    /// 警戒 is below the band that raises an alert, on purpose. An alert interrupts a
    /// family at work and is reserved for a health risk; a button on a screen somebody
    /// opened themselves costs nothing, and this is already the band at which older
    /// people are asked to watch the indoor temperature.
    /// </summary>
    [Fact]
    public void Offers_cooling_one_band_earlier_than_the_alert()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(26.0),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac")],
            cold: null,
            heating: null);

        Assert.NotNull(suggestion);
        Assert.True(HeatAdvisory.Classify(26.0) < RiskAssessmentService.CoolingExpectedFrom);
    }

    /// <summary>
    /// A plug switched on at the wall but drawing nothing is the case the warning exists
    /// for, so the suggestion has to cover it too -- and say why, because "動いていません"
    /// about a switch the family can see is on would read as a bug.
    /// </summary>
    [Fact]
    public void Explains_a_plug_that_is_on_but_drawing_nothing()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(29.0),
            [new CoolingDevice("エアコン", IsOn: true, Watts: 0.2, Alias: "living-ac")],
            cold: null,
            heating: null);

        Assert.NotNull(suggestion);
        Assert.Contains("電気が使われていません", suggestion.Reason);
    }

    [Fact]
    public void Withholds_the_button_for_an_appliance_barred_from_remote_switch_on()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(29.0),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac", SafetyClass: "Restricted")],
            cold: null,
            heating: null);

        Assert.NotNull(suggestion);
        Assert.False(suggestion.CanTurnOnRemotely);
    }

    [Fact]
    public void Prefers_an_appliance_that_can_actually_be_switched_on()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(29.0),
            [
                new CoolingDevice("扇風機", IsOn: false, Watts: null, Alias: "living-fan", SafetyClass: "Restricted"),
                new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac", SafetyClass: "Guarded")
            ],
            cold: null,
            heating: null);

        Assert.NotNull(suggestion);
        Assert.Equal("living-ac", suggestion.Alias);
    }

    [Fact]
    public void Says_nothing_when_no_cooling_appliance_is_registered()
    {
        var suggestion = ComfortSuggestion.For(Heat(31.5), [], cold: null, heating: null);

        Assert.Null(suggestion);
    }

    [Fact]
    public void Offers_heating_on_a_cold_day_with_nothing_running()
    {
        var suggestion = ComfortSuggestion.For(
            heat: null,
            cooling: null,
            Cold(12.0),
            [new HeatingDevice("電気ストーブ", IsOn: false, Watts: null, Alias: "living-heater", SafetyClass: "Guarded")]);

        Assert.NotNull(suggestion);
        Assert.Equal("暖房をつけますか？", suggestion.Title);
        Assert.Equal("living-heater", suggestion.Alias);
    }

    /// <summary>
    /// Heat wins when both rules could fire, which in practice only happens with bad
    /// data. Indoor heatstroke moves in hours; a cold room does not.
    /// </summary>
    [Fact]
    public void Prefers_the_heat_case_over_the_cold_one()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(29.0),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null, Alias: "living-ac")],
            Cold(4.0),
            [new HeatingDevice("電気ストーブ", IsOn: false, Watts: null, Alias: "living-heater")]);

        Assert.NotNull(suggestion);
        Assert.Equal("冷房をつけますか？", suggestion.Title);
    }

    /// <summary>
    /// A device we cannot address is a device we cannot offer to switch on. Saying so
    /// would produce a button that fails when pressed.
    /// </summary>
    [Fact]
    public void Says_nothing_when_the_appliance_has_no_alias()
    {
        var suggestion = ComfortSuggestion.For(
            Heat(31.5),
            [new CoolingDevice("エアコン", IsOn: false, Watts: null)],
            cold: null,
            heating: null);

        Assert.Null(suggestion);
    }
}

using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

/// <summary>
/// The dashboard says 使用中 to tell the family someone is up and about. A plug that is
/// switched on while drawing nothing must not make that claim, or a household where the
/// appliance was unplugged hours ago still looks lively.
/// </summary>
public class DeviceCardStandbyTests
{
    private static DeviceCard Card(bool isOn, bool isStateKnown, double? watts) => new(
        Id: Guid.NewGuid(),
        Name: "プラグミニ",
        Alias: "プラグミニ",
        Room: "居間",
        DeviceType: "Plug Mini (JP)",
        IsOn: isOn,
        IsStateKnown: isStateKnown,
        LastUsedUtc: null,
        TodayUsageCount: 0,
        RemoteControlAllowed: true,
        SafetyClass: "safe",
        PowerWatts: watts);

    [Fact]
    public void On_With_Zero_Watts_Is_Standing_By()
    {
        Assert.True(Card(isOn: true, isStateKnown: true, watts: 0).IsStandingBy());
    }

    [Fact]
    public void On_With_Real_Draw_Is_Not_Standing_By()
    {
        Assert.False(Card(isOn: true, isStateKnown: true, watts: 13.17).IsStandingBy());
    }

    [Fact]
    public void On_Without_A_Wattage_Reading_Is_Not_Standing_By()
    {
        // Bots and other devices report no wattage at all; staying silent beats guessing.
        Assert.False(Card(isOn: true, isStateKnown: true, watts: null).IsStandingBy());
    }

    [Fact]
    public void Off_Is_Never_Standing_By()
    {
        Assert.False(Card(isOn: false, isStateKnown: true, watts: 0).IsStandingBy());
    }

    [Fact]
    public void Unknown_State_Is_Never_Standing_By()
    {
        // 確認中 must stay 確認中 - we do not know the relay is on.
        Assert.False(Card(isOn: true, isStateKnown: false, watts: 0).IsStandingBy());
    }
}

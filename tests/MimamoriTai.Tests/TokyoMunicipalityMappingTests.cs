using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// Families choose a ward or city; 気象庁 only publishes temperatures from a handful of
/// stations, none of them named after a ward. These tests hold the mapping that stands
/// between the two, because getting it wrong sends a household in 葛飾区 the weather from
/// the wrong side of Tokyo without anything on screen looking broken.
/// </summary>
public class TokyoMunicipalityMappingTests
{
    // The 東京都 stations that actually report a temperature, plus one from 神奈川県 and
    // one from 山梨県 so the "nearest station may be outside the prefecture" case is real.
    private static readonly AmedasStation[] Stations =
    [
        new("44132", "東京", 35.69, 139.76),
        new("44071", "練馬", 35.74, 139.59),
        new("44136", "江戸川臨海", 35.63, 139.86),
        new("44116", "府中", 35.68, 139.48),
        new("44112", "八王子", 35.66, 139.32),
        new("44056", "青梅", 35.79, 139.24),
        new("46106", "横浜", 35.44, 139.65),
        new("49142", "甲府", 35.67, 138.55),
    ];

    private static AmedasStation Nearest(string municipality)
    {
        var m = TokyoMunicipalities.Find(municipality)!;

        return Stations.OrderBy(s => s.DistanceKmTo(m.Latitude, m.Longitude)).First();
    }

    [Fact]
    public void Covers_All_Twenty_Three_Wards()
    {
        var wards = TokyoMunicipalities.All.Where(m => m.Group == TokyoMunicipalities.Wards).ToList();

        Assert.Equal(23, wards.Count);
        Assert.Contains(wards, w => w.Name == "世田谷区");
        Assert.Contains(wards, w => w.Name == "葛飾区");
    }

    [Fact]
    public void Lists_Every_Tokyo_Municipality_Exactly_Once()
    {
        var names = TokyoMunicipalities.All.Select(m => m.Name).ToList();

        Assert.Equal(62, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [InlineData("千代田区", "東京")]
    [InlineData("江戸川区", "江戸川臨海")]
    [InlineData("葛飾区", "東京")]
    [InlineData("練馬区", "練馬")]
    [InlineData("八王子市", "八王子")]
    [InlineData("青梅市", "青梅")]
    [InlineData("府中市", "府中")]
    public void Sends_Each_Place_To_A_Station_Someone_Local_Would_Expect(string place, string station) =>
        Assert.Equal(station, Nearest(place).Name);

    /// <summary>
    /// 町田市 juts down into 神奈川県. Restricting the search to station codes beginning
    /// "44" would hand it a reading from further away purely to keep the prefecture tidy.
    /// </summary>
    [Fact]
    public void Will_Cross_A_Prefecture_Border_For_A_Closer_Thermometer()
    {
        var machida = TokyoMunicipalities.Find("町田市")!;

        var nearest = Stations.OrderBy(s => s.DistanceKmTo(machida.Latitude, machida.Longitude)).First();
        var nearestInTokyo = Stations
            .Where(s => s.Code.StartsWith("44", StringComparison.Ordinal))
            .OrderBy(s => s.DistanceKmTo(machida.Latitude, machida.Longitude))
            .First();

        Assert.True(
            nearest.DistanceKmTo(machida.Latitude, machida.Longitude)
                <= nearestInTokyo.DistanceKmTo(machida.Latitude, machida.Longitude));
    }

    [Fact]
    public void Keeps_Every_Coordinate_Inside_Tokyo_Prefecture()
    {
        foreach (var m in TokyoMunicipalities.All)
        {
            // 沖ノ鳥島 and 南鳥島 belong to 小笠原村, so the box has to stay generous.
            Assert.InRange(m.Latitude, 24.0, 36.0);
            Assert.InRange(m.Longitude, 136.0, 154.0);
        }
    }

    [Fact]
    public void Finds_Nothing_For_A_Place_That_Is_Not_In_Tokyo() =>
        Assert.Null(TokyoMunicipalities.Find("横浜市"));

    [Fact]
    public void Ignores_A_Blank_Name() => Assert.Null(TokyoMunicipalities.Find(""));

    /// <summary>
    /// Islands are genuinely far from any mainland station. The picker still offers them,
    /// but the distance is surfaced so nobody reads the figure as local.
    /// </summary>
    [Fact]
    public void Is_Honest_About_How_Far_An_Island_Is_From_Its_Station()
    {
        var hachijo = TokyoMunicipalities.Find("八丈町")!;

        var km = Stations.Min(s => s.DistanceKmTo(hachijo.Latitude, hachijo.Longitude));

        Assert.True(km > 100, $"expected a long distance, got {km:0}km");
    }
}

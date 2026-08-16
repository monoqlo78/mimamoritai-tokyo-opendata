using MimamoriTai.Infrastructure.OpenData;
using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

/// <summary>
/// The AMeDAS station table decides which thermometer a family's warnings are read
/// from, so the two things that can quietly go wrong here -- picking a station that
/// does not measure temperature, and mangling the sexagesimal coordinates -- are
/// pinned down.
/// </summary>
public class AmedasStationCatalogTests
{
    // Trimmed from the real 気象庁 file. 世田谷 genuinely has elems "01000000": it
    // reports rainfall only, and offering it would leave a household with no figure.
    private const string Table = """
        {
          "44132": {"kjName":"東京","type":"s","lat":[35,41.4],"lon":[139,45.6],"elems":"11111111"},
          "44071": {"kjName":"練馬","type":"A","lat":[35,44.3],"lon":[139,35.5],"elems":"11112010"},
          "44126": {"kjName":"世田谷","type":"C","lat":[35,38.6],"lon":[139,40.4],"elems":"01000000"},
          "67326": {"kjName":"府中","type":"A","lat":[34,34.4],"lon":[133,14.1],"elems":"11112010"},
          "44116": {"kjName":"府中","type":"A","lat":[35,40.5],"lon":[139,28.7],"elems":"11112010"}
        }
        """;

    [Fact]
    public void Keeps_Only_Stations_That_Measure_Temperature()
    {
        var stations = AmedasStationCatalog.Parse(Table);

        Assert.DoesNotContain(stations, s => s.Code == "44126");
        Assert.Contains(stations, s => s.Code == "44132");
    }

    [Fact]
    public void Converts_Degrees_And_Minutes_To_Decimal_Degrees()
    {
        var tokyo = AmedasStationCatalog.Parse(Table).Single(s => s.Code == "44132");

        // 35°41.4' = 35 + 41.4/60
        Assert.Equal(35.69, tokyo.Latitude, 2);
        Assert.Equal(139.76, tokyo.Longitude, 2);
        Assert.Equal("東京", tokyo.Name);
    }

    [Fact]
    public void Survives_A_Truncated_Or_Empty_Table()
    {
        Assert.Empty(AmedasStationCatalog.Parse(""));
        Assert.Empty(AmedasStationCatalog.Parse("{"));
    }

    /// <summary>
    /// 府中 exists in both 東京都 and 広島県. Choosing by name alone would send a Tokyo
    /// family the weather from 600km away, which is the whole reason the picker resolves
    /// a coordinate rather than a place name.
    /// </summary>
    [Fact]
    public void Distance_Separates_Two_Stations_That_Share_A_Name()
    {
        var stations = AmedasStationCatalog.Parse(Table).Where(s => s.Name == "府中").ToList();

        var nearest = stations.OrderBy(s => s.DistanceKmTo(35.69, 139.76)).First();

        Assert.Equal("44116", nearest.Code);
    }
}

/// <summary>
/// The overlay puts watt-hours and degrees on one frame. These tests hold the two
/// promises that make it honest: a day with no observation leaves a gap instead of
/// diving to freezing, and a steady week is not magnified into a dramatic swing.
/// </summary>
public class WeatherOverlayGeometryTests
{
    private static WeatherOverlayPoint Day(string label, double wh, double? low, double? high) =>
        new(label, wh, low, high);

    [Fact]
    public void Skips_Days_With_No_Observation_Rather_Than_Drawing_Them_As_Zero()
    {
        List<WeatherOverlayPoint> points =
        [
            Day("1/1", 800, 2, 9),
            Day("1/2", 900, null, null),
            Day("1/3", 700, 3, 10),
        ];

        var scale = WeatherOverlayGeometry.DegreeScale(points);
        var line = WeatherOverlayGeometry.Line(points, high: false, scale);

        Assert.Equal(2, line.Split(' ').Length);
    }

    [Fact]
    public void Marks_A_Lone_Reading_That_No_Line_Could_Show()
    {
        // A single day is what a household sees on the day it signs up. A polyline of one
        // point draws nothing, so without dots the chart would come up empty next to a
        // temperature axis and read as broken.
        List<WeatherOverlayPoint> points = [Day("1/1", 800, 23.8, 24.7)];

        var scale = WeatherOverlayGeometry.DegreeScale(points);

        // One coordinate: a polyline of a single point is a valid element that paints
        // nothing at all, which is exactly what the household saw.
        Assert.Single(WeatherOverlayGeometry.Line(points, high: true, scale).Split(' '));
        Assert.Single(WeatherOverlayGeometry.Markers(points, high: true, scale));
        Assert.Single(WeatherOverlayGeometry.Markers(points, high: false, scale));
    }

    [Fact]
    public void Does_Not_Mark_A_Day_With_No_Observation()
    {
        List<WeatherOverlayPoint> points =
        [
            Day("1/1", 800, 2, 9),
            Day("1/2", 900, null, null),
            Day("1/3", 700, 3, 10),
        ];

        var scale = WeatherOverlayGeometry.DegreeScale(points);

        Assert.Equal(2, WeatherOverlayGeometry.Markers(points, high: true, scale).Count);
    }

    [Fact]
    public void Puts_A_Marker_Exactly_Where_The_Line_Turns()
    {
        List<WeatherOverlayPoint> points = [Day("1/1", 800, 2, 9), Day("1/2", 900, 3, 10)];

        var scale = WeatherOverlayGeometry.DegreeScale(points);
        var markers = WeatherOverlayGeometry.Markers(points, high: true, scale);

        Assert.Equal(WeatherOverlayGeometry.CenterX(0, 2), markers[0].X, 6);
        Assert.Equal(WeatherOverlayGeometry.DegreeY(9, scale), markers[0].Y, 6);
    }

    [Fact]
    public void Widens_A_Flat_Week_So_Noise_Is_Not_Magnified()
    {
        List<WeatherOverlayPoint> points =
        [
            Day("1/1", 800, 5.0, 5.2),
            Day("1/2", 810, 5.1, 5.3),
        ];

        var (low, high) = WeatherOverlayGeometry.DegreeScale(points);

        Assert.True(high - low >= WeatherOverlayGeometry.MinDegreeSpan);
    }

    [Fact]
    public void Puts_The_Warmer_Reading_Above_The_Colder_One()
    {
        List<WeatherOverlayPoint> points = [Day("1/1", 800, 2, 12)];

        var scale = WeatherOverlayGeometry.DegreeScale(points);

        Assert.True(
            WeatherOverlayGeometry.DegreeY(12, scale) < WeatherOverlayGeometry.DegreeY(2, scale));
    }

    [Fact]
    public void Gives_A_Day_With_No_Electricity_A_Visible_Bar()
    {
        Assert.Equal(
            WeatherOverlayGeometry.MinBarHeight,
            WeatherOverlayGeometry.BarHeight(0, 1200));
    }

    [Fact]
    public void Keeps_Bars_Inside_The_Plot_Even_If_A_Value_Exceeds_The_Stated_Max()
    {
        var height = WeatherOverlayGeometry.BarHeight(5000, 1200);

        Assert.True(height <= WeatherOverlayGeometry.PlotBottom - WeatherOverlayGeometry.PlotTop);
    }

    [Fact]
    public void Needs_Two_Days_Before_It_Draws_A_Band()
    {
        var one = new List<WeatherOverlayPoint> { Day("1/1", 800, 2, 9) };

        Assert.Equal(string.Empty, WeatherOverlayGeometry.Band(one, WeatherOverlayGeometry.DegreeScale(one)));
    }

    /// <summary>
    /// A ja-JP thread must not emit "1,5" into an SVG attribute: the browser drops the
    /// whole shape and the card renders empty.
    /// </summary>
    [Fact]
    public void Formats_Numbers_With_A_Dot_Whatever_The_Culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", WeatherOverlayGeometry.F(1.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Says_So_When_A_Day_Has_No_Temperature()
    {
        var text = WeatherOverlayGeometry.Describe(Day("1/2", 900, null, null));

        Assert.Contains("気温の記録なし", text);
    }

    [Theory]
    [InlineData(0.9, 1)]
    [InlineData(1.7, 2)]
    [InlineData(2.3, 2.5)]
    [InlineData(4.0, 5)]
    [InlineData(7.0, 10)]
    [InlineData(230, 250)]
    public void Rounds_A_Step_Up_To_A_Number_People_Read_Easily(double raw, double expected) =>
        Assert.Equal(expected, WeatherOverlayGeometry.NiceStep(raw));

    /// <summary>
    /// The two axes only mean anything side by side if they hang off the same gridlines,
    /// so both are forced to exactly four equal steps.
    /// </summary>
    [Fact]
    public void Builds_Both_Axes_With_The_Same_Number_Of_Steps()
    {
        var energy = WeatherOverlayGeometry.Axis(0, 1180);
        var degrees = WeatherOverlayGeometry.Axis(1.4, 12.6);

        Assert.Equal(energy.Low + (energy.Step * WeatherOverlayGeometry.Divisions), energy.High, 6);
        Assert.Equal(degrees.Low + (degrees.Step * WeatherOverlayGeometry.Divisions), degrees.High, 6);
    }

    [Fact]
    public void Never_Leaves_A_Value_Above_The_Top_Gridline()
    {
        foreach (var (low, high) in new[] { (0d, 1180d), (1.4, 12.6), (-3.2, 4.1), (0d, 7d), (18d, 18.2) })
        {
            var axis = WeatherOverlayGeometry.Axis(low, high);

            Assert.True(axis.High >= high - 1e-9, $"{high} did not fit under {axis.High}");
            Assert.True(axis.Low <= low + 1e-9, $"{low} fell below {axis.Low}");
        }
    }

    [Fact]
    public void Starts_The_Electricity_Axis_At_Zero()
    {
        var axis = WeatherOverlayGeometry.Axis(0, 1180);

        Assert.Equal(0, axis.Low);
    }

    [Fact]
    public void Places_Gridlines_From_The_Baseline_Up()
    {
        Assert.Equal(WeatherOverlayGeometry.PlotBottom, WeatherOverlayGeometry.TickY(0));
        Assert.Equal(WeatherOverlayGeometry.PlotTop, WeatherOverlayGeometry.TickY(WeatherOverlayGeometry.Divisions), 6);
        Assert.True(WeatherOverlayGeometry.TickY(1) > WeatherOverlayGeometry.TickY(2));
    }

    [Fact]
    public void Shortens_Long_Watt_Hour_Labels_To_Kilowatt_Hours()
    {
        Assert.Equal("900", WeatherOverlayGeometry.EnergyTick(900));
        Assert.Equal("1.2k", WeatherOverlayGeometry.EnergyTick(1200));
    }
}

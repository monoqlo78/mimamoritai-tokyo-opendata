using MimamoriTai.Infrastructure.OpenData;

namespace MimamoriTai.Tests;

/// <summary>
/// Parsing of the two public feeds the heatstroke rule runs on. Both fixtures are the
/// real shape returned by the live endpoints on 2026-08-16, trimmed to the columns that
/// matter, so a change in either format shows up here rather than in production.
/// </summary>
public class OpenDataParsingTests
{
    private const string WbgtCsv =
        ",,2026081615,2026081618,2026081621,2026081624,2026081703\n" +
        "44046,2026/08/16 12:25, 210, 210, 200, 200, 180\n" +
        "44132,2026/08/16 12:25, 270, 240, 230, 220, 220\n";

    private static DateTimeOffset Jst(int day, int hour) =>
        new(new DateTime(2026, 8, day, hour, 0, 0), TimeSpan.FromHours(9));

    [Fact]
    public void Reads_Tenths_Of_A_Degree_For_The_Requested_Point()
    {
        var reading = TokyoHeatAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(16, 15), 3);

        Assert.NotNull(reading);
        Assert.Equal(27.0, reading!.Value.Wbgt);
        Assert.Equal(Jst(16, 15), reading.Value.AtLocal);
    }

    [Fact]
    public void Picks_The_Column_Nearest_To_Now()
    {
        // 20:00 sits between the 18:00 and 21:00 columns, an hour from the later one.
        var reading = TokyoHeatAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(16, 20), 3);

        Assert.NotNull(reading);
        Assert.Equal(23.0, reading!.Value.Wbgt);
        Assert.Equal(Jst(16, 21), reading.Value.AtLocal);
    }

    /// <summary>
    /// The government writes midnight closing a day as hour 24, which .NET will not
    /// parse. It has to become 00:00 the next morning or the last column is silently lost.
    /// </summary>
    [Fact]
    public void Normalises_Hour_24_To_Midnight_The_Next_Day()
    {
        var reading = TokyoHeatAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(17, 0), 1);

        Assert.NotNull(reading);
        Assert.Equal(22.0, reading!.Value.Wbgt);
        Assert.Equal(Jst(17, 0), reading.Value.AtLocal);
    }

    /// <summary>
    /// Out of season the file still exists but stops covering today. A stale column must
    /// not be dressed up as the current index.
    /// </summary>
    [Fact]
    public void Refuses_A_Column_Outside_The_Tolerance()
    {
        Assert.Null(TokyoHeatAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(20, 12), 3));
    }

    [Fact]
    public void Returns_Nothing_For_An_Unknown_Point()
    {
        Assert.Null(TokyoHeatAdvisoryProvider.ParseWbgt(WbgtCsv, "99999", Jst(16, 15), 3));
    }

    [Fact]
    public void Survives_An_Empty_Or_Truncated_File()
    {
        Assert.Null(TokyoHeatAdvisoryProvider.ParseWbgt("", "44132", Jst(16, 15), 3));
        Assert.Null(TokyoHeatAdvisoryProvider.ParseWbgt(",,2026081615\n", "44132", Jst(16, 15), 3));
    }

    [Fact]
    public void Reads_Temperature_And_Humidity_From_The_Amedas_Map()
    {
        const string json = """
            {"44132":{"temp":[25.8,0],"humidity":[77,0],"wind":[2.7,0]}}
            """;

        var (temp, humidity) = TokyoHeatAdvisoryProvider.ParseAmedas(json, "44132");

        Assert.Equal(25.8, temp);
        Assert.Equal(77, humidity);
    }

    /// <summary>
    /// The second element is the instrument's own quality flag. Non-zero means it does
    /// not vouch for the reading, so neither do we.
    /// </summary>
    [Fact]
    public void Drops_A_Reading_The_Instrument_Flags_As_Suspect()
    {
        const string json = """
            {"44132":{"temp":[25.8,1],"humidity":[77,0]}}
            """;

        var (temp, humidity) = TokyoHeatAdvisoryProvider.ParseAmedas(json, "44132");

        Assert.Null(temp);
        Assert.Equal(77, humidity);
    }

    [Fact]
    public void Returns_Nothing_When_The_Point_Is_Missing()
    {
        var (temp, humidity) = TokyoHeatAdvisoryProvider.ParseAmedas("""{"44071":{"temp":[25.8,0]}}""", "44132");

        Assert.Null(temp);
        Assert.Null(humidity);
    }
}

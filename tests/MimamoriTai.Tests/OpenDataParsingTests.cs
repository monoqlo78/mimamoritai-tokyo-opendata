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
        var reading = TokyoWeatherAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(16, 15), 3);

        Assert.NotNull(reading);
        Assert.Equal(27.0, reading!.Value.Wbgt);
        Assert.Equal(Jst(16, 15), reading.Value.AtLocal);
    }

    [Fact]
    public void Picks_The_Column_Nearest_To_Now()
    {
        // 20:00 sits between the 18:00 and 21:00 columns, an hour from the later one.
        var reading = TokyoWeatherAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(16, 20), 3);

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
        var reading = TokyoWeatherAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(17, 0), 1);

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
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseWbgt(WbgtCsv, "44132", Jst(20, 12), 3));
    }

    [Fact]
    public void Returns_Nothing_For_An_Unknown_Point()
    {
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseWbgt(WbgtCsv, "99999", Jst(16, 15), 3));
    }

    [Fact]
    public void Survives_An_Empty_Or_Truncated_File()
    {
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseWbgt("", "44132", Jst(16, 15), 3));
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseWbgt(",,2026081615\n", "44132", Jst(16, 15), 3));
    }

    [Fact]
    public void Reads_Temperature_And_Humidity_From_The_Amedas_Map()
    {
        const string json = """
            {"44132":{"temp":[25.8,0],"humidity":[77,0],"wind":[2.7,0]}}
            """;

        var (temp, humidity) = TokyoWeatherAdvisoryProvider.ParseAmedas(json, "44132");

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

        var (temp, humidity) = TokyoWeatherAdvisoryProvider.ParseAmedas(json, "44132");

        Assert.Null(temp);
        Assert.Equal(77, humidity);
    }

    [Fact]
    public void Returns_Nothing_When_The_Point_Is_Missing()
    {
        var (temp, humidity) = TokyoWeatherAdvisoryProvider.ParseAmedas("""{"44071":{"temp":[25.8,0]}}""", "44132");

        Assert.Null(temp);
        Assert.Null(humidity);
    }
    /// <summary>
    /// The 気象庁 forecast JSON, trimmed to the temperature block. Unlike WBGT this feed
    /// is published all year, which is why the cold half of the watch is built on it:
    /// the 暑さ指数 series simply does not exist from late October to late April.
    /// </summary>
    private const string ForecastJson = """
        [{"timeSeries":[
          {"timeDefines":["2027-01-14T09:00:00+09:00","2027-01-15T00:00:00+09:00","2027-01-15T09:00:00+09:00"],
           "areas":[{"area":{"name":"東京","code":"44132"},"temps":["11","2","9"]},
                    {"area":{"name":"大島","code":"44172"},"temps":["13","7","12"]}]}
        ]}]
        """;

    /// <summary>
    /// Convention in this feed is that the 00:00 stamp carries the day's low and 09:00
    /// the high, but the array shifts as the day advances -- yesterday's entries are
    /// dropped, so the index that held the low this morning holds a high by evening.
    /// Reading by position would silently start warning about the wrong number, so the
    /// parser takes the minimum of everything stamped for the day instead.
    /// </summary>
    [Fact]
    public void Reads_Tomorrows_Low_Regardless_Of_Its_Position_In_The_Array()
    {
        var low = TokyoWeatherAdvisoryProvider.ParseForecastLow(
            ForecastJson, "44132", new DateOnly(2027, 1, 15));

        Assert.Equal(2, low);
    }

    [Fact]
    public void Reads_The_Low_For_The_Requested_Point_Only()
    {
        var low = TokyoWeatherAdvisoryProvider.ParseForecastLow(
            ForecastJson, "44172", new DateOnly(2027, 1, 15));

        Assert.Equal(7, low);
    }

    [Fact]
    public void Returns_Nothing_When_The_Forecast_Does_Not_Reach_That_Day()
    {
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseForecastLow(
            ForecastJson, "44132", new DateOnly(2027, 1, 20)));
    }

    [Fact]
    public void Returns_Nothing_For_An_Unknown_Forecast_Point()
    {
        Assert.Null(TokyoWeatherAdvisoryProvider.ParseForecastLow(
            ForecastJson, "99999", new DateOnly(2027, 1, 15)));
    }

    /// <summary>
    /// Temperatures arrive as strings, and the feed uses an empty one where it has no
    /// figure. Parsing that as zero would read as a freezing night and send a family a
    /// warning about weather that was never forecast.
    /// </summary>
    [Fact]
    public void Ignores_A_Blank_Temperature_Rather_Than_Reading_It_As_Zero()
    {
        const string json = """
            [{"timeSeries":[
              {"timeDefines":["2027-01-15T00:00:00+09:00","2027-01-15T09:00:00+09:00"],
               "areas":[{"area":{"name":"東京","code":"44132"},"temps":["","9"]}]}
            ]}]
            """;

        var low = TokyoWeatherAdvisoryProvider.ParseForecastLow(
            json, "44132", new DateOnly(2027, 1, 15));

        Assert.Equal(9, low);
    }
}

/// <summary>
/// One station's own published history, which is what fills in the days before the app
/// was watching. Fixture is the real shape of a three-hour block from 2026-08-15.
/// </summary>
public class AmedasHistoryParsingTests
{
    private const string Block = """
        {
          "20260815120000": {"temp":[26.5,0],"humidity":[70,0],"pressure":[1008.4,0]},
          "20260815121000": {"temp":[26.8,0],"humidity":[69,0]},
          "20260815122000": {"temp":[27.1,1],"humidity":[68,0]},
          "20260815123000": {"humidity":[68,0]}
        }
        """;

    [Fact]
    public void Reads_Every_Ten_Minute_Temperature_In_The_Block()
    {
        var readings = AmedasHistoryProvider.Parse(Block);

        Assert.Equal(2, readings.Count);
        Assert.Equal(26.5, readings[0].TemperatureC);
        Assert.Equal(70, readings[0].HumidityPercent);
    }

    /// <summary>
    /// Keys are JST with no offset on them. Reading them as UTC would file every
    /// observation nine hours early, which lands an afternoon reading on the previous day's
    /// chart and quietly shifts the daily high and low.
    /// </summary>
    [Fact]
    public void Treats_The_Key_As_Japan_Time()
    {
        var readings = AmedasHistoryProvider.Parse(Block);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero),
            readings[0].ObservedAtUtc);
    }

    /// <summary>
    /// A non-zero quality flag means the instrument itself is unsure. Charting it anyway
    /// would put a spike on the screen that the station never stood behind.
    /// </summary>
    [Fact]
    public void Drops_A_Reading_The_Station_Flagged_As_Suspect()
    {
        var readings = AmedasHistoryProvider.Parse(Block);

        Assert.DoesNotContain(readings, r => r.TemperatureC == 27.1);
    }

    [Fact]
    public void Returns_Nothing_Rather_Than_Throwing_On_A_Broken_Body()
    {
        Assert.Empty(AmedasHistoryProvider.Parse("<html>404</html>"));
    }
}

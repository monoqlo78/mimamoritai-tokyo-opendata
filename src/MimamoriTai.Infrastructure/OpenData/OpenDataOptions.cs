namespace MimamoriTai.Infrastructure.OpenData;

/// <summary>
/// Where the public heat figures come from, and for which observation point.
///
/// <para>
/// Defaults point at Tokyo (AMeDAS/WBGT station 44132), because that is the area this
/// service watches. Everything is overridable from configuration so a household in
/// another part of the country -- or a test -- can point somewhere else without a
/// rebuild.
/// </para>
/// </summary>
public sealed class OpenDataOptions
{
    public const string SectionName = "OpenData";

    /// <summary>Master switch. Off means the app behaves exactly as it did before.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 環境省 熱中症予防情報サイト: WBGT (暑さ指数) forecast for the Tokyo points, refreshed
    /// roughly hourly. Values are tenths of a degree, so 270 means 27.0.
    /// </summary>
    public string WbgtCsvUrl { get; set; } = "https://www.wbgt.env.go.jp/prev15WG/dl/yohou_tokyo.csv";

    /// <summary>気象庁 AMeDAS: the timestamp of the most recent nationwide observation.</summary>
    public string AmedasLatestTimeUrl { get; set; } = "https://www.jma.go.jp/bosai/amedas/data/latest_time.txt";

    /// <summary>
    /// 気象庁 AMeDAS: nationwide observation map. <c>{0}</c> is the yyyyMMddHHmmss stamp
    /// returned by <see cref="AmedasLatestTimeUrl"/>.
    /// </summary>
    public string AmedasMapUrlFormat { get; set; } = "https://www.jma.go.jp/bosai/amedas/data/map/{0}.json";

    /// <summary>
    /// 気象庁 天気予報: the three-day forecast for 東京都 (area 130000), which carries
    /// tomorrow's expected minimum temperature. Published all year, unlike WBGT.
    /// </summary>
    public string ForecastJsonUrl { get; set; } = "https://www.jma.go.jp/bosai/forecast/data/forecast/130000.json";

    /// <summary>Observation point. 44132 is 東京 for both sources, which is why one code covers both.</summary>
    public string PointCode { get; set; } = "44132";

    public string AreaName { get; set; } = "東京";

    /// <summary>
    /// How long a fetched figure is reused. WBGT is republished about hourly and AMeDAS
    /// every ten minutes, so half an hour keeps the screen current while making sure a
    /// busy dashboard never hammers a government website.
    /// </summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>
    /// How far ahead of now a WBGT forecast column may sit and still be treated as
    /// "current". The series is three-hourly, so one step is the natural tolerance.
    /// </summary>
    public int ForecastToleranceHours { get; set; } = 3;

    public string Attribution { get; set; } = "出典：環境省熱中症予防情報サイト／気象庁";

    /// <summary>
    /// Credit for the sources that carry no WBGT. Kept separate so the winter card does
    /// not credit 環境省 for a figure that came from 気象庁 alone.
    /// </summary>
    public string AmedasAttribution { get; set; } = "出典：気象庁";
}

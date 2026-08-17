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

    /// <summary>
    /// 気象庁 AMeDAS: the published station table (coordinates and measured elements
    /// for every station nationwide). Used to turn a phone's GPS fix into the station
    /// nearest the house, and to list the stations around it by name.
    /// </summary>
    public string AmedasStationTableUrl { get; set; } = "https://www.jma.go.jp/bosai/amedas/const/amedastable.json";

    /// <summary>
    /// 気象庁 AMeDAS: one station's own observations, in three-hour files of ten-minute
    /// readings. <c>{0}</c> is the point code, <c>{1}</c> the JST date as yyyyMMdd and
    /// <c>{2}</c> the JST hour the block starts at, as one of 00/03/.../21.
    ///
    /// <para>
    /// The nationwide map only ever carries the newest observation, so this is the only
    /// way to learn what yesterday was like. Used to fill in the days before the app was
    /// watching, which is what makes the weather overlay readable on the day a family
    /// signs up rather than a fortnight later.
    /// </para>
    /// </summary>
    public string AmedasPointHistoryUrlFormat { get; set; } =
        "https://www.jma.go.jp/bosai/amedas/data/point/{0}/{1}_{2}.json";

    /// <summary>
    /// How many days of station history to fill in on startup. Matches the fortnight the
    /// dashboard charts, and is the cap on how much of a public website this app will
    /// read in one go.
    /// </summary>
    public int HistoryBackfillDays { get; set; } = 14;

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

    /// <summary>
    /// 気象庁 警報・注意報: the current warning state for 東京都 (area 130000). Read for
    /// 特別警報 only -- the ordinary 注意報 in this file change several times a day and
    /// are not something to push to a family's phone.
    /// </summary>
    public string WarningJsonUrl { get; set; } = "https://www.jma.go.jp/bosai/warning/data/warning/130000.json";

    /// <summary>
    /// 気象庁 防災情報XML「随時」フィード. Carries 土砂災害警戒情報 and 顕著な大雨に関する
    /// 気象情報 (線状降水帯), neither of which appears in the warning JSON above.
    /// </summary>
    public string DisasterFeedUrl { get; set; } = "https://www.data.jma.go.jp/developer/xml/feed/extra.xml";

    /// <summary>
    /// 気象庁 地震情報一覧. Nationwide, so entries are filtered down to
    /// <see cref="PrefectureCode"/> before anything is shown.
    /// </summary>
    public string QuakeListUrl { get; set; } = "https://www.jma.go.jp/bosai/quake/data/list.json";

    /// <summary>
    /// 都道府県コード for the household's area, as 気象庁 numbers them. "13" is 東京都; the
    /// six-digit "130000" form used by the warning feed is derived from it.
    /// </summary>
    public string PrefectureCode { get; set; } = "13";

    /// <summary>
    /// The weakest 震度 worth telling a family about. Below 5弱 the published guidance is
    /// that furniture does not move, so a push would be noise -- and noise is what makes
    /// the heatstroke alert get muted. Accepts 気象庁 notation: 1..4, 5-, 5+, 6-, 6+, 7.
    /// </summary>
    public string MinimumQuakeIntensity { get; set; } = "5-";

    /// <summary>
    /// How long emergency information is reused. Shorter than <see cref="CacheMinutes"/>
    /// because this is the one class of figure where being ten minutes late matters.
    /// </summary>
    public int DisasterCacheMinutes { get; set; } = 5;

    /// <summary>
    /// How far back an advisory still counts as "active". 気象庁 leaves an entry in the
    /// feed long after the rain has stopped, and a family does not need to hear about
    /// yesterday's warning at breakfast.
    /// </summary>
    public int DisasterActiveHours { get; set; } = 6;

    public string Attribution { get; set; } = "出典：環境省熱中症予防情報サイト／気象庁";

    /// <summary>
    /// Credit for the sources that carry no WBGT. Kept separate so the winter card does
    /// not credit 環境省 for a figure that came from 気象庁 alone.
    /// </summary>
    public string AmedasAttribution { get; set; } = "出典：気象庁";
}

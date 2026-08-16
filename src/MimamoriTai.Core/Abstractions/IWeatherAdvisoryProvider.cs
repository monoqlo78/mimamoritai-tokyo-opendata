namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// The five bands the Ministry of the Environment publishes WBGT (暑さ指数) against.
/// Kept as the government's own bands rather than a scale of our own invention so the
/// wording a family reads on screen matches the wording they hear on the news.
/// </summary>
public enum HeatAlertLevel
{
    Unknown = 0,
    /// <summary>WBGT &lt; 21. ほぼ安全.</summary>
    Safe = 1,
    /// <summary>21 ≤ WBGT &lt; 25. 注意.</summary>
    Caution = 2,
    /// <summary>25 ≤ WBGT &lt; 28. 警戒.</summary>
    Warning = 3,
    /// <summary>28 ≤ WBGT &lt; 31. 厳重警戒. 外出は控え、室内でも室温上昇に注意.</summary>
    SevereWarning = 4,
    /// <summary>31 ≤ WBGT. 危険. 高齢者は安静状態でも発症する危険性が大きい.</summary>
    Danger = 5
}

/// <summary>
/// How cold it is outside, in bands chosen for what an older person living alone has
/// to do about it rather than for meteorological interest.
///
/// <para>
/// There is no government "cold index" equivalent to WBGT to borrow, so these are our
/// own bands anchored to two published figures: WHO's housing guideline of keeping
/// indoor temperature at or above 18°C, and the winter concentration of bathing
/// deaths among older people that 消費者庁 warns about (ヒートショック). An unheated
/// Japanese home tracks the outdoor temperature closely, so the outdoor figure is a
/// usable stand-in for "this room needs heating now".
/// </para>
/// </summary>
public enum ColdAlertLevel
{
    Unknown = 0,
    /// <summary>15°C 以上. 穏やか. 何も言うことはない.</summary>
    Mild = 1,
    /// <summary>10 ≤ t &lt; 15. 肌寒い. 一枚羽織る程度.</summary>
    Chilly = 2,
    /// <summary>5 ≤ t &lt; 10. 冷え込み. 暖房なしでは室温18°Cを保ちにくい.</summary>
    Cold = 3,
    /// <summary>t &lt; 5. 厳しい冷え込み. 入浴時のヒートショックと低体温症に警戒.</summary>
    SevereCold = 4
}

/// <summary>
/// The outdoor heat picture for the household's area, as published by public open data.
/// </summary>
/// <param name="Wbgt">暑さ指数 (WBGT) in degrees Celsius.</param>
/// <param name="Level">The Ministry of the Environment band <paramref name="Wbgt"/> falls in.</param>
/// <param name="TemperatureC">Observed air temperature, when an observation was available.</param>
/// <param name="HumidityPercent">Observed relative humidity, when available.</param>
/// <param name="ObservedAtUtc">When the underlying figure was published/observed.</param>
/// <param name="AreaName">Human readable name of the observation point.</param>
/// <param name="Attribution">Credit line that must be shown wherever this is displayed.</param>
public sealed record HeatAdvisory(
    double Wbgt,
    HeatAlertLevel Level,
    double? TemperatureC,
    double? HumidityPercent,
    DateTimeOffset ObservedAtUtc,
    string AreaName,
    string Attribution)
{
    /// <summary>The band boundaries published by 環境省熱中症予防情報サイト.</summary>
    public static HeatAlertLevel Classify(double wbgt) => wbgt switch
    {
        >= 31 => HeatAlertLevel.Danger,
        >= 28 => HeatAlertLevel.SevereWarning,
        >= 25 => HeatAlertLevel.Warning,
        >= 21 => HeatAlertLevel.Caution,
        _ => HeatAlertLevel.Safe
    };

    public static string LevelLabel(HeatAlertLevel level) => level switch
    {
        HeatAlertLevel.Danger => "危険",
        HeatAlertLevel.SevereWarning => "厳重警戒",
        HeatAlertLevel.Warning => "警戒",
        HeatAlertLevel.Caution => "注意",
        HeatAlertLevel.Safe => "ほぼ安全",
        _ => "不明"
    };

    /// <summary>
    /// One plain sentence telling the resident what to actually do, or null when there
    /// is nothing worth saying. Written as advice to the person at home, not as a
    /// description of the weather -- a number on its own has never made anyone drink
    /// water. Silent below 警戒 on purpose: advice that appears every day stops being
    /// read on the day it matters.
    /// </summary>
    public static string? AdviceFor(HeatAlertLevel level) => level switch
    {
        HeatAlertLevel.Danger => "無理せず涼しいお部屋へ。エアコンをつけて、水分をこまめにとりましょう",
        HeatAlertLevel.SevereWarning => "エアコンをつけて、のどが渇く前に水分をとりましょう",
        HeatAlertLevel.Warning => "暑くなってきました。水分をこまめにとりましょう",
        _ => null
    };

    public string LevelText => LevelLabel(Level);

    public string? Advice => AdviceFor(Level);
}

/// <summary>
/// The outdoor cold picture for the household's area.
///
/// <para>
/// Separate from <see cref="HeatAdvisory"/> and not a variant of it, because the two
/// rest on different data with different lifetimes: WBGT is only published from late
/// April to late October, while the 気象庁 AMeDAS temperature this is built from runs
/// all year. Winter is exactly when an older person living alone is most at risk, so
/// the cold side cannot be allowed to inherit the heat side's seasonal gap.
/// </para>
/// </summary>
public sealed record ColdAdvisory(
    double TemperatureC,
    ColdAlertLevel Level,
    double? HumidityPercent,
    DateTimeOffset ObservedAtUtc,
    string AreaName,
    string Attribution)
{
    public static ColdAlertLevel Classify(double temperatureC) => temperatureC switch
    {
        < 5 => ColdAlertLevel.SevereCold,
        < 10 => ColdAlertLevel.Cold,
        < 15 => ColdAlertLevel.Chilly,
        _ => ColdAlertLevel.Mild
    };

    public static string LevelLabel(ColdAlertLevel level) => level switch
    {
        ColdAlertLevel.SevereCold => "厳しい冷え込み",
        ColdAlertLevel.Cold => "冷え込み",
        ColdAlertLevel.Chilly => "肌寒い",
        ColdAlertLevel.Mild => "穏やか",
        _ => "不明"
    };

    /// <summary>
    /// The bathing advice is deliberately the loudest one. Most deaths that happen in
    /// the bath in winter are of older people, and the danger is the temperature step
    /// between a warm room and a cold changing room -- something a resident can fix in
    /// a minute if anyone reminds them.
    /// </summary>
    public static string? AdviceFor(ColdAlertLevel level) => level switch
    {
        ColdAlertLevel.SevereCold => "お風呂の前に脱衣所と浴室を暖めましょう（ヒートショックにご注意）",
        ColdAlertLevel.Cold => "暖房をつけて、お部屋を18℃以上に保ちましょう",
        ColdAlertLevel.Chilly => "朝晩が冷えます。一枚多く羽織りましょう",
        _ => null
    };

    public string LevelText => LevelLabel(Level);

    public string? Advice => AdviceFor(Level);
}

/// <summary>
/// Tomorrow morning's expected low for the household's area.
///
/// <para>
/// This is the piece that turns the service from noticing into preventing. A cold
/// snap is knowable the evening before, and the two things that protect an older
/// person -- warming the changing room before a bath, and not letting the bedroom
/// drop overnight -- both have to be done *before* the cold arrives. Telling someone
/// at 6am that it is already 2°C is too late to be kind.
/// </para>
/// </summary>
/// <param name="ForDateLocal">The local date the low is forecast for.</param>
/// <param name="MinTemperatureC">Forecast minimum air temperature.</param>
/// <param name="Level">The band <paramref name="MinTemperatureC"/> falls in.</param>
public sealed record ColdForecast(
    DateOnly ForDateLocal,
    double MinTemperatureC,
    ColdAlertLevel Level,
    string AreaName,
    string Attribution)
{
    /// <summary>
    /// The evening nudge. Silent unless the morning is actually going to be cold,
    /// because a message that arrives every night is a message nobody opens.
    /// </summary>
    public static string? AdviceFor(ColdAlertLevel level) => level switch
    {
        ColdAlertLevel.SevereCold =>
            "明日の朝は厳しい冷え込みです。今夜のうちに脱衣所と浴室を暖める準備をしておきましょう",
        ColdAlertLevel.Cold =>
            "明日の朝は冷え込みます。寝室の暖房と、朝の一枚の準備をしておきましょう",
        _ => null
    };

    public string LevelText => ColdAdvisory.LevelLabel(Level);

    public string? Advice => AdviceFor(Level);
}

/// <summary>
/// Supplies the current outdoor advisories for the household's area from public open
/// data.
///
/// <para>
/// Implementations must fail soft: when the source is unreachable, out of season, or
/// returns something unexpected they return <c>null</c> rather than throwing, because a
/// government website being down must never stop the watch service from running.
/// </para>
/// </summary>
public interface IWeatherAdvisoryProvider
{
    /// <summary>Null out of season, when WBGT is simply not published.</summary>
    Task<HeatAdvisory?> GetHeatAsync(CancellationToken ct = default);

    /// <summary>Available all year, since it rests on the AMeDAS observation alone.</summary>
    Task<ColdAdvisory?> GetColdAsync(CancellationToken ct = default);

    /// <summary>Tomorrow's forecast low, for advice that arrives in time to act on.</summary>
    Task<ColdForecast?> GetTomorrowColdAsync(CancellationToken ct = default);

    /// <summary>
    /// The same observation, read for one household's own AMeDAS station.
    ///
    /// <para>
    /// Defaulted rather than required so that a provider which only knows one place --
    /// including every test double -- keeps working unchanged. Implementations that can
    /// answer per station override it; the nationwide observation map is a single file,
    /// so doing so costs no extra request.
    /// </para>
    /// </summary>
    Task<ColdAdvisory?> GetColdAtAsync(
        string stationCode, string stationName, CancellationToken ct = default) =>
        GetColdAsync(ct);
}

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

    public string LevelText => LevelLabel(Level);
}

/// <summary>
/// Supplies the current heat advisory for the household's area from public open data.
///
/// <para>
/// Implementations must fail soft: when the source is unreachable, out of season, or
/// returns something unexpected they return <c>null</c> rather than throwing, because a
/// government website being down must never stop the watch service from running.
/// </para>
/// </summary>
public interface IHeatAdvisoryProvider
{
    Task<HeatAdvisory?> GetCurrentAsync(CancellationToken ct = default);
}

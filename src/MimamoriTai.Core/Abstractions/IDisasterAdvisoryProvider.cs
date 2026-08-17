namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// The kinds of 気象庁 emergency information this app acts on.
///
/// <para>
/// Deliberately a short list. Every phone in Japan already receives 緊急速報メール, and a
/// service that re-broadcasts every 注意報 becomes something a family mutes -- which
/// would take the heatstroke alert down with it. These four are the ones where the
/// question a family is actually left with is "is she alright?", which is the only
/// question this app can answer and a broadcast cannot.
/// </para>
/// </summary>
public enum DisasterKind
{
    /// <summary>土砂災害警戒情報. 都道府県と気象庁の共同発表.</summary>
    Landslide = 1,

    /// <summary>顕著な大雨に関する気象情報 (いわゆる線状降水帯).</summary>
    HeavyRainBand = 2,

    /// <summary>特別警報 (大雨・暴風など).</summary>
    SpecialWarning = 3,

    /// <summary>震度5弱以上の地震.</summary>
    Earthquake = 4
}

/// <summary>
/// One active piece of emergency information for the household's area.
/// </summary>
/// <param name="Kind">Which of the handled categories this is.</param>
/// <param name="Headline">The government's own wording, e.g. "土砂災害警戒情報".</param>
/// <param name="AreaName">Area the information covers, as published.</param>
/// <param name="Detail">Extra published fact worth showing, e.g. "最大震度5強", or null.</param>
/// <param name="IssuedAtUtc">When 気象庁 issued it.</param>
/// <param name="Attribution">Credit line that must be shown wherever this is displayed.</param>
public sealed record DisasterAdvisory(
    DisasterKind Kind,
    string Headline,
    string AreaName,
    string? Detail,
    DateTimeOffset IssuedAtUtc,
    string Attribution)
{
    /// <summary>
    /// Stable key for "we already told the family about this one", so a warning that
    /// stays active for six hours does not become six pushes.
    /// </summary>
    public string DedupeKey => $"{Kind}|{AreaName}|{IssuedAtUtc:yyyy-MM-ddTHH:mm}";
}

/// <summary>
/// Reads currently active emergency information for the household's area.
///
/// <para>
/// Every failure path returns an empty list rather than throwing. 気象庁 going down, or
/// a household's internet being out, must never stop the rest of the watch service.
/// </para>
/// </summary>
public interface IDisasterAdvisoryProvider
{
    /// <summary>True when the provider has enough configuration to be worth calling.</summary>
    bool IsConfigured { get; }

    /// <summary>Active advisories for the configured area, newest first. Never null.</summary>
    Task<IReadOnlyList<DisasterAdvisory>> GetActiveAsync(CancellationToken ct = default);
}

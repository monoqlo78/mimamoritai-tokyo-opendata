using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record RiskResult(RiskLevel Level, int Score, string Reason);

/// <summary>
/// A device that is currently on, and for how long. Passed into the risk rules so
/// "電気つけっぱなし" can be judged without the scoring logic touching the database.
/// </summary>
public sealed record LeftOnDevice(string Name, DeviceType DeviceType, TimeSpan On);

/// <summary>
/// A device whose draw has not moved for long enough that the family asked to hear
/// about it. Separate from <see cref="LeftOnDevice"/>: that one is about an appliance
/// running too long, this one is about nothing happening at all.
/// </summary>
public sealed record FlatPowerDevice(string Name, TimeSpan Flat, int ThresholdHours);

/// <summary>
/// A cooling appliance and whether it is actually doing anything right now.
///
/// <para>
/// <paramref name="Watts"/> is what makes this rule honest. A Plug Mini reports its
/// relay as "on" for as long as it is switched at the wall, so an air conditioner that
/// was turned off by its remote still reads as on. Only the draw tells us whether the
/// room is being cooled, which is the same distinction the dashboard shows as 待機中.
/// </para>
/// </summary>
public sealed record CoolingDevice(
    string Name, bool IsOn, double? Watts, string Alias = "", string SafetyClass = "")
{
    /// <summary>
    /// Below this the appliance is standing by, not cooling. Set just above zero rather
    /// than at zero because a plug reports a fraction of a watt for its own electronics.
    /// </summary>
    public const double ActiveWatts = 1.0;

    /// <summary>
    /// True only when we can see the room is actually being cooled. When the plug gives
    /// us no wattage at all we fall back to the relay state: half a signal is better
    /// than accusing a working air conditioner of being off.
    /// </summary>
    public bool IsCooling => IsOn && (Watts is null || Watts.Value > ActiveWatts);
}

/// <summary>
/// A heating appliance and whether it is actually warming the room right now. The same
/// standby distinction as <see cref="CoolingDevice"/>, for the other half of the year.
/// </summary>
public sealed record HeatingDevice(
    string Name, bool IsOn, double? Watts, string Alias = "", string SafetyClass = "")
{
    public bool IsHeating => IsOn && (Watts is null || Watts.Value > CoolingDevice.ActiveWatts);
}

/// <summary>
/// A room that could be made comfortable right now, and the one appliance that would do
/// it.
///
/// <para>
/// The heat and cold rules already work out that it is hot outside and nothing indoors
/// is cooling. Until now that only ever became a warning -- accurate, and useless to a
/// daughter at work who can see the problem and has to ring her mother to fix it. This
/// turns the same finding into the action it implies, so noticing and acting are one
/// tap rather than a phone call.
/// </para>
///
/// <para>
/// Deliberately not a piece of advice from the model: it is produced by the same
/// deterministic rule that produced the warning, and it never acts on its own. Somebody
/// has to press it, which is what keeps a machine from deciding to switch on an
/// appliance in a house it cannot see.
/// </para>
/// </summary>
/// <param name="CanTurnOnRemotely">
/// False for appliances the household marked as never-remotely-on. Those still get the
/// prompt, because knowing to ring and ask is worth something, but not the button.
/// </param>
public sealed record ComfortSuggestion(
    string Title,
    string Reason,
    string ActionLabel,
    string DeviceName,
    string Alias,
    bool CanTurnOnRemotely,
    bool NeedsHazardCheck)
{
    /// <summary>
    /// The suggestion the current conditions imply, or null when there is nothing to
    /// suggest -- which is the normal case, and stays silent rather than filling the
    /// screen with advice nobody asked for.
    /// </summary>
    public static ComfortSuggestion? For(
        HeatAdvisory? heat,
        IReadOnlyList<CoolingDevice>? cooling,
        ColdAdvisory? cold,
        IReadOnlyList<HeatingDevice>? heating)
    {
        // Heat first: it is the sharper of the two. Heatstroke indoors takes hours, not
        // days, and more than half of Tokyo's cases happen at home.
        if (heat is not null
            && heat.Level >= RiskAssessmentService.CoolingSuggestedFrom
            && cooling is { Count: > 0 }
            && !cooling.Any(d => d.IsCooling)
            && Pick(cooling.Select(d => (d.Name, d.Alias, d.SafetyClass, d.IsOn))) is { } ac)
        {
            return new ComfortSuggestion(
                "冷房をつけますか？",
                $"暑さ指数{heat.Wbgt:0.#}（{heat.LevelText}）です。"
                    + (ac.IsOn
                        ? $"{ac.Name}のスイッチは入っていますが、電気が使われていません"
                        : $"{ac.Name}が動いていません"),
                "冷房をつける",
                ac.Name,
                ac.Alias,
                ac.SafetyClass != "Restricted",
                ac.SafetyClass == "Guarded");
        }

        if (cold is not null
            && cold.Level >= RiskAssessmentService.HeatingSuggestedFrom
            && heating is { Count: > 0 }
            && !heating.Any(d => d.IsHeating)
            && Pick(heating.Select(d => (d.Name, d.Alias, d.SafetyClass, d.IsOn))) is { } warmer)
        {
            return new ComfortSuggestion(
                "暖房をつけますか？",
                $"気温{cold.TemperatureC:0.#}℃（{cold.LevelText}）です。"
                    + (warmer.IsOn
                        ? $"{warmer.Name}のスイッチは入っていますが、電気が使われていません"
                        : $"{warmer.Name}が動いていません"),
                "暖房をつける",
                warmer.Name,
                warmer.Alias,
                warmer.SafetyClass != "Restricted",
                warmer.SafetyClass == "Guarded");
        }

        return null;
    }

    /// <summary>
    /// One appliance out of however many are registered. Anything the family barred from
    /// remote switching comes last, so a household with both an air conditioner and a
    /// restricted fan is offered the one that can actually be turned on.
    /// </summary>
    private static (string Name, string Alias, string SafetyClass, bool IsOn)? Pick(
        IEnumerable<(string Name, string Alias, string SafetyClass, bool IsOn)> devices)
    {
        var usable = devices.Where(d => !string.IsNullOrEmpty(d.Alias)).ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        return usable
            .OrderBy(d => d.SafetyClass == "Restricted" ? 1 : 0)
            .ThenByDescending(d => d.IsOn)
            .First();
    }
}

/// <summary>
/// A stretch of recent hours in which the house drew far less electricity than those
/// same clock hours usually draw.
///
/// <para>
/// On an ordinary day this is not worth telling anyone about -- people go shopping. It
/// exists because of one case: 気象庁 has emergency information out over her area and the
/// house has been dark for hours, which is the difference between "she is riding it out
/// indoors" and "she may be outside in it".
/// </para>
/// </summary>
/// <param name="Hours">How many whole hours the comparison covers.</param>
/// <param name="FromHour">Local clock hour the stretch starts at, for wording a message.</param>
/// <param name="RecentWh">Watt-hours actually drawn over those hours.</param>
/// <param name="UsualWh">Watt-hours those same clock hours usually draw.</param>
public sealed record QuietSpell(int Hours, int FromHour, double RecentWh, double UsualWh)
{
    /// <summary>Recent draw as a share of the usual one.</summary>
    public double Ratio => UsualWh <= 0 ? 1 : RecentWh / UsualWh;

    /// <summary>The same share as a whole percentage, so messages need not repeat the maths.</summary>
    public int PercentOfUsual => (int)Math.Round(Ratio * 100);
}

/// <summary>
/// Deterministic, rule based risk scoring. Intentionally NOT delegated to the LLM:
/// the model may phrase the result, but never decides whether something is abnormal.
/// </summary>
public sealed class RiskAssessmentService(
    IAppDbContext db,
    TimeProvider clock,
    IWeatherAdvisoryProvider? heatAdvisory = null)
{
    /// <summary>
    /// The hour by which every household is expected to have stirred. This is the
    /// backstop, not the goal: a resident who is always up at 6 should not have to wait
    /// until 10 for anyone to notice, which is what <see cref="LateStartGrace"/> is for.
    /// </summary>
    public const int NoActivityByHour = 10;

    /// <summary>
    /// How long past this household's own usual start we wait before calling the morning
    /// late. Short enough to beat the 10 o'clock backstop by hours for an early riser,
    /// long enough that a lie-in is not treated as an emergency.
    /// </summary>
    public static readonly TimeSpan LateStartGrace = TimeSpan.FromHours(2);

    /// <summary>
    /// The earliest the personalised rule is allowed to fire. Without this a household
    /// whose habit really is to be up at 3am would be reported every single morning.
    /// </summary>
    public const int EarliestLateStartHour = 6;

    /// <summary>Hours when a still house means "asleep", not "something is wrong".</summary>
    public const int QuietStartHour = 22;
    /// <summary>Hour the house is expected to be awake again.</summary>
    public const int QuietEndHour = 6;

    /// <summary>
    /// The figure offered when a family does turn stillness watching on for a device.
    /// Short enough to catch a missed lunch, long enough that a quiet afternoon with a
    /// book is not an incident.
    /// </summary>
    public const int DefaultFlatPowerAlertHours = 3;

    public static bool IsQuietHour(TimeOnly local) =>
        local.Hour >= QuietStartHour || local.Hour < QuietEndHour;

    /// <summary>How many whole hours back <see cref="DetectQuiet"/> looks.</summary>
    public const int AwayLookbackHours = 3;

    /// <summary>
    /// Share of the usual draw below which the hours read as "nobody is in" rather than
    /// "it is a quiet afternoon". Deliberately low: this figure only ever becomes a
    /// message when emergency information is already out, and the cost of being wrong is
    /// telephoning a woman who is perfectly fine and sitting down.
    /// </summary>
    public const double AwayRatio = 0.25;

    /// <summary>
    /// Watt-hours the window must usually carry before its absence means anything.
    /// Without a floor, an hour whose norm is 2Wh would report the house empty over a
    /// rounding error.
    /// </summary>
    public const double AwayMinUsualWh = 30;

    /// <summary>Days of history needed before "usual" is a number worth comparing against.</summary>
    public const int AwayMinUsualDays = 3;

    /// <summary>
    /// Reports the recent hours as unusually dark, or null when they are ordinary, when
    /// there is not enough history to say, or when the house is only meant to be still.
    ///
    /// <para>
    /// The newest bucket of <see cref="HourlyEnergyProfile"/> is the hour currently
    /// running, so it is always short of a full hour's electricity and is left out --
    /// including it would report every household as quiet, every hour, forever.
    /// </para>
    /// </summary>
    public static QuietSpell? DetectQuiet(
        HourlyEnergyProfile profile, int lookbackHours = AwayLookbackHours)
    {
        if (profile.UsualDayCount < AwayMinUsualDays
            || profile.Today.Count < 24
            || profile.Usual.Count < 24
            || lookbackHours < 1)
        {
            return null;
        }

        var last = profile.Today.Count - 2; // the in-progress hour is index Count - 1
        var first = last - lookbackHours + 1;
        if (first < 0)
        {
            return null;
        }

        // A house is supposed to be dark while everyone is asleep. Comparing those hours
        // would fire at the same time every night, and a nightly alert is one the family
        // learns to swipe away -- taking the morning it mattered with it.
        for (var i = first; i <= last; i++)
        {
            if (IsQuietHour(new TimeOnly((profile.StartHour + i) % 24, 0)))
            {
                return null;
            }
        }

        var recent = 0.0;
        var usual = 0.0;
        for (var i = first; i <= last; i++)
        {
            recent += profile.Today[i];
            usual += profile.Usual[i];
        }

        if (usual < AwayMinUsualWh || recent > usual * AwayRatio)
        {
            return null;
        }

        return new QuietSpell(
            lookbackHours,
            (profile.StartHour + first) % 24,
            Math.Round(recent, 2),
            Math.Round(usual, 2));
    }

    /// <summary>
    /// The time this household usually gets going, taken as the median of the days we
    /// actually hold. Median rather than mean because one 2am night out would otherwise
    /// drag the whole habit an hour earlier and blunt the rule for weeks.
    /// </summary>
    public static TimeOnly? UsualFirstActivity(IReadOnlyList<DailyActivity> baseline, DateOnly today)
    {
        var starts = baseline
            .Where(d => d.Date != today && d.FirstActivityTime is not null)
            .Select(d => d.FirstActivityTime!.Value.ToTimeSpan().TotalMinutes)
            .OrderBy(m => m)
            .ToList();

        // Fewer than three days is a coincidence, not a habit.
        return starts.Count < 3
            ? null
            : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(starts[starts.Count / 2]));
    }

    /// <summary>
    /// The time today counts as "the morning never happened": this household's usual
    /// start plus a grace period, or the fixed backstop when we have no habit to go on
    /// or the habit would have us calling before dawn.
    /// </summary>
    public static TimeOnly LateStartThreshold(IReadOnlyList<DailyActivity> baseline, DateOnly today)
    {
        var backstop = new TimeOnly(NoActivityByHour, 0);

        if (UsualFirstActivity(baseline, today) is not { } usual)
        {
            return backstop;
        }

        var personal = usual.Add(LateStartGrace);

        return personal.Hour >= EarliestLateStartHour && personal < backstop
            ? personal
            : backstop;
    }

    /// <summary>Anything that produces heat is treated as urgent when left running.</summary>
    public static readonly TimeSpan HeatLeftOnLimit = TimeSpan.FromHours(2);

    /// <summary>Lights and similar appliances left on through the small hours.</summary>
    public static readonly TimeSpan NightLeftOnLimit = TimeSpan.FromHours(4);

    /// <summary>Lights and similar appliances left on during the day.</summary>
    public static readonly TimeSpan DayLeftOnLimit = TimeSpan.FromHours(12);

    public static bool IsHeatProducing(DeviceType type) =>
        type is DeviceType.Heater or DeviceType.Kettle or DeviceType.CookingDevice or DeviceType.Microwave;

    /// <summary>Appliances whose job is to make a hot room survivable.</summary>
    public static bool IsCooling(DeviceType type) =>
        type is DeviceType.AirConditioner or DeviceType.Fan;

    /// <summary>
    /// Appliances whose job is to make a cold room survivable. An air conditioner counts
    /// on both lists because in a Japanese home it is usually the heating too.
    /// </summary>
    public static bool IsHeating(DeviceType type) =>
        type is DeviceType.AirConditioner or DeviceType.Heater;

    /// <summary>
    /// The band at which a room with no cooling running stops being a comfort issue and
    /// becomes a health one. 28 is 厳重警戒 in the Ministry of the Environment's own
    /// scale, the point from which it advises avoiding heat and watching indoor
    /// temperature -- and indoors is where roughly half of Tokyo's heatstroke
    /// ambulance calls start (東京都福祉局, 令和3年: 住居施設等 56.6%).
    /// </summary>
    public const HeatAlertLevel CoolingExpectedFrom = HeatAlertLevel.SevereWarning;

    /// <summary>
    /// The band from which a room with no heating running stops being a comfort issue.
    /// Below 10°C outside, an unheated Japanese home struggles to hold the 18°C that
    /// WHO's housing guideline asks for, and it is the cold room -- not the cold street
    /// -- that puts an older person at risk of ヒートショック and 低体温症.
    /// </summary>
    public const ColdAlertLevel HeatingExpectedFrom = ColdAlertLevel.Cold;

    /// <summary>
    /// The band from which the dashboard offers to switch the cooling on. Deliberately
    /// one step below <see cref="CoolingExpectedFrom"/>, because the two are not the same
    /// act. Above 28 we interrupt a family at work with an alert, and that has to be
    /// reserved for a health risk. Offering a button on a screen somebody chose to open
    /// costs them nothing, and 警戒 (25) is already the band at which the Ministry of the
    /// Environment asks older people to watch the indoor temperature.
    /// </summary>
    public const HeatAlertLevel CoolingSuggestedFrom = HeatAlertLevel.Warning;

    /// <summary>
    /// The same one-step-earlier offer for the cold half of the year. Below 15°C outside,
    /// an unheated room is heading for the morning that causes ヒートショック; suggesting
    /// the heater then is cheaper than waiting for it to become an alert.
    /// </summary>
    public const ColdAlertLevel HeatingSuggestedFrom = ColdAlertLevel.Chilly;

    /// <summary>
    /// Scores the heatstroke picture: how hot it is outside (open data) against whether
    /// anything in the house is cooling (our own readings).
    ///
    /// <para>
    /// Deliberately silent unless the household actually registered a cooling appliance.
    /// Telling a family "no air conditioner is running" when they never told us about
    /// one is noise, and noise is what stops alerts being read.
    /// </para>
    /// </summary>
    internal static (int Score, string? Reason) EvaluateHeat(
        HeatAdvisory? heat,
        IReadOnlyList<CoolingDevice>? cooling)
    {
        if (heat is null || heat.Level < CoolingExpectedFrom)
        {
            return (0, null);
        }

        var known = cooling ?? [];
        if (known.Count == 0)
        {
            // Worth saying, not worth scoring: we have no way to check the room.
            return (0, $"暑さ指数{heat.Wbgt:0.#}（{heat.LevelText}）です。冷房機器が未登録のため室内の状況は確認できません");
        }

        if (known.Any(d => d.IsCooling))
        {
            return (0, null);
        }

        var names = string.Join("・", known.Select(d => d.Name).Distinct().Take(2));

        // At 危険 this stands alone, like a heater left running: the guidance is that a
        // resident this age can be taken ill sitting still, so waiting for a second
        // signal is not acceptable.
        var score = heat.Level >= HeatAlertLevel.Danger ? 60 : 45;

        return (score, $"暑さ指数{heat.Wbgt:0.#}（{heat.LevelText}）ですが、{names}が動いていません（熱中症の恐れ）");
    }

    /// <summary>
    /// The mirror of <see cref="EvaluateHeat"/> for the other half of the year: how cold
    /// it is outside (open data) against whether anything in the house is heating.
    ///
    /// <para>
    /// This is the rule the service exists for in winter. A resident who is quietly
    /// going without heating -- to save money, or because turning it on stopped feeling
    /// worth the bother -- looks completely normal from the outside and completely
    /// normal in every other signal we hold. The one place it shows is the draw, which
    /// is exactly what we are already watching, and no camera is needed to see it.
    /// </para>
    ///
    /// <para>
    /// Silent unless the household registered a heating appliance, for the same reason
    /// as the heat rule: telling a family "no heater is running" when they never told us
    /// about one is noise, and noise is what stops alerts being read.
    /// </para>
    /// </summary>
    internal static (int Score, string? Reason) EvaluateCold(
        ColdAdvisory? cold,
        IReadOnlyList<HeatingDevice>? heating)
    {
        if (cold is null || cold.Level < HeatingExpectedFrom)
        {
            return (0, null);
        }

        var known = heating ?? [];
        if (known.Count == 0)
        {
            return (0, $"気温{cold.TemperatureC:0.#}℃（{cold.LevelText}）です。暖房機器が未登録のため室内の状況は確認できません");
        }

        if (known.Any(d => d.IsHeating))
        {
            return (0, null);
        }

        var names = string.Join("・", known.Select(d => d.Name).Distinct().Take(2));

        // 厳しい冷え込み stands alone for the same reason 危険 does on the heat side: the
        // harm here is a fall in the bath or a night the resident does not wake from,
        // and neither gives us a second signal to wait for.
        var score = cold.Level >= ColdAlertLevel.SevereCold ? 60 : 45;

        return (score, $"気温{cold.TemperatureC:0.#}℃（{cold.LevelText}）ですが、{names}が動いていません（ヒートショック・低体温症の恐れ）");
    }

    /// <summary>
    /// How long a warming appliance may run on a cold day before it counts as left on.
    ///
    /// <para>
    /// The two-hour heat limit exists because a heater left running is a fire risk. But
    /// applied literally it would report a household every winter evening for the crime
    /// of being warm, and an alert that fires nightly is one the family stops opening --
    /// which costs them the night it matters. When the open data says it is genuinely
    /// cold, a heater that is running is doing its job, so the limit stretches to cover
    /// an evening while still catching one left on unattended overnight.
    /// </para>
    /// </summary>
    public static readonly TimeSpan WarmingLeftOnLimit = TimeSpan.FromHours(8);

    /// <summary>How long this device may stay on before it counts as left on.</summary>
    public static TimeSpan LeftOnLimit(
        DeviceType type, TimeOnly nowLocal, ColdAlertLevel cold = ColdAlertLevel.Unknown)
    {
        if (IsHeating(type) && cold >= HeatingExpectedFrom)
        {
            return WarmingLeftOnLimit;
        }

        if (IsHeatProducing(type))
        {
            return HeatLeftOnLimit;
        }

        var isNight = nowLocal.Hour is >= ActivityService.NightStartHour and < ActivityService.NightEndHour;
        return isNight ? NightLeftOnLimit : DayLeftOnLimit;
    }

    public static RiskResult Evaluate(
        DailyActivity today,
        IReadOnlyList<DailyActivity> baseline,
        TimeOnly nowLocal,
        IReadOnlyList<LeftOnDevice>? leftOn = null,
        IReadOnlyList<FlatPowerDevice>? flatPower = null,
        HeatAdvisory? heat = null,
        IReadOnlyList<CoolingDevice>? cooling = null,
        ColdAdvisory? cold = null,
        IReadOnlyList<HeatingDevice>? heating = null)
    {
        var score = 0;
        var reasons = new List<string>();
        var lateStart = LateStartThreshold(baseline, today.Date);

        if (today.DeviceUsageCount == 0)
        {
            if (nowLocal >= lateStart)
            {
                score += 60;
                reasons.Add($"{lateStart:HH\\:mm}を過ぎても家電の利用がありません");
            }
            else
            {
                reasons.Add("まだ本日の活動記録がありません");
            }
        }
        else if (today.FirstActivityTime is { } first && first >= lateStart)
        {
            score += 35;
            reasons.Add($"活動開始が{first:HH\\:mm}と遅めです");
        }

        if (today.NightActivityCount >= 2)
        {
            score += 30;
            reasons.Add($"深夜帯に{today.NightActivityCount}回の家電利用があります");
        }

        // Compare against the recent norm, ignoring days with no data at all.
        var reference = baseline.Where(d => d.Date != today.Date && d.DeviceUsageCount > 0).ToList();
        if (reference.Count >= 3 && today.DeviceUsageCount > 0)
        {
            var average = reference.Average(d => d.DeviceUsageCount);
            if (average > 0 && today.DeviceUsageCount <= average * 0.4)
            {
                score += 25;
                reasons.Add($"普段（平均{average:0.#}回）より活動量が少なめです");
            }
        }

        // Left-on appliances. Only the single worst offender adds to the score, so a
        // house with several lights on doesn't inflate the level past a real emergency.
        var worst = (leftOn ?? [])
            .Where(d => d.On >= LeftOnLimit(d.DeviceType, nowLocal, cold?.Level ?? ColdAlertLevel.Unknown))
            .OrderByDescending(d => IsHeatProducing(d.DeviceType))
            .ThenByDescending(d => d.On)
            .FirstOrDefault();

        if (worst is not null)
        {
            var hours = (int)worst.On.TotalHours;
            if (IsHeatProducing(worst.DeviceType))
            {
                // On its own this must reach High: a heater left running is the one
                // case where waiting for a second signal is not acceptable.
                score += 60;
                reasons.Add($"{worst.Name}が{hours}時間つけっぱなしです（火災の恐れ）");
            }
            else
            {
                score += 20;
                reasons.Add($"{worst.Name}が{hours}時間つけっぱなしです");
            }
        }

        // Appliances the family asked to be told about when their draw stops moving.
        // Only the longest-still one scores, for the same reason as the left-on rule:
        // a quiet house should read as one concern, not as many.
        var stillest = (flatPower ?? [])
            .OrderByDescending(d => d.Flat)
            .FirstOrDefault();

        if (stillest is not null)
        {
            // A house is meant to be still while everyone is asleep. Scoring that would
            // fire at the same hour every single night, and an alert that cries wolf
            // nightly is worse than no alert at all -- the family stops reading them.
            // Both ends matter: at 6am the last three hours were always going to be
            // flat, so the window has to have covered waking hours to mean anything.
            var windowStart = nowLocal.Add(-stillest.Flat);

            if (!IsQuietHour(nowLocal) && !IsQuietHour(windowStart))
            {
                score += 45;
                reasons.Add(
                    $"{stillest.Name}の使用量が{stillest.ThresholdHours}時間以上変わっていません");
            }
        }

        // Heatstroke: outdoor open data crossed with whether the room is being cooled.
        var (heatScore, heatReason) = EvaluateHeat(heat, cooling);
        score += heatScore;
        if (heatReason is not null)
        {
            reasons.Add(heatReason);
        }

        // The same question asked of the cold half of the year.
        var (coldScore, coldReason) = EvaluateCold(cold, heating);
        score += coldScore;
        if (coldReason is not null)
        {
            reasons.Add(coldReason);
        }

        var level = score switch
        {
            >= 60 => RiskLevel.High,
            >= 25 => RiskLevel.Medium,
            _ => RiskLevel.Low
        };

        var reason = reasons.Count > 0
            ? string.Join("／", reasons)
            : "普段どおりの生活リズムです";

        return new RiskResult(level, Math.Min(score, 100), reason);
    }

    public async Task<RiskResult> AssessTodayAsync(Guid householdId, CancellationToken ct = default)
    {
        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var nowLocal = HouseholdTime.LocalTime(clock.GetUtcNow());

        var leftOn = await LoadLeftOnAsync(householdId, ct);
        var cooling = await LoadCoolingAsync(householdId, ct);
        var heating = await LoadHeatingAsync(householdId, ct);
        var heat = await GetHeatAsync(ct);
        var cold = await GetColdAsync(ct);
        var result = Evaluate(today, recent, nowLocal, leftOn, null, heat, cooling, cold, heating);

        var resident = await db.People
            .Where(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident)
            .FirstOrDefaultAsync(ct);

        if (resident is not null)
        {
            db.RiskAssessments.Add(new RiskAssessment
            {
                HouseholdId = householdId,
                PersonId = resident.Id,
                RiskLevel = result.Level,
                Score = result.Score,
                Reason = result.Reason,
                CreatedAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Fetches the current heat advisory, treating any failure as "we do not know".
    /// The provider already fails soft; this is the belt to its braces, because a public
    /// data source must never be able to stop a watch assessment from completing.
    /// </summary>
    /// <summary>
    /// The outdoor temperature range for each of the last <paramref name="days"/> days,
    /// taken from the 気象庁 observations this app has already recorded.
    ///
    /// <para>
    /// This exists so the dashboard can lay the weather over the household's electricity
    /// use. A bar that is taller than its neighbours means little on its own; a bar that
    /// is taller on the coldest morning of the fortnight says the heating went on, and a
    /// flat bar on that same morning says it did not.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DailyOutdoorTemperature>> GetDailyTemperaturesAsync(
        int days, string? pointCode = null, CancellationToken ct = default)
    {
        var today = HouseholdTime.LocalDate(DateTimeOffset.UtcNow);
        var fromUtc = HouseholdTime.StartOfLocalDayUtc(today.AddDays(-(days - 1)));

        var query = db.HeatReadings.Where(r => r.ObservedAtUtc >= fromUtc && r.TemperatureC != null);
        if (pointCode is { Length: > 0 })
        {
            query = query.Where(r => r.PointCode == pointCode);
        }

        var rows = await query
            .Select(r => new { r.ObservedAtUtc, r.TemperatureC })
            .ToListAsync(ct);

        // Bucketing happens here rather than in SQL because the day boundary is the
        // household's local one, and translating that into a provider-agnostic
        // expression buys nothing at this volume (a fortnight of ten-minute readings).
        return rows
            .GroupBy(r => HouseholdTime.LocalDate(r.ObservedAtUtc))
            .Where(g => g.Key <= today)
            .OrderBy(g => g.Key)
            .Select(g => new DailyOutdoorTemperature(
                g.Key,
                g.Min(r => r.TemperatureC!.Value),
                g.Max(r => r.TemperatureC!.Value)))
            .ToList();
    }

    public async Task<HeatAdvisory?> GetHeatAsync(CancellationToken ct = default)
    {
        if (heatAdvisory is null)
        {
            return null;
        }

        try
        {
            return await heatAdvisory.GetHeatAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the current cold advisory, treating any failure as "we do not know".
    /// Same belt-and-braces as <see cref="GetHeatAsync"/>.
    /// </summary>
    public async Task<ColdAdvisory?> GetColdAsync(CancellationToken ct = default)
    {
        if (heatAdvisory is null)
        {
            return null;
        }

        try
        {
            return await heatAdvisory.GetColdAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The cold advisory for a household's own AMeDAS station, falling back to the
    /// default point when the household has not set one.
    /// </summary>
    public async Task<ColdAdvisory?> GetColdAsync(Household? household, CancellationToken ct = default)
    {
        if (heatAdvisory is null)
        {
            return null;
        }

        if (household?.AmedasStationCode is not { Length: > 0 } code)
        {
            return await GetColdAsync(ct);
        }

        try
        {
            return await heatAdvisory.GetColdAtAsync(code, household.AmedasStationName ?? string.Empty, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tomorrow morning's forecast low, for the evening notice. Same fail-soft contract:
    /// a forecast we could not fetch is simply a notice we do not send.
    /// </summary>
    public async Task<ColdForecast?> GetTomorrowColdAsync(CancellationToken ct = default)
    {
        if (heatAdvisory is null)
        {
            return null;
        }

        try
        {
            return await heatAdvisory.GetTomorrowColdAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the current on/off state of each enabled device from its most recent
    /// PowerState event. One query per device: a household has a handful of devices,
    /// and this keeps the intent obvious and provider-agnostic.
    /// </summary>
    public async Task<IReadOnlyList<LeftOnDevice>> LoadLeftOnAsync(Guid householdId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .ToListAsync(ct);

        var leftOn = new List<LeftOnDevice>();

        foreach (var device in devices)
        {
            var last = await db.DeviceEvents
                .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);

            if (last is null || !last.State.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var on = now - last.OccurredAtUtc;
            if (on > TimeSpan.Zero)
            {
                leftOn.Add(new LeftOnDevice(device.DisplayName, device.DeviceType, on));
            }
        }

        return leftOn;
    }

    /// <summary>
    /// Reads which cooling appliances the household has and whether each is actually
    /// drawing power, so the heatstroke rule can tell a running air conditioner from one
    /// that is merely switched on at the wall.
    /// </summary>
    public async Task<IReadOnlyList<CoolingDevice>> LoadCoolingAsync(
        Guid householdId, CancellationToken ct = default)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .ToListAsync(ct);

        var cooling = new List<CoolingDevice>();

        // Only a fresh reading can speak for right now; an old one would let a plug that
        // went offline hours ago vouch for a room nobody is measuring any more.
        var since = clock.GetUtcNow() - ReadingFreshness;

        foreach (var device in devices.Where(d => IsCooling(d.DeviceType)))
        {
            var last = await db.DeviceEvents
                .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);

            var isOn = last is not null && last.State.Equals("on", StringComparison.OrdinalIgnoreCase);

            var watts = await db.PlugMiniReadings
                .Where(r => r.DeviceId == device.Id && r.ApproxWatts != null && r.OccurredAtUtc >= since)
                .OrderByDescending(r => r.OccurredAtUtc)
                .Select(r => r.ApproxWatts)
                .FirstOrDefaultAsync(ct);

            cooling.Add(new CoolingDevice(
                device.DisplayName, isOn, watts, device.Alias, device.SafetyClass.ToString()));
        }

        return cooling;
    }

    /// <summary>
    /// The heating counterpart of <see cref="LoadCoolingAsync"/>, so the cold rule can
    /// tell a heater that is warming the room from one that is merely switched on at
    /// the wall.
    /// </summary>
    public async Task<IReadOnlyList<HeatingDevice>> LoadHeatingAsync(
        Guid householdId, CancellationToken ct = default)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .ToListAsync(ct);

        var heating = new List<HeatingDevice>();
        var since = clock.GetUtcNow() - ReadingFreshness;

        foreach (var device in devices.Where(d => IsHeating(d.DeviceType)))
        {
            var last = await db.DeviceEvents
                .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);

            var isOn = last is not null && last.State.Equals("on", StringComparison.OrdinalIgnoreCase);

            var watts = await db.PlugMiniReadings
                .Where(r => r.DeviceId == device.Id && r.ApproxWatts != null && r.OccurredAtUtc >= since)
                .OrderByDescending(r => r.OccurredAtUtc)
                .Select(r => r.ApproxWatts)
                .FirstOrDefaultAsync(ct);

            heating.Add(new HeatingDevice(
                device.DisplayName, isOn, watts, device.Alias, device.SafetyClass.ToString()));
        }

        return heating;
    }

    /// <summary>
    /// How old a wattage reading may be and still describe the present. Readings arrive
    /// every few minutes, so half an hour absorbs a restart without going stale.
    /// </summary>
    public static readonly TimeSpan ReadingFreshness = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Reads how long each device's draw has been sitting still, for the devices whose
    /// family asked to be told about it.
    ///
    /// This is the signal that matters for a Plug Mini, and it took a while to see why.
    /// A plug is put in the wall once and left there, so the socket's on/off state
    /// stops changing on day one and every "is anyone up?" rule built on it quietly
    /// stops working. What still moves is the draw: the kettle, the heater, the lamp
    /// behind the plug all show up as the watts going up and coming back down. A whole
    /// day of perfectly flat watts therefore does not mean the appliance is idle -- it
    /// means nobody touched it, which is exactly what the family wants to hear about.
    ///
    /// Deliberately opt-in per device, and measurement says to keep it that way. Making
    /// it the default was tried and rejected: over a week of this household's real
    /// readings, an always-on lamp sat perfectly flat through 91% of waking three-hour
    /// windows, and the house as a whole produced only five significant changes all
    /// week. Watching every device by default would therefore have raised an alert most
    /// afternoons and taught the family to ignore the alerts that matter. Silence stays
    /// the default; the family picks the appliances whose stillness actually says
    /// something -- typically a kettle or a microwave, used daily and never left on.
    /// </summary>
    public async Task<IReadOnlyList<FlatPowerDevice>> LoadFlatPowerAsync(
        Guid householdId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled && d.FlatPowerAlertHours != null)
            .ToListAsync(ct);

        var flat = new List<FlatPowerDevice>();

        foreach (var device in devices)
        {
            var hours = device.FlatPowerAlertHours!.Value;
            if (hours <= 0)
            {
                continue;
            }

            var since = now.AddHours(-hours);

            var readings = await db.PlugMiniReadings
                .Where(r => r.DeviceId == device.Id && r.ApproxWatts != null && r.OccurredAtUtc >= since)
                .OrderBy(r => r.OccurredAtUtc)
                .Select(r => new { r.OccurredAtUtc, Watts = r.ApproxWatts!.Value })
                .ToListAsync(ct);

            // Two samples is the minimum that can show a change at all. Below that the
            // plug is offline or newly added, and calling that "unchanging" would
            // report a monitoring gap as if it were the resident sitting still.
            if (readings.Count < 2)
            {
                continue;
            }

            // The window has to actually be covered. If the oldest sample we hold is
            // recent, the appliance may well have been busy before it and we simply
            // were not watching.
            if (readings[0].OccurredAtUtc - since > CoverageTolerance)
            {
                continue;
            }

            var min = readings.Min(r => r.Watts);
            var max = readings.Max(r => r.Watts);

            // Same significance test the poller uses to decide what is worth recording,
            // so "no change happened" here means precisely "no change was recorded".
            if (SwitchBotPollingCycleService.IsSignificantPowerChange(min, max))
            {
                continue;
            }

            flat.Add(new FlatPowerDevice(
                device.DisplayName,
                now - readings[0].OccurredAtUtc,
                hours));
        }

        return flat;
    }

    /// <summary>
    /// How much of the requested window may be missing before we decline to judge it.
    /// Readings arrive every few minutes, so an hour of slack absorbs a restart or a
    /// brief outage without letting a genuinely short history masquerade as a long
    /// quiet spell.
    /// </summary>
    public static readonly TimeSpan CoverageTolerance = TimeSpan.FromHours(1);
}

/// <summary>The coldest and warmest outdoor readings recorded on one local day.</summary>
public sealed record DailyOutdoorTemperature(DateOnly Date, double LowC, double HighC);

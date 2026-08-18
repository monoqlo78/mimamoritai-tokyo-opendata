using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record DailyActivity(
    DateOnly Date,
    TimeOnly? FirstActivityTime,
    TimeOnly? LastActivityTime,
    int DeviceUsageCount,
    int ActiveMinutes,
    int NightActivityCount,
    double EnergyWh = 0,
    TimeOnly? FirstPowerMoveTime = null,
    TimeOnly? SettledTime = null);

/// <summary>One appliance's share of a day's electricity, hour by local hour.</summary>
/// <param name="Hours">24 entries, indexed by the local hour of the day.</param>
public sealed record DeviceHourlyEnergy(Guid DeviceId, IReadOnlyList<double> Hours, double TotalWh);

/// <summary>
/// One measured draw at one instant, whichever table it was read out of.
///
/// The energy maths wants a dense series of "the socket was pulling this many watts at
/// this moment". Two tables carry that, and neither is sufficient on its own:
/// <see cref="PlugMiniReading"/> is written every poll and so describes a steady load,
/// while <see cref="DeviceEvent"/> is only written when something changed and so pins
/// the exact instant a load started or stopped. Normalising both into one shape lets
/// the integration use whichever exists without caring where it came from.
/// </summary>
public readonly record struct PowerSample(Guid DeviceId, DateTimeOffset OccurredAtUtc, double Watts);

/// <summary>
/// A day's electricity laid out across the clock, next to what an ordinary day looks like.
///
/// A single number per day answers "was today busy" but not "was today <em>normal</em>".
/// Someone who slept until noon and someone who got up at six can draw exactly the same
/// watt-hours, and only the shape of the day tells them apart - which is the whole point
/// of watching a household that nobody is standing in.
/// </summary>
/// <param name="Today">Watt-hours drawn in each hour of the window, oldest first.</param>
/// <param name="Usual">The same profile averaged over the earlier days, for comparison.</param>
/// <param name="TodayByDevice">The window's profile split per appliance, busiest first.</param>
/// <param name="UsualDayCount">How many past days the average is built from.</param>
/// <param name="StartHour">Local clock hour of the first bucket, so the axis can be labelled.</param>
public sealed record HourlyEnergyProfile(
    IReadOnlyList<double> Today,
    IReadOnlyList<double> Usual,
    IReadOnlyList<DeviceHourlyEnergy> TodayByDevice,
    int UsualDayCount,
    int StartHour = 0);

/// <summary>Aggregates raw device events into the daily life-rhythm figures the UI and Q&amp;A use.</summary>
public sealed class ActivityService(IAppDbContext db)
{
    public const int NightStartHour = 0;
    public const int NightEndHour = 5;

    public async Task<DailyActivity> GetDailyAsync(Guid householdId, DateOnly localDate, CancellationToken ct = default)
    {
        var from = HouseholdTime.StartOfLocalDayUtc(localDate);
        var to = HouseholdTime.StartOfLocalDayUtc(localDate.AddDays(1));

        var events = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= from && e.OccurredAtUtc < to)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var readings = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && (r.DailyEnergyWh != null || r.ApproxWatts != null)
                && r.OccurredAtUtc >= from && r.OccurredAtUtc < to)
            .OrderBy(r => r.OccurredAtUtc)
            .ToListAsync(ct);

        return Summarize(localDate, events, readings);
    }

    public async Task<IReadOnlyList<DailyActivity>> GetRecentAsync(Guid householdId, int days, CancellationToken ct = default)
    {
        var today = HouseholdTime.LocalDate(DateTimeOffset.UtcNow);
        var firstDate = today.AddDays(-(days - 1));
        var from = HouseholdTime.StartOfLocalDayUtc(firstDate);

        var events = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= from)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var readings = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && (r.DailyEnergyWh != null || r.ApproxWatts != null)
                && r.OccurredAtUtc >= from)
            .OrderBy(r => r.OccurredAtUtc)
            .ToListAsync(ct);

        var byDate = events.GroupBy(e => HouseholdTime.LocalDate(e.OccurredAtUtc))
            .ToDictionary(g => g.Key, g => g.ToList());
        var readingsByDate = readings.GroupBy(r => HouseholdTime.LocalDate(r.OccurredAtUtc))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DailyActivity>();
        for (var i = 0; i < days; i++)
        {
            var date = firstDate.AddDays(i);
            result.Add(Summarize(
                date,
                byDate.TryGetValue(date, out var list) ? list : [],
                readingsByDate.TryGetValue(date, out var measured) ? measured : []));
        }

        return result;
    }

    public static DailyActivity Summarize(
        DateOnly date, IReadOnlyList<DeviceEvent> events, IReadOnlyList<PlugMiniReading>? readings = null)
    {
        // "usage" means the device was actually switched on by/for the resident.
        var usage = events.Where(IsUse).ToList();
        var samples = PowerSamples(events, readings);
        var energyWh = EnergyWh(samples);

        // When the draw moved is a separate question from when a socket was switched, and
        // it is the one this household can actually answer: the plugs stay energised, so
        // the electricity behind them is the only thing that reacts to somebody being up.
        var moves = PowerMovements(samples);
        var firstMove = moves.Count > 0 ? HouseholdTime.LocalTime(moves[0]) : (TimeOnly?)null;
        var settled = moves.Count > 0 ? HouseholdTime.LocalTime(moves[^1]) : (TimeOnly?)null;

        if (usage.Count == 0)
        {
            return new DailyActivity(date, null, null, 0, 0, 0, energyWh, firstMove, settled);
        }

        // The day starts at the first real use. Anything logged before that -- a plug
        // reporting its standby draw, or the socket being de-energised -- is the house
        // sitting still, and calling it the moment someone got up is the kind of
        // confident falsehood this app exists to avoid. The closing time may still come
        // from a later "off", because that is genuinely when the use ended.
        var first = usage.Min(e => e.OccurredAtUtc);
        var last = events.Where(e => e.OccurredAtUtc >= first).Max(e => e.OccurredAtUtc);
        var night = usage.Count(e =>
        {
            var hour = HouseholdTime.LocalTime(e.OccurredAtUtc).Hour;
            return hour >= NightStartHour && hour < NightEndHour;
        });

        var activeMinutes = (int)Math.Round((last - first).TotalMinutes);

        return new DailyActivity(
            date,
            HouseholdTime.LocalTime(first),
            HouseholdTime.LocalTime(last),
            usage.Count,
            Math.Max(activeMinutes, 0),
            night,
            energyWh,
            firstMove,
            settled);
    }

    /// <summary>
    /// Normalises the two tables that carry a wattage into one time-ordered series.
    ///
    /// This distinction is the whole reason the dashboard can be wrong while the data is
    /// right. <see cref="DeviceEvent"/> rows bearing watts are only written when the draw
    /// <em>changed</em> enough to be worth recording, so a television left on at a steady
    /// 99W produces one row and then nothing for hours. Integrating that alone reports an
    /// almost empty day for a house that was plainly occupied. The measurement table is
    /// written every poll whatever the load is doing, so it is the one that describes a
    /// steady appliance -- and the events still matter, because they pin the exact instant
    /// something switched rather than rounding it to the next poll.
    /// </summary>
    public static IReadOnlyList<PowerSample> PowerSamples(
        IReadOnlyList<DeviceEvent> events, IReadOnlyList<PlugMiniReading>? readings = null)
    {
        var samples = new List<PowerSample>();

        foreach (var e in events)
        {
            if (e.PowerWatts is { } watts)
            {
                samples.Add(new PowerSample(e.DeviceId, e.OccurredAtUtc, watts));
            }
        }

        foreach (var r in readings ?? [])
        {
            // Real power first, the volts-times-amps estimate only when the plug did not
            // report it -- the same precedence the poller uses to decide a socket is in
            // use. Apparent power on a reactive load reads far above the watts actually
            // consumed (314mA at 104V computes to 32.7W against 0.3W reported), so
            // integrating it here while PowerUsageService integrates the real figure
            // would put two contradictory electricity totals in front of the same family.
            if ((r.DailyEnergyWh ?? r.ApproxWatts) is { } watts)
            {
                samples.Add(new PowerSample(r.DeviceId, r.OccurredAtUtc, watts));
            }
        }

        // A poll writes the measurement and, when the draw moved, an event carrying the
        // same wattage at the same instant. Keeping both would put a zero-length gap in
        // the middle of the series, which contributes no energy but does show up as a
        // repeated "movement". One sample per device per second is enough.
        return [.. samples
            .GroupBy(s => (s.DeviceId, Second: s.OccurredAtUtc.ToUnixTimeSeconds()))
            .Select(g => g.First())
            .OrderBy(s => s.OccurredAtUtc)];
    }

    /// <summary>
    /// The instants at which the household's electricity actually moved.
    ///
    /// A plug that stays in the wall almost never switches, so on/off events say nothing
    /// about the day. What does move is the draw behind the socket: the kettle, the rice
    /// cooker, the heater. A movement is either the load crossing into or out of use, or a
    /// swing big enough to be a different appliance rather than a thermostat breathing -
    /// the same rule the poller uses to decide an event is worth recording at all.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> PowerMovements(IReadOnlyList<PowerSample> samples)
    {
        var moments = new List<DateTimeOffset>();

        foreach (var device in samples.GroupBy(s => s.DeviceId))
        {
            var readings = device.OrderBy(s => s.OccurredAtUtc).ToList();

            for (var i = 1; i < readings.Count; i++)
            {
                if (IsPowerMovement(readings[i - 1].Watts, readings[i].Watts))
                {
                    moments.Add(readings[i].OccurredAtUtc);
                }
            }
        }

        moments.Sort();
        return moments;
    }

    /// <inheritdoc cref="PowerMovements(IReadOnlyList{PowerSample})"/>
    public static IReadOnlyList<DateTimeOffset> PowerMovements(IReadOnlyList<DeviceEvent> events) =>
        PowerMovements(PowerSamples(events));

    private static bool IsPowerMovement(double previous, double current)
    {
        // A lamp coming on may only be a few watts, far below the "different appliance"
        // swing, but it is still the clearest sign a person is up. Crossing the in-use
        // line therefore counts on its own.
        var crossed = previous < SwitchBotPollingCycleService.InUseWattsThreshold
            != current < SwitchBotPollingCycleService.InUseWattsThreshold;

        return crossed || SwitchBotPollingCycleService.IsSignificantPowerChange(previous, current);
    }

    /// <summary>Longest gap between two readings that is still treated as continuous draw.</summary>
    internal static readonly TimeSpan MaxIntegrationGap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much electricity the home actually drew over the day, in watt-hours.
    ///
    /// This is the figure the family should be looking at. A Plug Mini that lives
    /// permanently in the wall almost never reports an on/off transition -- the kettle,
    /// the heater and the lamp are switched at the appliance -- so a chart of switch
    /// events is close to a chart of nothing. What does move is the draw behind the
    /// socket, and a day with no movement in it at all is the genuinely worrying case.
    ///
    /// Each reading is held to be the draw until the next reading from the same device,
    /// which is how a 5-minute poll of an instantaneous wattage has to be read. The gap
    /// is capped at <see cref="MaxIntegrationGap"/> so a device that drops off the
    /// network overnight does not get credited with eight hours of its last known load.
    /// </summary>
    public static double EnergyWh(IReadOnlyList<PowerSample> samples) =>
        Math.Round(HourlyEnergyWh(samples).Sum(), 2);

    /// <inheritdoc cref="EnergyWh(IReadOnlyList{PowerSample})"/>
    public static double EnergyWh(IReadOnlyList<DeviceEvent> events) =>
        EnergyWh(PowerSamples(events));

    /// <summary>
    /// The same electricity, but placed on the clock: watt-hours drawn in each local hour,
    /// indexed 0-23.
    ///
    /// Reading a day as one total hides the thing worth watching. Two households can draw
    /// identical watt-hours while one of them was up at six and the other never got out of
    /// bed until the afternoon, and it is the shape - not the size - that says which.
    /// </summary>
    public static double[] HourlyEnergyWh(IReadOnlyList<PowerSample> samples)
    {
        var hours = new double[24];
        Accumulate(samples, hours, null);
        return hours;
    }

    /// <inheritdoc cref="HourlyEnergyWh(IReadOnlyList{PowerSample})"/>
    public static double[] HourlyEnergyWh(IReadOnlyList<DeviceEvent> events) =>
        HourlyEnergyWh(PowerSamples(events));

    /// <summary>
    /// The same rollup, but bucketed by hours elapsed since <paramref name="windowStartLocal"/>
    /// instead of by clock hour, so the newest reading can sit at the right-hand edge.
    /// </summary>
    public static double[] HourlyEnergyWh(IReadOnlyList<PowerSample> samples, DateTime windowStartLocal)
    {
        var hours = new double[24];
        Accumulate(samples, hours, windowStartLocal);
        return hours;
    }

    /// <inheritdoc cref="HourlyEnergyWh(IReadOnlyList{PowerSample}, DateTime)"/>
    public static double[] HourlyEnergyWh(IReadOnlyList<DeviceEvent> events, DateTime windowStartLocal) =>
        HourlyEnergyWh(PowerSamples(events), windowStartLocal);

    /// <summary>
    /// Today's profile split per appliance, busiest first, so a change in the total can be
    /// traced to the thing that caused it rather than left as an unexplained dip.
    /// </summary>
    public static IReadOnlyList<DeviceHourlyEnergy> HourlyEnergyByDevice(
        IReadOnlyList<PowerSample> samples, DateTime? windowStartLocal = null)
    {
        var result = new List<DeviceHourlyEnergy>();

        foreach (var device in samples.GroupBy(s => s.DeviceId))
        {
            var hours = new double[24];
            Accumulate(device.ToList(), hours, windowStartLocal);
            result.Add(new DeviceHourlyEnergy(device.Key, hours, Math.Round(hours.Sum(), 2)));
        }

        return [.. result.OrderByDescending(d => d.TotalWh)];
    }

    /// <inheritdoc cref="HourlyEnergyByDevice(IReadOnlyList{PowerSample}, DateTime?)"/>
    public static IReadOnlyList<DeviceHourlyEnergy> HourlyEnergyByDevice(
        IReadOnlyList<DeviceEvent> events, DateTime? windowStartLocal = null) =>
        HourlyEnergyByDevice(PowerSamples(events), windowStartLocal);

    private static void Accumulate(IReadOnlyList<PowerSample> samples, double[] hours, DateTime? windowStartLocal)
    {
        foreach (var device in samples.GroupBy(s => s.DeviceId))
        {
            var readings = device.OrderBy(s => s.OccurredAtUtc).ToList();

            for (var i = 0; i < readings.Count - 1; i++)
            {
                var watts = readings[i].Watts;
                if (watts <= 0)
                {
                    continue;
                }

                var gap = readings[i + 1].OccurredAtUtc - readings[i].OccurredAtUtc;
                if (gap <= TimeSpan.Zero)
                {
                    continue;
                }

                var capped = gap < MaxIntegrationGap ? gap : MaxIntegrationGap;
                Spread(readings[i].OccurredAtUtc, capped, watts, hours, windowStartLocal);
            }
        }
    }

    /// <summary>
    /// Charges one reading's energy to the hours it actually spans instead of dumping all of
    /// it on the hour it started in. A 30-minute hold that begins at 06:50 belongs partly to
    /// the six o'clock hour and partly to seven, and getting that wrong is what turns a
    /// gentle morning into a spike.
    /// </summary>
    private static void Spread(
        DateTimeOffset startUtc, TimeSpan span, double watts, double[] hours, DateTime? windowStartLocal)
    {
        var cursor = HouseholdTime.ToLocal(startUtc).DateTime;
        var remaining = span;

        while (remaining > TimeSpan.Zero)
        {
            var nextHour = cursor.Date.AddHours(cursor.Hour + 1);
            var slice = nextHour - cursor;
            if (slice > remaining)
            {
                slice = remaining;
            }

            // Either bucket by clock hour (a calendar day) or by hours elapsed since the
            // window opened (a rolling day). Energy outside the window is simply dropped.
            var bucket = windowStartLocal is null
                ? cursor.Hour
                : (int)Math.Floor((cursor - windowStartLocal.Value).TotalHours);

            if (bucket >= 0 && bucket < hours.Length)
            {
                hours[bucket] += watts * slice.TotalHours;
            }

            cursor = cursor.Add(slice);
            remaining -= slice;
        }
    }

    /// <summary>
    /// Loads the last 24 hours of electricity hour by hour, together with the average shape
    /// of the days before it, so the dashboard can draw "now" against "usually".
    /// </summary>
    public async Task<HourlyEnergyProfile> GetHourlyEnergyAsync(
        Guid householdId, int days, CancellationToken ct = default) =>
        await GetHourlyEnergyAsync(householdId, days, HouseholdTime.LocalDate(DateTimeOffset.UtcNow), ct);

    /// <inheritdoc cref="GetHourlyEnergyAsync(Guid, int, CancellationToken)"/>
    /// <param name="today">
    /// The local date to treat as today. Callers that hold a <see cref="TimeProvider"/>
    /// should pass it rather than let this service read the wall clock, so a background
    /// job and its tests see the same day.
    /// </param>
    public async Task<HourlyEnergyProfile> GetHourlyEnergyAsync(
        Guid householdId, int days, DateOnly today, CancellationToken ct = default)
    {
        var from = HouseholdTime.StartOfLocalDayUtc(today.AddDays(-(days - 1)));

        var events = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= from)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var readings = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && (r.DailyEnergyWh != null || r.ApproxWatts != null)
                && r.OccurredAtUtc >= from)
            .OrderBy(r => r.OccurredAtUtc)
            .ToListAsync(ct);

        return BuildHourlyProfile(today, events, readings);
    }

    public static HourlyEnergyProfile BuildHourlyProfile(
        DateOnly today, IReadOnlyList<DeviceEvent> events, IReadOnlyList<PlugMiniReading>? readings = null)
    {
        var samples = PowerSamples(events, readings);

        // The window ends at the newest reading rather than at midnight. A chart that always
        // runs 0時-24時 spends most of the day showing empty hours that have not happened yet,
        // and pushes the part the family actually wants -- what has been going on lately --
        // into a shrinking sliver on the left. Ending "now" at the right edge keeps the last
        // 24 hours on screen whatever time it is opened.
        var latest = samples
            .Select(s => (DateTimeOffset?)s.OccurredAtUtc)
            .Max();

        var endLocal = latest is null
            ? today.ToDateTime(TimeOnly.MinValue).AddDays(1)
            : NextHour(HouseholdTime.ToLocal(latest.Value).DateTime);

        var startLocal = endLocal.AddHours(-24);

        var byDate = samples
            .GroupBy(s => HouseholdTime.LocalDate(s.OccurredAtUtc))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PowerSample>)[.. g]);

        // Today is left out of its own average - otherwise the comparison line chases the
        // bars and can never disagree with them. Days with no readings at all are left out
        // too: a gap in the polling is not evidence that the house was quiet, and averaging
        // it in would drag the whole rhythm toward zero and make every real day look busy.
        var past = byDate
            .Where(kv => kv.Key < today)
            .OrderBy(kv => kv.Key)
            .Select(kv => HourlyEnergyWh(kv.Value))
            .Where(h => h.Sum() > 0)
            .ToList();

        var usual = new double[24];
        if (past.Count > 0)
        {
            // The average is per clock hour, so it has to be rotated onto the window before
            // it can be drawn over it -- otherwise 07時's usual value lands under 00時's bar.
            for (var i = 0; i < 24; i++)
            {
                var clockHour = (startLocal.Hour + i) % 24;
                usual[i] = Math.Round(past.Average(p => p[clockHour]), 3);
            }
        }

        return new HourlyEnergyProfile(
            HourlyEnergyWh(samples, startLocal),
            usual,
            HourlyEnergyByDevice(samples, startLocal),
            past.Count,
            startLocal.Hour);
    }

    private static DateTime NextHour(DateTime local) => local.Date.AddHours(local.Hour + 1);

    /// <summary>
    /// Whether an event is evidence that somebody started using an appliance.
    ///
    /// Being switched on is not enough on its own. A Plug Mini left permanently
    /// energised -- which is how anyone actually lives with one -- reports a small
    /// standby draw forever, so a bare "on" says only that the socket has electricity
    /// in it. The poller already knows this and applies
    /// <see cref="SwitchBotPollingCycleService.InUseWattsThreshold"/> when it decides
    /// what to record; reading the events back has to apply the same rule, or the two
    /// halves of the app disagree about what a use is.
    ///
    /// That disagreement is not hypothetical. Events written before the poller learned
    /// to prefer real watts over volts-times-amps carry the apparent-power figure, and
    /// one production morning a socket sitting idle at 0.3W was logged as 32.7W and
    /// reported to the family as "活動を始めた8時35分". Holding both sides to the same
    /// threshold retires those rows without having to trust when they were written.
    ///
    /// A rise in draw behind a socket that was already on counts too. With a plug left
    /// in the wall the on-transition simply never happens again: the kettle, the
    /// heater and the lamp are all switched at the appliance, and the only thing the
    /// plug can see is the draw moving. Counting only on-transitions made a real day
    /// of someone boiling water and running a heater indistinguishable from a day
    /// where nobody got out of bed -- both scored zero -- so the family was told
    /// "まだ本日の活動記録がありません" while the kettle was still warm. The poller
    /// already filters those rises for significance
    /// (<see cref="SwitchBotPollingCycleService.IsSignificantPowerChange"/>), so every
    /// PowerChange row on record is a real load starting, not thermostat jitter.
    ///
    /// A fall in draw is the end of a use, not the start of one, so it is excluded:
    /// counting both halves would double every use.
    ///
    /// Events with no measurement attached still count. A button press, a motion
    /// sensor and a contact sensor all arrive without watts, and there is no evidence
    /// there to dismiss them with.
    /// </summary>
    private static bool IsUse(DeviceEvent e) =>
        IsPowerOn(e) || IsLoadStarting(e);

    private static bool IsPowerOn(DeviceEvent e) =>
        e.State.Equals("on", StringComparison.OrdinalIgnoreCase)
        && e.PowerWatts is null or >= SwitchBotPollingCycleService.InUseWattsThreshold;

    private static bool IsLoadStarting(DeviceEvent e) =>
        e.EventType.Equals("PowerChange", StringComparison.OrdinalIgnoreCase)
        && e.State.Equals("increased", StringComparison.OrdinalIgnoreCase)
        && e.PowerWatts is null or >= SwitchBotPollingCycleService.InUseWattsThreshold;
}

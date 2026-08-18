using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Infrastructure.Data;

/// <summary>
/// ==========================================================================
///  DEMO DATA ONLY - NOT REAL SENSOR DATA
/// ==========================================================================
/// Generates a household, three mock devices and ~14 days of synthetic device
/// events so the dashboard and the Q&amp;A features are demoable before the
/// physical SwitchBot devices arrive.
///
/// Every generated event is written with <see cref="EventSource.Seed"/> and the
/// devices carry the "demo-" external id prefix, so demo data is always
/// distinguishable from real data in the database.
/// </summary>
public static class DemoDataSeeder
{
    public const string DemoHouseholdName = "見守り隊デモ世帯";
    public const int DemoDays = 14;

    /// <summary>
    /// Cadence of the synthetic meter, matching <c>SwitchBot:PollIntervalMinutes</c>.
    ///
    /// This value is not cosmetic. Both readers of the series hold a sample for a bounded
    /// time and then stop - <see cref="PowerUsageService.MaxSampleSpan"/> at ten minutes,
    /// and ActivityService's integration cap at thirty - so that an appliance which drops
    /// off the network is not credited with running all night. Demo data spaced further
    /// apart than those caps is therefore charged for the cap and no more, which is what
    /// leaves a chart blank for the rest of an appliance's run.
    /// </summary>
    public const int ReadingIntervalMinutes = 5;

    /// <summary>
    /// Draw of an appliance while it is running, in real watts.
    ///
    /// Typical figures for a Japanese home at 100V: an LED ceiling light is tens of watts,
    /// a fan is comparable, and the resistive and compressor loads are an order of
    /// magnitude above both. The point of not giving every appliance the same number is
    /// that the per-device chart is meant to answer "what caused the change", and it
    /// cannot do that if the heater and the bedside lamp draw alike.
    /// </summary>
    private static double RunningWatts(DeviceType type) => type switch
    {
        DeviceType.Light => 34.0,
        DeviceType.Fan => 28.0,
        DeviceType.AirConditioner => 470.0,
        DeviceType.Heater => 730.0,
        DeviceType.Kettle => 1250.0,
        DeviceType.Microwave => 1000.0,
        _ => 40.0
    };

    /// <summary>
    /// Real power over apparent power. Lighting and motor loads are reactive, so a plug
    /// measuring volts and amps reads well above the watts actually consumed - the gap
    /// <see cref="PowerUsageService"/> exists to avoid integrating.
    /// </summary>
    private static double PowerFactor(DeviceType type) => type switch
    {
        DeviceType.Light => 0.60,
        DeviceType.Fan => 0.75,
        DeviceType.Heater => 1.00,
        DeviceType.Kettle => 1.00,
        _ => 0.90
    };

    /// <summary>Draw of a plug whose appliance is switched off but still energised.</summary>
    private const double StandbyWatts = 0.35;

    /// <summary>Nominal Japanese domestic mains voltage.</summary>
    private const double NominalVolts = 101.0;

    /// <summary>Deterministic seed keeps demos reproducible between runs.</summary>
    private const int RandomSeed = 20260808;

    public static async Task<Guid> SeedAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        // Ensure the fixed dev/demo AppUser row exists so HouseholdAccessService and
        // ownership checks work out of the box with zero login.
        var demoUser = await db.AppUsers.FirstOrDefaultAsync(
            u => u.IdentityProvider == "dev" && u.ExternalSubject == "demo", ct);
        if (demoUser is null)
        {
            db.AppUsers.Add(new AppUser
            {
                Id = DevCurrentUserAccessor.DemoUserId,
                IdentityProvider = "dev",
                ExternalSubject = "demo",
                DisplayName = "デモユーザー",
                CreatedAtUtc = now,
                LastLoginAtUtc = now
            });
        }

        var existing = await db.Households.FirstOrDefaultAsync(h => h.Name == DemoHouseholdName, ct);
        if (existing is not null)
        {
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var household = new Household { Name = DemoHouseholdName, DataSourceMode = DataSourceMode.Sample, CreatedAtUtc = now };
        db.Households.Add(household);

        var resident = new Person { HouseholdId = household.Id, DisplayName = "お母さん", Role = PersonRole.Resident, CreatedAtUtc = now };
        var daughter = new Person { HouseholdId = household.Id, DisplayName = "娘", Role = PersonRole.Family, CreatedAtUtc = now };
        var son = new Person { HouseholdId = household.Id, DisplayName = "息子", Role = PersonRole.Family, CreatedAtUtc = now };
        db.People.AddRange(resident, daughter, son);

        var devices = MockDeviceProvider.SeedDevices.Select(d => new Device
        {
            HouseholdId = household.Id,
            ExternalDeviceId = d.ExternalDeviceId,
            Name = d.Name,
            Alias = MockDeviceProvider.SeedAliases[d.ExternalDeviceId],
            DeviceType = d.DeviceType,
            Room = d.Room,
            Provider = DeviceProviderKind.Mock,
            IsEnabled = true,
            RemoteControlAllowed = true,
            SafetyClass = DeviceSafetyPolicy.Classify(d.DeviceType),
            CreatedAtUtc = now
        }).ToList();

        // The seed list includes a Heater, which DeviceSafetyPolicy.Classify marks as
        // Guarded. That device exists so the safety guard-rail can be demonstrated:
        // asking the AI to turn it on must first ask about the surroundings, and the
        // answer to that question - not the AI's confidence - is what energises it.
        db.Devices.AddRange(devices);

        var seedEvents = GenerateEvents(household.Id, devices, now);
        db.DeviceEvents.AddRange(seedEvents);
        db.PlugMiniReadings.AddRange(GenerateReadings(
            household.Id,
            devices,
            seedEvents,
            HouseholdTime.StartOfLocalDayUtc(HouseholdTime.LocalDate(now).AddDays(-(DemoDays - 1))),
            now));

        db.FamilyMessages.AddRange(
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = daughter.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "お母さん、今日は暑いから水分とってね",
                OccurredAtUtc = now.AddHours(-5)
            },
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = resident.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "ありがとう、大丈夫よ",
                OccurredAtUtc = now.AddHours(-4)
            },
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = son.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "週末に顔出すね",
                OccurredAtUtc = now.AddHours(-3)
            });

        await db.SaveChangesAsync(ct);
        return household.Id;
    }

    /// <summary>
    /// Builds a normal daily rhythm (wake ~07:00, wind down ~23:00) and injects the
    /// three abnormal patterns the demo scenario needs.
    /// </summary>
    public static List<DeviceEvent> GenerateEvents(Guid householdId, IReadOnlyList<Device> devices, DateTimeOffset now)
    {
        var today = HouseholdTime.LocalDate(now);
        var events = new List<DeviceEvent>();

        for (var offset = DemoDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);

            // Abnormal pattern A: no activity until late morning (10 days ago).
            var lateStart = offset == 10;
            // Abnormal pattern B: night-time appliance usage (5 days ago).
            var nightActivity = offset == 5;
            // Abnormal pattern C: unusually low activity (3 days ago).
            var lowActivity = offset == 3;

            events.AddRange(GenerateDayEvents(householdId, devices, date, DayRandom(date), lateStart, nightActivity, lowActivity));
        }

        return events.Where(e => e.OccurredAtUtc <= now).OrderBy(e => e.OccurredAtUtc).ToList();
    }

    /// <summary>
    /// Fills in synthetic events for a Sample household from the newest existing
    /// seeded event (exclusive) up to "now". Called periodically so a demo household
    /// never goes stale: without this, the 14-day window generated by
    /// <see cref="SeedAsync"/> would fall further into the past every day and
    /// <c>/api/activity/today</c> would show zero activity forever after day 14.
    /// Production households are never touched: only households the seeder itself
    /// created (Sample mode, <see cref="DemoHouseholdName"/>) are eligible.
    /// </summary>
    public static async Task TopUpAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var household = await db.Households.FirstOrDefaultAsync(
            h => h.Name == DemoHouseholdName && h.DataSourceMode == DataSourceMode.Sample, ct);
        if (household is null)
        {
            // No demo household to top up (e.g. a Production-only deployment).
            return;
        }

        var devices = await db.Devices.Where(d => d.HouseholdId == household.Id).ToListAsync(ct);

        // A demo household created before an appliance was added to the seed list would
        // otherwise never see it: SeedAsync returns early once the household exists, and
        // the mock provider is not real hardware anyone can go and re-sync. Adding the
        // missing ones here keeps a long-lived demo deployment matching the seed list.
        var added = false;
        foreach (var seed in MockDeviceProvider.SeedDevices)
        {
            var alias = MockDeviceProvider.SeedAliases[seed.ExternalDeviceId];
            if (devices.Any(d => d.Alias == alias))
            {
                continue;
            }

            var device = new Device
            {
                HouseholdId = household.Id,
                ExternalDeviceId = seed.ExternalDeviceId,
                Name = seed.Name,
                Alias = alias,
                DeviceType = seed.DeviceType,
                Room = seed.Room,
                Provider = DeviceProviderKind.Mock,
                IsEnabled = true,
                RemoteControlAllowed = true,
                SafetyClass = DeviceSafetyPolicy.Classify(seed.DeviceType),
                CreatedAtUtc = clock.GetUtcNow()
            };

            db.Devices.Add(device);
            devices.Add(device);
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }

        var livingLight = devices.FirstOrDefault(d => d.Alias == "living-light");
        var bedroomLight = devices.FirstOrDefault(d => d.Alias == "bedroom-light");
        var fan = devices.FirstOrDefault(d => d.Alias == "living-fan");
        if (livingLight is null || bedroomLight is null || fan is null)
        {
            // Devices missing (unexpected/partial state); nothing safe to generate.
            return;
        }

        var now = clock.GetUtcNow();
        var newestSeedEvent = await db.DeviceEvents
            .Where(e => e.HouseholdId == household.Id && e.Source == EventSource.Seed)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => (DateTimeOffset?)e.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        if (newestSeedEvent is null)
        {
            // Nothing seeded yet; SeedAsync owns the initial population.
            return;
        }

        var lastEventAt = newestSeedEvent.Value;
        var lastDate = HouseholdTime.LocalDate(lastEventAt);
        var today = HouseholdTime.LocalDate(now);
        if (lastDate > today)
        {
            // Clock moved backwards (or data is already current); nothing to do.
            return;
        }

        var events = new List<DeviceEvent>();
        for (var date = lastDate; date <= today; date = date.AddDays(1))
        {
            events.AddRange(GenerateDayEvents(household.Id, devices, date, DayRandom(date), lateStart: false, nightActivity: false, lowActivity: false));
        }

        // Only keep events strictly after the newest existing one and not in the
        // future: this is what makes calling TopUpAsync repeatedly idempotent, since
        // day generation is deterministic (seeded by date) and re-running it for a
        // day already covered simply reproduces events that are filtered out here.
        var newEvents = events.Where(e => e.OccurredAtUtc > lastEventAt && e.OccurredAtUtc <= now).ToList();
        if (newEvents.Count > 0)
        {
            db.DeviceEvents.AddRange(newEvents);
        }

        // Measurements are topped up separately from switch events, and against their own
        // watermark. A plug reports its draw every poll whether or not anything was
        // switched, so an afternoon in which nobody touched an appliance still owes the
        // chart its readings - gating them on new events would reintroduce the very gaps
        // this data exists to fill.
        var newReadings = await TopUpReadingsAsync(db, household.Id, devices, events, now, ct);
        if (newEvents.Count == 0 && newReadings == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Extends the synthetic meter up to <paramref name="now"/>, starting from the newest
    /// reading already stored. Returns how many rows were queued.
    /// </summary>
    private static async Task<int> TopUpReadingsAsync(
        AppDbContext db,
        Guid householdId,
        IReadOnlyList<Device> devices,
        IReadOnlyList<DeviceEvent> generatedEvents,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var newestReading = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId)
            .OrderByDescending(r => r.OccurredAtUtc)
            .Select(r => (DateTimeOffset?)r.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        // A demo household seeded before this data existed has switch events but no
        // measurements at all. Backfilling the same window the seeder would have written
        // is what repairs it in place, rather than leaving those deployments with the
        // gapped chart for ever.
        var from = newestReading?.AddTicks(1)
            ?? HouseholdTime.StartOfLocalDayUtc(HouseholdTime.LocalDate(now).AddDays(-(DemoDays - 1)));

        if (from >= now)
        {
            return 0;
        }

        // Readings need the switch timeline that covers the window, including the events
        // already in the database: the state an appliance was left in yesterday is what
        // decides whether this morning's first reading is a run or a standby.
        var storedEvents = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.Source == EventSource.Seed)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var timeline = storedEvents
            .Concat(generatedEvents.Where(e => e.OccurredAtUtc > (storedEvents.Count > 0 ? storedEvents[^1].OccurredAtUtc : DateTimeOffset.MinValue)
                && e.OccurredAtUtc <= now))
            .OrderBy(e => e.OccurredAtUtc)
            .ToList();

        var readings = GenerateReadings(householdId, devices, timeline, from, now);
        if (readings.Count == 0)
        {
            return 0;
        }

        db.PlugMiniReadings.AddRange(readings);
        return readings.Count;
    }

    /// <summary>Deterministic per-day PRNG so a given local date always generates the same events, whether produced by the initial seed or a later top-up.</summary>
    private static Random DayRandom(DateOnly date) => new(RandomSeed ^ date.DayNumber);

    /// <summary>
    /// Generates one day's worth of synthetic device events following the normal
    /// household rhythm (wake, daytime activity, evening wind-down), with the demo's
    /// abnormal patterns available as opt-in flags.
    /// </summary>
    private static List<DeviceEvent> GenerateDayEvents(
        Guid householdId,
        IReadOnlyList<Device> devices,
        DateOnly date,
        Random random,
        bool lateStart,
        bool nightActivity,
        bool lowActivity)
    {
        var events = new List<DeviceEvent>();
        var dayStart = HouseholdTime.StartOfLocalDayUtc(date);

        var livingLight = devices.First(d => d.Alias == "living-light");
        var bedroomLight = devices.First(d => d.Alias == "bedroom-light");
        var fan = devices.First(d => d.Alias == "living-fan");

        var wakeMinutes = lateStart
            ? 11 * 60 + random.Next(0, 30)
            : 7 * 60 + random.Next(-20, 20);

        Add(events, householdId, bedroomLight, dayStart, wakeMinutes, "on");
        Add(events, householdId, bedroomLight, dayStart, wakeMinutes + 12, "off");
        Add(events, householdId, livingLight, dayStart, wakeMinutes + 15, "on");

        if (!lowActivity)
        {
            Add(events, householdId, fan, dayStart, 13 * 60 + random.Next(-30, 30), "on");
            Add(events, householdId, fan, dayStart, 17 * 60 + random.Next(-30, 30), "off");
            Add(events, householdId, livingLight, dayStart, 12 * 60 + random.Next(-20, 20), "off");
            Add(events, householdId, livingLight, dayStart, 18 * 60 + random.Next(-20, 20), "on");
        }

        if (nightActivity)
        {
            Add(events, householdId, livingLight, dayStart, 2 * 60 + 10, "on");
            Add(events, householdId, livingLight, dayStart, 2 * 60 + 40, "off");
            Add(events, householdId, bedroomLight, dayStart, 3 * 60 + 5, "on");
            Add(events, householdId, bedroomLight, dayStart, 3 * 60 + 20, "off");
        }

        // The current day is only simulated up to "now" (filtered by the caller).
        var sleepMinutes = 23 * 60 + random.Next(-25, 25);
        Add(events, householdId, livingLight, dayStart, sleepMinutes, "off");
        Add(events, householdId, bedroomLight, dayStart, sleepMinutes + 5, "on");
        Add(events, householdId, bedroomLight, dayStart, sleepMinutes + 35, "off");

        return events;
    }

    private static void Add(
        List<DeviceEvent> events, Guid householdId, Device device, DateTimeOffset dayStartUtc, int minutesFromLocalMidnight, string state)
    {
        events.Add(new DeviceEvent
        {
            HouseholdId = householdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = state,
            PowerWatts = state == "on" ? RunningWatts(device.DeviceType) : 0.0,
            Source = EventSource.Seed,
            OccurredAtUtc = dayStartUtc.AddMinutes(minutesFromLocalMidnight),
            ReceivedAtUtc = dayStartUtc.AddMinutes(minutesFromLocalMidnight),
            RawPayloadJson = null
        });
    }

    /// <summary>
    /// Turns the on/off timeline into the measurements a Plug Mini would have written
    /// while it ran.
    ///
    /// Switch events alone cannot draw an electricity chart. Every reader of the series
    /// holds a sample for a capped interval and then stops crediting it, so a light
    /// switched on at 07:13 and off at 11:51 shows up as half an hour of draw followed by
    /// four empty hours - the appliance was plainly running and the graph says nothing.
    /// Real hardware fills that in by reporting its draw every poll whether or not
    /// anything changed, and until that hardware is present the demo has to do the same
    /// or it demonstrates a defect it does not have.
    ///
    /// Only devices that appear in <paramref name="events"/> are metered, so an appliance
    /// the scenario deliberately leaves switched off stays switched off.
    /// </summary>
    public static List<PlugMiniReading> GenerateReadings(
        Guid householdId,
        IReadOnlyList<Device> devices,
        IReadOnlyList<DeviceEvent> events,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var readings = new List<PlugMiniReading>();
        if (toUtc <= fromUtc)
        {
            return readings;
        }

        var interval = TimeSpan.FromMinutes(ReadingIntervalMinutes);
        var byDevice = events.GroupBy(e => e.DeviceId);

        foreach (var group in byDevice)
        {
            var device = devices.FirstOrDefault(d => d.Id == group.Key);
            if (device is null)
            {
                continue;
            }

            var timeline = group.OrderBy(e => e.OccurredAtUtc).ToList();
            var factor = PowerFactor(device.DeviceType);

            // State before the window opens: whatever the last event prior to it said.
            // Without this a run that starts on the previous day reads as standby until
            // the next switch, which is the same hole this method exists to close.
            var index = 0;
            var running = false;
            while (index < timeline.Count && timeline[index].OccurredAtUtc < fromUtc)
            {
                running = timeline[index].State.Equals("on", StringComparison.OrdinalIgnoreCase);
                index++;
            }

            for (var at = AlignToInterval(fromUtc, interval); at < toUtc; at += interval)
            {
                while (index < timeline.Count && timeline[index].OccurredAtUtc <= at)
                {
                    running = timeline[index].State.Equals("on", StringComparison.OrdinalIgnoreCase);
                    index++;
                }

                var watts = running
                    ? Wobble(RunningWatts(device.DeviceType), device.Id, at)
                    : StandbyWatts;
                var volts = Math.Round(Wobble(NominalVolts, device.Id, at.AddSeconds(1)), 1);

                // The plug measures volts and amps; the watts it reports separately are the
                // real power. Deriving the current from the real figure and the power factor
                // keeps the three columns telling one consistent story, which is what makes
                // the apparent-versus-real distinction visible in the demo at all.
                var apparent = watts / factor;
                readings.Add(new PlugMiniReading
                {
                    HouseholdId = householdId,
                    DeviceId = device.Id,
                    VoltageV = volts,
                    CurrentMa = Math.Round(apparent / volts * 1000.0, 0),
                    DailyEnergyWh = Math.Round(watts, 2),
                    UsageMinutesToday = null,
                    ApproxWatts = Math.Round(apparent, 2),
                    OccurredAtUtc = at,
                    ReceivedAtUtc = at,
                    PublishedToStreamAtUtc = null
                });
            }
        }

        return [.. readings.OrderBy(r => r.OccurredAtUtc)];
    }

    private static DateTimeOffset AlignToInterval(DateTimeOffset at, TimeSpan interval)
    {
        var ticks = at.UtcTicks - (at.UtcTicks % interval.Ticks);
        var aligned = new DateTimeOffset(ticks, TimeSpan.Zero);
        return aligned < at ? aligned + interval : aligned;
    }

    /// <summary>
    /// A deterministic few percent either side of a nominal value, so the trace reads as a
    /// measurement rather than a constant.
    ///
    /// Kept well inside the quarter-swing the poller treats as a different appliance being
    /// switched on, so the wobble never manufactures an event the resident did not cause.
    /// Derived from the device and the instant rather than a running PRNG, so a top-up
    /// regenerates exactly what the initial seed would have written.
    /// </summary>
    private static double Wobble(double nominal, Guid deviceId, DateTimeOffset at)
    {
        var seed = HashCode.Combine(deviceId, at.ToUnixTimeSeconds());
        var unit = (seed & 0xFFFF) / 65535.0;
        return Math.Round(nominal * (1.0 + ((unit - 0.5) * 0.08)), 2);
    }
}

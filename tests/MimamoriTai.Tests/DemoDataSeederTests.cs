using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

public class DemoDataSeederTests
{
    [Fact]
    public async Task TopUpAsync_GeneratesEventsUpToNow()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);

        // Move the clock 5 days forward, well past the 14-day window SeedAsync built.
        clock.Advance(TimeSpan.FromDays(5));

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        var newest = db.Context.DeviceEvents
            .Where(e => e.Source == EventSource.Seed)
            .Max(e => e.OccurredAtUtc);

        // The newest event should now be close to "now" (within one simulated day),
        // rather than stuck at the end of the original 14-day demo window.
        Assert.True(newest > clock.GetUtcNow().AddDays(-1));
        Assert.True(newest <= clock.GetUtcNow());
    }

    [Fact]
    public async Task TopUpAsync_IsIdempotent()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);
        clock.Advance(TimeSpan.FromDays(5));

        await DemoDataSeeder.TopUpAsync(db.Context, clock);
        var countAfterFirst = db.Context.DeviceEvents.Count();

        // Calling it again for the same "now" must not add any more events.
        await DemoDataSeeder.TopUpAsync(db.Context, clock);
        var countAfterSecond = db.Context.DeviceEvents.Count();

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task TopUpAsync_DoesNotTouchProductionHousehold()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // A Production household that happens to share the demo household's name,
        // so a name-only lookup would incorrectly match it. TopUpAsync must filter
        // on DataSourceMode too.
        var production = new Household
        {
            Name = DemoDataSeeder.DemoHouseholdName,
            DataSourceMode = DataSourceMode.Production,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Context.Households.Add(production);
        var device = TestDb.Light();
        device.HouseholdId = production.Id;
        db.Context.Devices.Add(device);
        await db.Context.SaveChangesAsync();

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.Empty(db.Context.DeviceEvents.Where(e => e.HouseholdId == production.Id));
    }

    [Fact]
    public async Task TopUpAsync_OnEmptyDatabase_IsNoOp()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // No exception, no rows created, when there is no demo household at all.
        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.Empty(db.Context.Households);
        Assert.Empty(db.Context.DeviceEvents);
    }

    /// <summary>
    /// The demo used to hand the activity chart nothing but on/off events. A light left
    /// on from 07:13 to 11:51 produced a single sample four and a half hours wide, and
    /// <see cref="ActivityService"/> deliberately stops counting after 30 minutes so a
    /// plug that drops off the network cannot bill a family for an entire night. The
    /// chart therefore showed six lit hours out of twenty-four and looked broken. Real
    /// plugs report every five minutes, so the demo has to as well.
    /// </summary>
    [Fact]
    public void GeneratedDay_FillsEveryHourOfTheChart()
    {
        var (devices, events, readings, day) = GenerateDemoDay();

        var dayEvents = events.Where(e => HouseholdTime.LocalDate(e.OccurredAtUtc) == day).ToList();
        var dayReadings = readings.Where(r => HouseholdTime.LocalDate(r.OccurredAtUtc) == day).ToList();
        var hours = ActivityService.HourlyEnergyWh(
            ActivityService.PowerSamples(dayEvents, dayReadings));

        Assert.All(hours, wh => Assert.True(wh > 0, $"a demo hour is empty: [{string.Join(", ", hours.Select(h => h.ToString("F1")))}]"));

        // The lit hours have to stand out from the standby floor, otherwise the chart is
        // continuous but flat and says nothing about when the household was awake.
        Assert.True(hours.Max() > hours.Min() * 10);
        _ = devices;
    }

    /// <summary>
    /// Both integrators cap the span they will credit to one sample, so readings spaced
    /// wider than the tighter cap would leave the same holes this fixture exists to
    /// prevent. Pinning the relationship here means a change to either cap fails loudly.
    /// </summary>
    [Fact]
    public void ReadingInterval_StaysUnderBothIntegrationCaps()
    {
        var interval = TimeSpan.FromMinutes(DemoDataSeeder.ReadingIntervalMinutes);

        Assert.True(interval < PowerUsageService.MaxSampleSpan);
        Assert.True(interval < TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// Readings belong only to appliances the demo actually switches. The air
    /// conditioner and the heater stay untouched on purpose so the comfort suggestion
    /// can offer to turn the cooling on and the safety guard can refuse the heater;
    /// inventing power for them would silently delete both demos.
    /// </summary>
    [Fact]
    public void Readings_CoverOnlyAppliancesTheDemoSwitches()
    {
        var (devices, events, readings, _) = GenerateDemoDay();

        var switched = events.Select(e => e.DeviceId).Distinct().ToHashSet();
        var measured = readings.Select(r => r.DeviceId).Distinct().ToHashSet();

        Assert.Equal(switched, measured);

        var idle = devices.Where(d => !switched.Contains(d.Id)).Select(d => d.DeviceType).ToList();
        Assert.Contains(DeviceType.AirConditioner, idle);
    }

    [Fact]
    public async Task TopUpAsync_IsIdempotentForReadings()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);
        clock.Advance(TimeSpan.FromDays(5));

        await DemoDataSeeder.TopUpAsync(db.Context, clock);
        var afterFirst = db.Context.PlugMiniReadings.Count();
        Assert.True(afterFirst > 0);

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.Equal(afterFirst, db.Context.PlugMiniReadings.Count());
    }

    /// <summary>
    /// Environments seeded before readings existed hold events but no readings at all.
    /// Topping up from the newest reading would find none and back-fill nothing, so
    /// those demos would keep the broken chart forever.
    /// </summary>
    [Fact]
    public async Task TopUpAsync_BackfillsReadingsForAnOlderDemoHousehold()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);
        db.Context.PlugMiniReadings.RemoveRange(db.Context.PlugMiniReadings);
        await db.Context.SaveChangesAsync();

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.NotEmpty(db.Context.PlugMiniReadings);
    }

    private static (List<Device> Devices, List<DeviceEvent> Events, List<PlugMiniReading> Readings, DateOnly Day)
        GenerateDemoDay()
    {
        var householdId = Guid.NewGuid();
        var devices = MockDeviceProvider.SeedDevices.Select(d => new Device
        {
            HouseholdId = householdId,
            ExternalDeviceId = d.ExternalDeviceId,
            Name = d.Name,
            Alias = MockDeviceProvider.SeedAliases[d.ExternalDeviceId],
            DeviceType = d.DeviceType,
            Room = d.Room
        }).ToList();

        // A fixed instant keeps the assertions reproducible; the generator is
        // deterministic for a given household and time.
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var events = DemoDataSeeder.GenerateEvents(householdId, devices, now);
        var from = HouseholdTime.StartOfLocalDayUtc(
            HouseholdTime.LocalDate(now).AddDays(-(DemoDataSeeder.DemoDays - 1)));
        var readings = DemoDataSeeder.GenerateReadings(householdId, devices, events, from, now);

        return (devices, events, readings, HouseholdTime.LocalDate(now).AddDays(-1));
    }
}

using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>Controllable publisher stub for the heat stream: records batches and can be forced to fail.</summary>
public sealed class FakeHeatReadingStreamPublisher : IHeatReadingStreamPublisher
{
    public bool IsConfigured { get; init; } = true;

    public string DisplayName => "FakeHeatReadingStream";

    public bool ShouldFail { get; set; }

    public List<IReadOnlyList<HeatReadingRecord>> Calls { get; } = [];

    public Task<EventStreamPublishResult> PublishAsync(IReadOnlyList<HeatReadingRecord> readings, CancellationToken ct = default)
    {
        Calls.Add(readings);

        if (ShouldFail)
        {
            return Task.FromResult(new EventStreamPublishResult(false, 0, 0, "simulated failure"));
        }

        return Task.FromResult(new EventStreamPublishResult(true, readings.Count, 0));
    }
}

/// <summary>Returns a fixed advisory (or nothing), like the real provider does out of season.</summary>
public sealed class FakeHeatAdvisoryProvider(HeatAdvisory? advisory) : IHeatAdvisoryProvider
{
    public int Calls { get; private set; }

    public HeatAdvisory? Advisory { get; set; } = advisory;

    public Task<HeatAdvisory?> GetCurrentAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Advisory);
    }
}

public class HeatReadingServiceTests
{
    private const string Point = "44132";

    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static HeatAdvisory Advisory(double wbgt, DateTimeOffset observedAtUtc) => new(
        wbgt,
        HeatAdvisory.Classify(wbgt),
        26.1,
        78.0,
        observedAtUtc,
        "東京",
        "環境省熱中症予防情報サイト");

    private static HeatReading Reading(DateTimeOffset observedAtUtc, DateTimeOffset? publishedAtUtc = null) => new()
    {
        PointCode = Point,
        AreaName = "東京",
        Wbgt = 27.0,
        Level = (int)HeatAlertLevel.Warning,
        TemperatureC = 26.1,
        HumidityPercent = 78.0,
        ObservedAtUtc = observedAtUtc,
        PublishedToStreamAtUtc = publishedAtUtc
    };

    [Fact]
    public async Task CaptureAsync_Stores_A_New_Observation()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var provider = new FakeHeatAdvisoryProvider(Advisory(27.0, Now.AddHours(-1)));
        var service = new HeatReadingService(db.Context, new FakeHeatReadingStreamPublisher(), new FakeTimeProvider(Now), provider);

        var advisory = await service.CaptureAsync(Point);

        Assert.NotNull(advisory);
        var stored = await db.Context.HeatReadings.SingleAsync();
        Assert.Equal(Point, stored.PointCode);
        Assert.Equal("東京", stored.AreaName);
        Assert.Equal(27.0, stored.Wbgt);
        Assert.Equal((int)HeatAlertLevel.Warning, stored.Level);
        Assert.Equal(Now, stored.ReceivedAtUtc);
        Assert.Null(stored.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task CaptureAsync_Does_Not_Duplicate_The_Same_Observation_Time()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var observedAtUtc = Now.AddHours(-1);
        var provider = new FakeHeatAdvisoryProvider(Advisory(27.0, observedAtUtc));
        var service = new HeatReadingService(db.Context, new FakeHeatReadingStreamPublisher(), new FakeTimeProvider(Now), provider);

        // The provider serves the same forecast column for its whole cache window, so
        // repeated cycles legitimately see an observation time that is already stored.
        await service.CaptureAsync(Point);
        var second = await service.CaptureAsync(Point);

        Assert.NotNull(second);
        Assert.Equal(1, await db.Context.HeatReadings.CountAsync());
    }

    [Fact]
    public async Task CaptureAsync_Stores_A_Second_Row_When_The_Observation_Time_Moves_On()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var provider = new FakeHeatAdvisoryProvider(Advisory(27.0, Now.AddHours(-3)));
        var service = new HeatReadingService(db.Context, new FakeHeatReadingStreamPublisher(), new FakeTimeProvider(Now), provider);

        await service.CaptureAsync(Point);
        provider.Advisory = Advisory(29.0, Now);
        await service.CaptureAsync(Point);

        Assert.Equal(2, await db.Context.HeatReadings.CountAsync());
    }

    [Fact]
    public async Task CaptureAsync_Stores_Nothing_When_Open_Data_Is_Unavailable()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var provider = new FakeHeatAdvisoryProvider(null);
        var service = new HeatReadingService(db.Context, new FakeHeatReadingStreamPublisher(), new FakeTimeProvider(Now), provider);

        var advisory = await service.CaptureAsync(Point);

        Assert.Null(advisory);
        Assert.Empty(await db.Context.HeatReadings.ToListAsync());
    }

    [Fact]
    public async Task CaptureAsync_Is_A_NoOp_Without_A_Provider()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = new HeatReadingService(db.Context, new FakeHeatReadingStreamPublisher(), new FakeTimeProvider(Now));

        Assert.Null(await service.CaptureAsync(Point));
        Assert.Empty(await db.Context.HeatReadings.ToListAsync());
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Returns_Empty_When_There_Is_Nothing_Pending()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var publisher = new FakeHeatReadingStreamPublisher();
        var service = new HeatReadingService(db.Context, publisher, new FakeTimeProvider(Now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(0, result.Attempted);
        Assert.True(result.Success);
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Publishes_And_Stamps()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var reading = Reading(Now.AddHours(-1));
        db.Context.HeatReadings.Add(reading);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeHeatReadingStreamPublisher();
        var service = new HeatReadingService(db.Context, publisher, new FakeTimeProvider(Now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Published);
        Assert.True(result.Success);
        Assert.Equal("警戒", publisher.Calls[0][0].LevelText);

        var reloaded = await db.Context.HeatReadings.SingleAsync(r => r.Id == reading.Id);
        Assert.Equal(Now, reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Leaves_Rows_Unstamped_When_Publisher_Fails()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var reading = Reading(Now.AddHours(-1));
        db.Context.HeatReadings.Add(reading);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeHeatReadingStreamPublisher { ShouldFail = true };
        var service = new HeatReadingService(db.Context, publisher, new FakeTimeProvider(Now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.False(result.Success);
        Assert.Equal("simulated failure", result.Error);

        var reloaded = await db.Context.HeatReadings.SingleAsync(r => r.Id == reading.Id);
        Assert.Null(reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Skips_Already_Published_And_Orders_Oldest_First()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var published = Reading(Now.AddHours(-9), publishedAtUtc: Now.AddHours(-8));
        var older = Reading(Now.AddHours(-6));
        var newer = Reading(Now.AddHours(-3));
        db.Context.HeatReadings.AddRange(newer, published, older);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeHeatReadingStreamPublisher();
        var service = new HeatReadingService(db.Context, publisher, new FakeTimeProvider(Now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(2, result.Attempted);
        Assert.Equal(older.Id, publisher.Calls[0][0].ReadingId);
        Assert.Equal(newer.Id, publisher.Calls[0][1].ReadingId);
    }

    [Fact]
    public void Project_Denormalises_The_Band_Label_For_Downstream_KQL()
    {
        var reading = Reading(Now);
        reading.Level = (int)HeatAlertLevel.Danger;

        var record = Assert.Single(HeatReadingService.Project([reading]));

        Assert.Equal("危険", record.LevelText);
        Assert.Equal((int)HeatAlertLevel.Danger, record.Level);
        Assert.Equal(DateTimeKind.Utc, record.ObservedAtUtc.Kind);
    }
}

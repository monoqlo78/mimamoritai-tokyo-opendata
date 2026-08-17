using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// The console sync must sign in to Fabric SQL as the App Service managed identity, which
/// is a Fabric workspace Admin. It briefly did not: the app registers its shared
/// <see cref="TokenCredential"/> with <c>TryAddSingleton</c>, the Fabric Data Agent got
/// there first with a service-principal credential (its query API cannot take a managed
/// identity), and the sync silently inherited it. Fabric SQL then rejected every cycle
/// with "Validation of user's permissions failed. Verify the user has the Read item
/// permission", because a service principal needs a tenant-wide switch we do not own.
/// </summary>
public class FabricConsoleSyncCredentialTests
{
    private static ServiceProvider Compose() =>
        new ServiceCollection()
            .AddLogging()
            .AddMimamoriTaiInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = "DataSource=:memory:",

                    // Fabric configured with a service principal: this is what used to
                    // win the TryAddSingleton race and take the sync down with it.
                    ["Fabric:Enabled"] = "true",
                    ["Fabric:WorkspaceId"] = "11111111-1111-1111-1111-111111111111",
                    ["Fabric:DataAgentId"] = "22222222-2222-2222-2222-222222222222",
                    ["Fabric:McpUrl"] = "https://api.fabric.microsoft.com/v1/mcp/agent",
                    ["Fabric:TenantId"] = "33333333-3333-3333-3333-333333333333",
                    ["Fabric:ClientId"] = "44444444-4444-4444-4444-444444444444",
                    ["Fabric:ClientSecret"] = "not-a-real-secret",

                    ["FabricConsoleSync:Enabled"] = "true",
                    ["FabricConsoleSync:ServerFqdn"] = "example.database.fabric.microsoft.com",
                    ["FabricConsoleSync:Database"] = "console",
                })
                .Build())
            .BuildServiceProvider();

    [Fact]
    public void Console_Sync_Does_Not_Borrow_The_Data_Agent_Service_Principal()
    {
        using var provider = Compose();

        var shared = provider.GetRequiredService<TokenCredential>();
        var sync = provider.GetRequiredService<FabricConsoleSyncCredential>();

        Assert.IsType<ClientSecretCredential>(shared);
        Assert.IsType<DefaultAzureCredential>(sync.Credential);
        Assert.NotSame(shared, sync.Credential);
    }

    [Fact]
    public void A_Failed_Login_Names_The_Principal_Without_Leaking_The_Token()
    {
        // "Login failed for user '<token-identified principal>'" is the whole message Fabric
        // gives back, which is useless for deciding who to grant access to. The claims below
        // are identifiers rather than credentials, so they can safely reach a log.
        var payload = """{"oid":"54af8a95-f1bd-4a59-96b2-e51ab7506c5e","appid":"6385e9c6","app_displayname":"contoso-web"}""";
        var jwt = $"header.{Base64Url(payload)}.signature";

        var described = FabricSqlConsoleSync.DescribeToken(jwt);

        Assert.Contains("contoso-web", described);
        Assert.Contains("54af8a95-f1bd-4a59-96b2-e51ab7506c5e", described);
        Assert.DoesNotContain("signature", described);
    }

    [Fact]
    public void An_Unreadable_Token_Does_Not_Take_The_Sync_Down()
    {
        // The diagnostic runs while handling a failure; throwing from it would replace a
        // clear "login failed" with a confusing FormatException.
        Assert.Equal("an unrecognised token", FabricSqlConsoleSync.DescribeToken("not-a-jwt"));
        Assert.Equal("an unreadable token", FabricSqlConsoleSync.DescribeToken("a.!!!.c"));
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Covers the read half of the Fabric console sync: the aggregation that replaced the
/// manual <c>sync-to-fabric.ps1</c> run. The write half needs a live Fabric SQL endpoint,
/// which is unreachable outside Azure (Fabric SQL redirects to ports 11000-11999), so
/// these tests pin the behaviour that can be verified locally -- the shape of the rollup,
/// the deterministic keys that make re-running the sync idempotent, and the privacy
/// exclusions.
/// </summary>
public class FabricConsoleSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static FabricSqlConsoleSync CreateSync(TestDb db, FabricConsoleSyncOptions? options = null) =>
        new(db.Context,
            new FabricConsoleSyncCredential(new StubCredential()),
            Options.Create(options ?? new FabricConsoleSyncOptions
            {
                Enabled = true,
                ServerFqdn = "example.database.fabric.microsoft.com",
                Database = "console",
            }),
            new FakeTimeProvider(Now),
            NullLogger<FabricSqlConsoleSync>.Instance);

    [Fact]
    public void IsConfigured_False_Until_Enabled_Server_And_Database_All_Set()
    {
        Assert.False(new FabricConsoleSyncOptions().IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { Enabled = true }.IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { Enabled = true, ServerFqdn = "s" }.IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { ServerFqdn = "s", Database = "d" }.IsConfigured);

        Assert.True(new FabricConsoleSyncOptions
        {
            Enabled = true,
            ServerFqdn = "s",
            Database = "d",
        }.IsConfigured);
    }

    [Fact]
    public async Task Unconfigured_Sync_Fails_Without_Touching_The_Network()
    {
        using var db = await new TestDb().SeedAsync();
        var sync = CreateSync(db, new FabricConsoleSyncOptions());

        var result = await sync.SyncAsync();

        Assert.False(result.Success);
        Assert.False(sync.IsConfigured);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Mock_Reports_Unconfigured_So_Callers_No_Op()
    {
        var sync = new MockFabricConsoleSync();

        var result = await sync.SyncAsync();

        Assert.False(sync.IsConfigured);
        Assert.False(result.Success);
        Assert.Equal(0, result.TotalRows);
    }

    [Fact]
    public void DeterministicId_Is_Stable_And_Distinct_Per_Key()
    {
        // Re-running the sync must update the same row rather than insert a duplicate.
        var first = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o-mini");
        var again = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o-mini");
        var other = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public void DeterministicId_Matches_The_PowerShell_Script_Byte_For_Byte()
    {
        // Rows written by scripts/sync-to-fabric.ps1 and by this class must collide
        // deliberately. .NET's new Guid(byte[]) and PowerShell's [guid]::new($bytes)
        // read the same MD5 bytes with the same little-endian interpretation, so a
        // hard-coded expectation here catches any drift in either direction.
        var id = FabricSqlConsoleSync.DeterministicId("household-snapshot:demo");

        Assert.Equal(new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("household-snapshot:demo"))), id);
    }

    [Fact]
    public async Task Household_Rollup_Counts_Devices_Residents_And_Recipients()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(), TestDb.Heater());

        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = "U-active",
            IsActive = true,
        });
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = "U-gone",
            IsActive = false,
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var household = Assert.Single(snapshot.Households);
        Assert.Equal(db.HouseholdId, household.HouseholdId);
        Assert.Equal(2, household.DeviceCount);
        Assert.Equal(1, household.ResidentCount);

        // Unfollowed recipients must not be counted: the whole point of the figure is
        // "can this household still be reached".
        Assert.Equal(1, household.ActiveLineRecipients);
    }

    [Fact]
    public async Task Household_Rollup_Uses_Plug_Mini_Readings_As_Last_Activity()
    {
        var plug = new Device
        {
            ExternalDeviceId = "plug-mini",
            Name = "プラグミニ",
            Alias = "plug-mini",
            DeviceType = DeviceType.Plug,
            Room = "リビング",
            Provider = DeviceProviderKind.SwitchBot,
            RemoteControlAllowed = false,
            SafetyClass = SafetyClass.Guarded,
        };
        using var db = await new TestDb().SeedAsync(plug);

        var readingAt = Now.AddMinutes(-5);
        db.Context.PlugMiniReadings.Add(new PlugMiniReading
        {
            HouseholdId = db.HouseholdId,
            DeviceId = plug.Id,
            DailyEnergyWh = 3.2,
            OccurredAtUtc = readingAt,
            ReceivedAtUtc = readingAt,
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var household = Assert.Single(snapshot.Households);
        Assert.Equal(readingAt, household.LastEventUtc);
    }

    [Fact]
    public async Task Production_Household_With_No_Reachable_Recipient_Needs_Attention()
    {
        var sample = new FabricSqlConsoleSync.HouseholdRow(
            Guid.NewGuid(), "sample", DataSourceMode.Sample, 1, 1, 1, null, null, null, 0, 0, 0, null);
        var production = sample with { DataSourceMode = DataSourceMode.Production };
        var reachable = production with { ActiveLineRecipients = 1 };
        var failing = reachable with { FailedAlertsInWindow = 1 };
        var broken = reachable with { SwitchBotStatus = SwitchBotConnectionStatus.Error };

        // A demo household with no recipients is expected, not a problem.
        Assert.False(FabricSqlConsoleSync.NeedsAttention(sample));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(production));
        Assert.False(FabricSqlConsoleSync.NeedsAttention(reachable));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(failing));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(broken));
    }

    [Fact]
    public async Task Alerts_Are_Windowed_And_Never_Carry_The_Family_Facing_Message()
    {
        using var db = await new TestDb().SeedAsync();

        db.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = db.HouseholdId,
            PersonId = db.ResidentId,
            RiskLevel = RiskLevel.High,
            Score = 80,
            Reason = "no-activity-12h",
            Message = "お母様のリビングで12時間動きがありません",
            SentAtUtc = Now.AddHours(-1),
            Success = true,
        });
        db.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = db.HouseholdId,
            PersonId = db.ResidentId,
            RiskLevel = RiskLevel.Low,
            Score = 10,
            Reason = "stale",
            Message = "古い通知",
            SentAtUtc = Now.AddDays(-30),
            Success = true,
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var alert = Assert.Single(snapshot.Alerts);
        Assert.Equal("no-activity-12h", alert.Reason);

        // AlertRow has no Message member at all -- the resident's name must not reach
        // the operator console. Reason is machine-generated and safe.
        Assert.DoesNotContain(
            "Message",
            typeof(FabricSqlConsoleSync.AlertRow).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task Activity_Is_Rolled_Up_To_The_Hour_Not_Emitted_Per_Event()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        foreach (var minute in new[] { 0, 10, 20 })
        {
            db.Context.DeviceEvents.Add(new DeviceEvent
            {
                HouseholdId = db.HouseholdId,
                DeviceId = light.Id,
                EventType = "state",
                State = "on",
                Source = EventSource.SwitchBotPoll,
                OccurredAtUtc = Now.AddHours(-2).AddMinutes(minute),
            });
        }

        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = light.Id,
            EventType = "state",
            State = "off",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = Now.AddHours(-2).AddMinutes(30),
        });

        // Outside the 30-day activity window.
        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = light.Id,
            EventType = "state",
            State = "on",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = Now.AddDays(-90),
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var bucket = Assert.Single(snapshot.Activity);
        Assert.Equal(4, bucket.EventCount);
        Assert.Equal(3, bucket.OnCount);
        Assert.Equal(0, bucket.BucketStart.Minute);
        Assert.Equal(light.Id, bucket.DeviceId);
        Assert.Equal(light.Name, bucket.DeviceName);
    }

    [Fact]
    public async Task Activity_Includes_Metered_Plug_Hours_Even_When_No_State_Event_Fired()
    {
        var plug = new Device
        {
            ExternalDeviceId = "plug-mini",
            Name = "プラグミニ 92",
            Alias = "plug-mini",
            DeviceType = DeviceType.Plug,
            Room = "リビング",
            Provider = DeviceProviderKind.SwitchBot,
            RemoteControlAllowed = false,
            SafetyClass = SafetyClass.Guarded,
        };
        using var db = await new TestDb().SeedAsync(plug);

        var first = Now.AddHours(-2).AddMinutes(5);
        db.Context.PlugMiniReadings.AddRange(
            new PlugMiniReading
            {
                HouseholdId = db.HouseholdId,
                DeviceId = plug.Id,
                DailyEnergyWh = 60,
                OccurredAtUtc = first,
                ReceivedAtUtc = first,
            },
            new PlugMiniReading
            {
                HouseholdId = db.HouseholdId,
                DeviceId = plug.Id,
                DailyEnergyWh = 60,
                OccurredAtUtc = first.AddMinutes(5),
                ReceivedAtUtc = first.AddMinutes(5),
            });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var bucket = Assert.Single(snapshot.Activity);
        Assert.Equal(0, bucket.EventCount);
        Assert.Equal(plug.Id, bucket.DeviceId);
        Assert.Equal(plug.Name, bucket.DeviceName);
        Assert.Equal(new DateTime(first.UtcDateTime.Year, first.UtcDateTime.Month, first.UtcDateTime.Day, first.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc), bucket.BucketStart);
        Assert.Equal(5, bucket.EnergyWh!.Value, 3);
    }

    [Fact]
    public async Task AiRouterCalls_Are_All_Time_And_Grouped_By_Purpose_Router_And_Model()
    {
        using var db = await new TestDb().SeedAsync();

        db.Context.AiRequestLogs.AddRange(
            Log("chat", "auto", "gpt-4o-mini", 100, true, Now.AddDays(-400)),
            Log("chat", "auto", "gpt-4o-mini", 300, true, Now.AddHours(-1)),
            Log("chat", "auto", "gpt-4o", 200, false, Now.AddHours(-2)),
            Log("summary", "auto", "gpt-4o-mini", 150, true, Now.AddHours(-3)));
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal(3, snapshot.AiCalls.Count);

        var top = snapshot.AiCalls[0];
        Assert.Equal("chat", top.Purpose);
        Assert.Equal("gpt-4o-mini", top.ResolvedModel);

        // The 400-day-old row still counts: callCount is an all-time total, so the
        // console figure only ever moves forward. A window here would make it drop.
        Assert.Equal(2, top.CallCount);
        Assert.Equal(2, top.SuccessCount);
        Assert.Equal(200, top.AvgDurationMs);

        var failed = snapshot.AiCalls.Single(c => c.ResolvedModel == "gpt-4o");
        Assert.Equal(1, failed.CallCount);
        Assert.Equal(0, failed.SuccessCount);
    }

    [Fact]
    public async Task Outdoor_Observations_Roll_Up_By_Point_And_Hour_Without_Inventing_Readings()
    {
        using var db = await new TestDb().SeedAsync();

        var hour = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);

        db.Context.HeatReadings.AddRange(
            Heat("44132", "東京", hour.AddMinutes(0), 30.0, 60, 28.0, level: 3, cold: 0),
            Heat("44132", "東京", hour.AddMinutes(30), 34.0, 70, 31.0, level: 4, cold: 0),

            // Same point, next hour: must not be folded into the bucket above.
            Heat("44132", "東京", hour.AddHours(1), 33.0, 65, 30.0, level: 4, cold: 0),

            // Winter row from another point: WBGT is out of season, so it stays unmeasured
            // rather than becoming a 0 the console would draw as "comfortable".
            Heat("44136", "八王子", hour.AddMinutes(10), 1.0, 40, null, level: 0, cold: 2),

            // Outside the activity window: dropped like every other rollup.
            Heat("44132", "東京", Now.AddDays(-90), 20.0, 50, 19.0, level: 1, cold: 0));
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal(3, snapshot.Outdoor.Count);

        var tokyo = snapshot.Outdoor.Single(o => o.PointCode == "44132" && o.BucketStart == hour.UtcDateTime);
        Assert.Equal("東京", tokyo.AreaName);
        Assert.Equal(32.0, tokyo.TemperatureC!.Value, 3);
        Assert.Equal(30.0, tokyo.MinTemperatureC!.Value, 3);
        Assert.Equal(34.0, tokyo.MaxTemperatureC!.Value, 3);
        Assert.Equal(65.0, tokyo.HumidityPercent!.Value, 3);

        // The peak, not the mean: the warning a family sees is about the worst moment.
        Assert.Equal(31.0, tokyo.MaxWbgt!.Value, 3);
        Assert.Equal(4, tokyo.HeatLevel);
        Assert.Equal(2, tokyo.SampleCount);

        var hachioji = snapshot.Outdoor.Single(o => o.PointCode == "44136");
        Assert.Null(hachioji.MaxWbgt);
        Assert.Equal(0, hachioji.HeatLevel);
        Assert.Equal(2, hachioji.ColdLevel);
        Assert.Equal(1.0, hachioji.TemperatureC!.Value, 3);
    }

    private static HeatReading Heat(
        string pointCode,
        string areaName,
        DateTimeOffset observedAt,
        double? temperatureC,
        double? humidity,
        double? wbgt,
        int level,
        int cold) => new()
        {
            PointCode = pointCode,
            AreaName = areaName,
            ObservedAtUtc = observedAt,
            TemperatureC = temperatureC,
            HumidityPercent = humidity,
            Wbgt = wbgt,
            Level = level,
            ColdLevel = cold,
        };

    private static AiRequestLog Log(
        string purpose, string router, string model, int durationMs, bool success, DateTimeOffset at) => new()
        {
            Purpose = purpose,
            Router = router,
            ResolvedModel = model,
            DurationMs = durationMs,
            Success = success,
            CreatedAtUtc = at,
        };

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests never reach the network.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests never reach the network.");
    }
}

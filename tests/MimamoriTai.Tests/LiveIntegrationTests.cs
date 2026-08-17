using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure;
using Xunit.Abstractions;

namespace MimamoriTai.Tests;

/// <summary>
/// Tests that talk to the REAL Azure Model Router and SwitchBot services using the
/// credentials in User Secrets. They are opt-in and are inert no-ops unless the
/// environment asks for them, so `dotnet test` on a machine (or in CI) without
/// credentials stays hermetic, offline and green:
///
///   MIMAMORI_LIVE=1          enables the read-only live checks
///   MIMAMORI_LIVE_CONTROL=1  additionally allows SWITCHING REAL HARDWARE
///
/// The control flag is separate on purpose: everything under MIMAMORI_LIVE only
/// reads, while the control test physically cuts power to whatever is plugged into
/// the smart plug. That test always restores the original power state afterwards,
/// including when the assertions in the middle fail.
/// </summary>
public class LiveIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Matches UserSecretsId in src/MimamoriTai.Web/MimamoriTai.Web.csproj.</summary>
    private const string UserSecretsId = "6712a655-51b4-4e8e-a661-0aca1a64081e";

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("MIMAMORI_LIVE") == "1";

    private static bool ControlEnabled => LiveEnabled && Environment.GetEnvironmentVariable("MIMAMORI_LIVE_CONTROL") == "1";

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddUserSecrets(typeof(Program).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static AzureModelRouterClient CreateAi(IConfiguration config)
    {
        var options = new AzureModelRouterOptions();
        config.GetSection(AzureModelRouterOptions.SectionName).Bind(options);

        // Mirrors the AddHttpClient configuration in ServiceCollectionExtensions:
        // the client issues relative URIs, so BaseAddress must be set here too.
        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BuildBaseAddress()),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 5)
        };

        return new AzureModelRouterClient(
            http,
            Options.Create(options),
            NullLogger<AzureModelRouterClient>.Instance,
            options.UseEntraId ? new Azure.Identity.DefaultAzureCredential() : null);
    }

    private static SwitchBotDeviceProvider CreateSwitchBot(IConfiguration config)
    {
        var options = new SwitchBotOptions();
        config.GetSection(SwitchBotOptions.SectionName).Bind(options);

        var client = new SwitchBotClient(
            new HttpClient(),
            Options.Create(options),
            NullLogger<SwitchBotClient>.Instance);

        return new SwitchBotDeviceProvider(client, NullLogger<SwitchBotDeviceProvider>.Instance);
    }

    private static FabricDataAgentMcpClient CreateFabric(IConfiguration config)
    {
        var options = new FabricOptions();
        config.GetSection(FabricOptions.SectionName).Bind(options);

        // Mirrors the AddHttpClient registration: the data agent can take a while to
        // answer, so the timeout is generous.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        return new FabricDataAgentMcpClient(
            http,
            Options.Create(options),
            ServiceCollectionExtensions.CreateFabricTokenCredential(options),
            NullLogger<FabricDataAgentMcpClient>.Instance);
    }

    /// <summary>
    /// Shows what the real Fabric Data Agent answers, and whether the client accepts
    /// it. A Fabric Data Agent answers HTTP 200 even when it could not reach its
    /// datasource -- it apologises in prose instead -- so a green HTTP status proves
    /// nothing on its own. This test therefore prints both the raw answer and the
    /// accept/reject decision, and only asserts the call did not throw, so it stays
    /// truthful about a datasource that is not reachable yet.
    /// </summary>
    [Fact]
    public async Task Fabric_Live_Reports_What_The_Data_Agent_Actually_Answers()
    {
        if (!LiveEnabled)
        {
            return;
        }

        var fabric = CreateFabric(Config());
        Assert.True(fabric.IsConfigured, "Fabric:* settings are missing from User Secrets.");

        var answer = await fabric.AskAsync("今日の家電の利用状況を教えてください。");

        output.WriteLine($"[LIVE] fabric success={answer.Success} source={answer.Source}");
        output.WriteLine($"[LIVE] fabric error={answer.Error ?? "(none)"}");
        output.WriteLine($"[LIVE] fabric text={answer.Answer}");

        // The orchestrator's contract: an unsuccessful answer must carry a reason so
        // it can fall back to local data rather than surfacing an empty reply.
        if (!answer.Success)
        {
            Assert.False(string.IsNullOrWhiteSpace(answer.Error));
        }
    }

    [Fact]
    public async Task ModelRouter_Live_Answers_A_Prompt()
    {
        if (!LiveEnabled)
        {
            return;
        }

        var ai = CreateAi(Config());
        Assert.True(ai.IsConfigured, "AzureModelRouter:Endpoint / ApiKey are missing from User Secrets.");

        var result = await ai.CompleteAsync(
            [AiMessage.User("「元気ですか」と一言だけ日本語で返事してください。")],
            purpose: "live-smoke");

        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));

        // ResolvedModel is read from the response's model field, which names the model
        // the router actually chose rather than the deployment that was asked for. A
        // value different from the deployment name is what proves routing happened.
        Assert.False(string.IsNullOrWhiteSpace(result.Router));
        Assert.False(string.IsNullOrWhiteSpace(result.ResolvedModel));

        output.WriteLine($"[LIVE] router={result.Router} resolvedModel={result.ResolvedModel} {result.DurationMs}ms");
        output.WriteLine($"[LIVE] content={result.Content}");
    }

    /// <summary>
    /// The regression this pins down: intent parsing asks for
    /// <c>response_format: json_object</c>, which some models silently ignore -- they
    /// answer with the JSON wrapped in a markdown code fence, which is not parseable.
    /// Model router picks the underlying model per request, so this asserts the live
    /// response really is parseable JSON whichever model it lands on.
    /// </summary>
    [Fact]
    public async Task ModelRouter_Live_Json_Mode_Returns_Parseable_Json()
    {
        if (!LiveEnabled)
        {
            return;
        }

        var ai = CreateAi(Config());

        var result = await ai.CompleteAsync(
            [
                AiMessage.System("You answer with a JSON object only."),
                AiMessage.User("""Return exactly {"intent":"control_device","action":"turn_off"}.""")
            ],
            purpose: "live-json",
            jsonMode: true);

        Assert.True(result.Success, result.Error);

        var parsed = JsonDocument.Parse(result.Content);
        Assert.Equal("control_device", parsed.RootElement.GetProperty("intent").GetString());

        output.WriteLine($"[LIVE] jsonModel={result.ResolvedModel} raw={result.Content}");
    }

    [Fact]
    public async Task SwitchBot_Live_Lists_Real_Devices()
    {
        if (!LiveEnabled)
        {
            return;
        }

        var provider = CreateSwitchBot(Config());
        Assert.True(provider.IsConfigured, "SwitchBot:Token / SwitchBot:Secret are missing from User Secrets.");

        var devices = await provider.GetDevicesAsync();

        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.ExternalDeviceId)));

        foreach (var d in devices)
        {
            output.WriteLine($"[LIVE] device id={d.ExternalDeviceId} name={d.Name} type={d.DeviceType} room={d.Room}");
        }
    }

    /// <summary>
    /// Read-only end-to-end proof that a natural-language "turn it off" against real
    /// hardware stops at the confirmation gate: the assistant must only propose, and
    /// no command may reach the device. Safe to run without MIMAMORI_LIVE_CONTROL
    /// precisely because nothing is executed.
    /// </summary>
    [Fact]
    public async Task Assistant_Live_Proposes_Before_Touching_Real_Hardware()
    {
        if (!LiveEnabled)
        {
            return;
        }

        var config = Config();
        var provider = CreateSwitchBot(config);
        var real = await FirstControllableAsync(provider);

        using var db = await new TestDb().SeedAsync(AsDevice(real));
        var orchestrator = CreateOrchestrator(db, CreateAi(config), provider);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, $"{real.Name}を消して", CommandSource.Web));

        Assert.True(response.AwaitingConfirmation);
        Assert.False(response.DeviceChanged);

        // Nothing may have been sent to the physical device yet.
        Assert.Empty(db.Context.DeviceCommands);

        output.WriteLine($"[LIVE] target={real.Name} ({real.ExternalDeviceId})");
        output.WriteLine($"[LIVE] awaitingConfirmation={response.AwaitingConfirmation} deviceChanged={response.DeviceChanged}");
        output.WriteLine($"[LIVE] reply={response.Reply}");
    }

    /// <summary>
    /// The full "家の電気を消して" journey against the real SwitchBot cloud:
    /// propose -> confirm -> the device actually switches off. Restores the original
    /// power state in a finally block so a developer's home is left as it was found.
    /// </summary>
    [Fact]
    public async Task Assistant_Live_Turns_Real_Device_Off_After_Confirmation()
    {
        if (!ControlEnabled)
        {
            return;
        }

        var config = Config();
        var provider = CreateSwitchBot(config);
        var real = await FirstControllableAsync(provider);

        var before = await provider.GetStatusAsync(real.ExternalDeviceId);
        Assert.NotNull(before);

        using var db = await new TestDb().SeedAsync(AsDevice(real));
        var orchestrator = CreateOrchestrator(db, CreateAi(config), provider);

        try
        {
            var proposal = await orchestrator.HandleAsync(
                new AssistantRequest(db.HouseholdId, null, $"{real.Name}を消して", CommandSource.Web));
            Assert.True(proposal.AwaitingConfirmation);

            var executed = await orchestrator.HandleAsync(
                new AssistantRequest(db.HouseholdId, null, "はい", CommandSource.Web));

            Assert.True(executed.DeviceChanged);
            Assert.False(executed.AwaitingConfirmation);

            var command = Assert.Single(db.Context.DeviceCommands);
            Assert.Equal(DeviceAction.TurnOff, command.Action);
            Assert.Equal(CommandStatus.Succeeded, command.Status);

            // SwitchBot applies the command asynchronously; give the cloud a moment
            // before reading the state back.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var after = await provider.GetStatusAsync(real.ExternalDeviceId);
            Assert.NotNull(after);
            Assert.False(after!.IsOn);

            output.WriteLine($"[LIVE] target={real.Name} ({real.ExternalDeviceId})");
            output.WriteLine($"[LIVE] propose={proposal.Reply}");
            output.WriteLine($"[LIVE] execute={executed.Reply}");
            output.WriteLine($"[LIVE] power before={before.IsOn} after={after.IsOn}");
        }
        finally
        {
            if (before!.IsOn)
            {
                await provider.TurnOnAsync(real.ExternalDeviceId);
            }
        }
    }

    /// <summary>
    /// End-to-end proof of the "状況をまとめて伝える" journey with the real router:
    /// real device activity in the database -> a factual local-data answer -> a gentle,
    /// family-facing Japanese summary written by the model the router selected.
    ///
    /// Uses the local data service rather than Fabric on purpose: this asserts the
    /// summarisation path, and it must hold whether or not the Fabric Data Agent can
    /// currently reach its datasource (see Fabric_Live_Reports_What_The_Data_Agent_Actually_Answers).
    /// </summary>
    [Fact]
    public async Task Assistant_Live_Summarises_Real_Activity_For_The_Family()
    {
        if (!LiveEnabled)
        {
            return;
        }

        using var db = await new TestDb().SeedAsync(TestDb.Light());
        await SeedTodaysActivityAsync(db);

        var orchestrator = CreateOrchestrator(db, CreateAi(Config()), new MockDeviceProvider());

        var started = Stopwatch.StartNew();
        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日の様子をまとめて教えて", CommandSource.Web));
        started.Stop();

        output.WriteLine($"[LIVE] intent={response.Intent} model={response.ResolvedModel} router={response.Router} {started.ElapsedMilliseconds}ms");
        output.WriteLine($"[LIVE] summary={response.Reply}");

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));

        // The summary must be grounded in the seeded facts rather than claiming a
        // malfunction: the resident WAS active today, so a "取得できません" style
        // answer means the pipeline silently lost the data.
        Assert.DoesNotContain("不具合", response.Reply);
        Assert.DoesNotContain("取得できませんでした", response.Reply);
    }

    /// <summary>
    /// A quiet day must not be reported as a system failure.
    ///
    /// This is a real defect found during live testing: with no activity recorded yet,
    /// the local data service correctly answers "まだ家電の利用が記録されていません",
    /// and the model rewrote that as "システムの一時的な不具合により確認できませんでした".
    /// That is a fabrication and it would send a family chasing a non-existent fault,
    /// so the summary prompt now forbids it and this test holds the line against the
    /// real router.
    /// </summary>
    [Fact]
    public async Task Assistant_Live_Reports_A_Quiet_Day_Without_Inventing_A_Malfunction()
    {
        if (!LiveEnabled)
        {
            return;
        }

        // Seeded devices but deliberately no events: nothing has happened today.
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = CreateOrchestrator(db, CreateAi(Config()), new MockDeviceProvider());

        var started = Stopwatch.StartNew();
        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日の様子をまとめて教えて", CommandSource.Web));
        started.Stop();

        output.WriteLine($"[LIVE] quiet-day {started.ElapsedMilliseconds}ms model={response.ResolvedModel}");
        output.WriteLine($"[LIVE] quiet-day summary={response.Reply}");

        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
        Assert.DoesNotContain("不具合", response.Reply);
        Assert.DoesNotContain("故障", response.Reply);
        Assert.DoesNotContain("エラー", response.Reply);
    }

    /// <summary>
    /// The Fabric Data Agent apologises with HTTP 200 when it cannot reach its
    /// datasource, and no phrase list catches every wording a model might produce.
    /// When such an apology slips through as a "successful" answer, the summary must
    /// still report the facts the local database is certain about, and must not tell
    /// the family that nothing was recorded -- that contradicts the same sentence and
    /// would send them chasing a fault that does not exist.
    /// </summary>
    [Fact]
    public async Task Assistant_Live_Ignores_A_Data_Agent_Apology_That_Slipped_Through()
    {
        if (!LiveEnabled)
        {
            return;
        }

        using var db = await new TestDb().SeedAsync(TestDb.Light());
        await SeedTodaysActivityAsync(db);

        var orchestrator = new AssistantOrchestrator(
            db.Context,
            CreateAi(Config()),
            new MockDeviceProvider(),
            new ApologisingFabricClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System,
            new InMemoryPendingActionStore());

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日の様子をまとめて教えて", CommandSource.Web));

        output.WriteLine($"[LIVE] apology-passthrough summary={response.Reply}");

        // The seeded day starts at 07:00, so any claim of "no record" is wrong.
        Assert.DoesNotContain("記録がありません", response.Reply);
        Assert.DoesNotContain("記録がありませんでした", response.Reply);
        Assert.DoesNotContain("確認できませんでした", response.Reply);
        Assert.DoesNotContain("不具合", response.Reply);
    }

    /// <summary>
    /// Measures the summary against the budget the LINE webhook actually enforces,
    /// using the SAME wiring production uses -- the real Fabric client included.
    ///
    /// WebhookEndpoints caps processing of a single LINE event at 8 seconds
    /// (EventProcessingTimeout) and replies with a fixed "時間がかかっています" style
    /// message when that elapses. The summary is the demo's headline feature, so if it
    /// cannot finish inside that budget it is never actually seen over LINE -- the
    /// family only ever gets the timeout text. That failure is invisible from the web
    /// UI, which has no such cap, and from any test that stubs Fabric out.
    ///
    /// It asserts nothing about the wall-clock numbers on purpose: they depend on a
    /// third-party router and would make the suite flaky. The verdict lines are the
    /// deliverable.
    /// </summary>
    [Fact]
    public async Task Summary_Live_Reports_Whether_It_Fits_The_Line_Webhook_Budget()
    {
        if (!LiveEnabled)
        {
            return;
        }

        // Keep in step with WebhookEndpoints.EventProcessingTimeout.
        var budget = TimeSpan.FromSeconds(8);
        var config = Config();

        var fabricElapsed = await MeasureAsync(async () =>
        {
            var answer = await CreateFabric(config).AskAsync("今日の家電の利用状況を教えてください。");
            return answer.Success ? "accepted" : "rejected -> local fallback";
        });

        output.WriteLine($"[BUDGET] fabric alone, unbounded   {fabricElapsed.Elapsed,6:0}ms  ({fabricElapsed.Result})");

        foreach (var (label, source, useRealFabric) in new (string, CommandSource, bool)[]
        {
            ("LINE  + real fabric  ", CommandSource.Line, true),
            ("web   + real fabric  ", CommandSource.Web, true),
            ("LINE  + fabric off   ", CommandSource.Line, false)
        })
        {
            using var db = await new TestDb().SeedAsync(TestDb.Light());
            await SeedTodaysActivityAsync(db);

            IFabricDataAgentClient fabric = useRealFabric
                ? CreateFabric(config)
                : new MockFabricDataAgentClient();

            // No overrides: this measures exactly what appsettings/user-secrets ship,
            // so the numbers are the ones a demo will actually see.
            var orchestrator = new AssistantOrchestrator(
                db.Context,
                CreateAi(config),
                new MockDeviceProvider(),
                fabric,
                new LocalDataQuestionService(db.Context, TimeProvider.System),
                TimeProvider.System,
                new InMemoryPendingActionStore(),
                TimeSpan.FromSeconds(2));

            var run = await MeasureAsync(async () =>
            {
                var response = await orchestrator.HandleAsync(
                    new AssistantRequest(db.HouseholdId, null, "今日の様子をまとめて教えて", source));

                return response.ResolvedModel;
            });

            var verdict = run.Elapsed <= budget.TotalMilliseconds ? "FITS" : "OVER BUDGET -> LINE shows the timeout text";
            output.WriteLine($"[BUDGET] {label} {run.Elapsed,6:0}ms  model={run.Result}  {verdict}");
        }

        output.WriteLine($"[BUDGET] LINE allows {budget.TotalSeconds:0}s per event (WebhookEndpoints.EventProcessingTimeout).");
    }

    private static async Task<(long Elapsed, T Result)> MeasureAsync<T>(Func<Task<T>> work)
    {
        var started = Stopwatch.StartNew();
        var result = await work();
        started.Stop();

        return (started.ElapsedMilliseconds, result);
    }

    /// <summary>
    /// Reproduces the real failure mode: HTTP 200, Success = true, and an apology as
    /// the body, worded so it slips past the phrase filter.
    /// </summary>
    private sealed class ApologisingFabricClient : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new FabricAnswer(
                true,
                "申し訳ありません。ただいま集計システムの調子が優れず、詳細な内訳をご案内できかねます。",
                "Fabric",
                null));
    }

    /// <summary>
    /// Writes a plausible day of appliance activity so the summary has real facts to
    /// work from: morning, midday and evening usage, all today, local time.
    /// </summary>
    private static async Task SeedTodaysActivityAsync(TestDb db)
    {
        var device = db.Context.Devices.First();
        var todayLocal = HouseholdTime.LocalDate(DateTimeOffset.UtcNow);
        var startOfDayUtc = HouseholdTime.StartOfLocalDayUtc(todayLocal);

        foreach (var (hour, state) in new[] { (7, "on"), (12, "off"), (18, "on"), (21, "off") })
        {
            var occurredUtc = startOfDayUtc.AddHours(hour);

            db.Context.DeviceEvents.Add(new DeviceEvent
            {
                HouseholdId = db.HouseholdId,
                DeviceId = device.Id,
                EventType = "PowerState",
                State = state,
                Source = EventSource.Mock,
                OccurredAtUtc = occurredUtc,
                ReceivedAtUtc = occurredUtc
            });
        }

        await db.Context.SaveChangesAsync();
    }

    /// <summary>
    /// Picks a device this test is allowed to switch. Plugs/lights/fans only -- never
    /// a lock, curtain or camera, whatever happens to be in the account.
    /// </summary>
    private static async Task<ProviderDevice> FirstControllableAsync(SwitchBotDeviceProvider provider)
    {
        var devices = await provider.GetDevicesAsync();
        var device = devices.FirstOrDefault(d =>
            d.DeviceType is DeviceType.Plug or DeviceType.Light or DeviceType.Fan);

        Assert.True(device is not null, "No switchable SwitchBot device (plug/light/fan) is registered on this account.");
        return device!;
    }

    private static Device AsDevice(ProviderDevice real) => new()
    {
        ExternalDeviceId = real.ExternalDeviceId,
        Name = real.Name,
        Alias = real.Name,
        DeviceType = real.DeviceType,
        Room = real.Room,
        Provider = DeviceProviderKind.SwitchBot,
        RemoteControlAllowed = true,
        SafetyClass = DeviceSafetyPolicy.Classify(real.DeviceType)
    };

    private static AssistantOrchestrator CreateOrchestrator(TestDb db, IAiRouterClient ai, IDeviceProvider provider) =>
        new(db.Context,
            ai,
            provider,
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System,
            new InMemoryPendingActionStore());
}

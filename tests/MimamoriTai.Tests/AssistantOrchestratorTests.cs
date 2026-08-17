using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Tests;

public class AssistantOrchestratorTests
{
    private static AssistantOrchestrator Create(
        TestDb db,
        IAiRouterClient? ai = null,
        IFabricDataAgentClient? fabric = null,
        TimeSpan? fabricBudget = null,
        IGuardedActionNotifier? guardedNotifier = null)
    {
        var provider = new MockDeviceProvider();
        return new AssistantOrchestrator(
            db.Context,
            ai ?? new MockAiRouterClient(),
            provider,
            fabric ?? new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System,
            null,
            fabricBudget,
            guardedNotifier);
    }

    /// <summary>
    /// The change the family asked for: a heater is no longer a flat "cannot". It is a
    /// question about the room, and a yes to that question is what energises it.
    /// </summary>
    [Fact]
    public async Task Asking_For_The_Heater_Produces_A_Hazard_Question_Not_A_Refusal()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var orchestrator = Create(db);

        var proposal = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "ストーブつけて", CommandSource.Line));

        Assert.True(proposal.AwaitingConfirmation);
        Assert.False(proposal.DeviceChanged);

        Assert.Contains("燃えやすい", proposal.Reply);
        Assert.Contains("ご家族全員", proposal.Reply);
    }

    [Fact]
    public async Task Answering_The_Hazard_Question_Switches_The_Heater_On_And_Tells_Everyone()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var notifier = new RecordingGuardedNotifier();
        var orchestrator = Create(db, guardedNotifier: notifier);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "ストーブつけて", CommandSource.Line));

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "はい", CommandSource.Line));

        Assert.True(response.DeviceChanged);
        Assert.Contains("つけました", response.Reply);
        Assert.Single(notifier.Notices);
    }

    [Fact]
    public async Task Declining_The_Hazard_Question_Leaves_The_Heater_Alone()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var notifier = new RecordingGuardedNotifier();
        var orchestrator = Create(db, guardedNotifier: notifier);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "ストーブつけて", CommandSource.Line));

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "いいえ", CommandSource.Line));

        Assert.False(response.DeviceChanged);
        Assert.Empty(notifier.Notices);
        Assert.Empty(db.Context.DeviceEvents);
    }

    /// <summary>
    /// A device the owner marked "never switch on from away" is still refused outright,
    /// and is never softened into a question - otherwise the setting would mean nothing.
    /// </summary>
    [Fact]
    public async Task A_Device_Marked_No_Remote_TurnOn_Is_Never_Offered_As_A_Question()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Heater());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "ストーブつけて", CommandSource.Line));

        Assert.False(response.AwaitingConfirmation);
        Assert.False(response.DeviceChanged);
        Assert.Contains("遠隔でONにしない設定", response.Reply);
    }

    private sealed class RecordingGuardedNotifier : IGuardedActionNotifier
    {
        public List<GuardedActionNotice> Notices { get; } = [];

        public Task NotifyAsync(GuardedActionNotice notice, CancellationToken ct = default)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Natural_Language_Turns_A_Light_On()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        // State changes are proposed first, then executed on confirmation.
        var proposal = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "リビングのライトつけて", CommandSource.Web));

        Assert.Equal(AssistantIntent.ControlDevice, proposal.Intent);
        Assert.True(proposal.AwaitingConfirmation);
        Assert.False(proposal.DeviceChanged);
        Assert.Equal(MockAiRouterClient.MockModelName, proposal.ResolvedModel);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "はい", CommandSource.Web));

        Assert.True(response.DeviceChanged);
        Assert.Contains("つけました", response.Reply);
    }

    [Fact]
    public async Task Natural_Language_Turns_A_Light_Off()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, null, "リビングのライトつけて", CommandSource.Web));
        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, null, "はい", CommandSource.Web));

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, null, "リビングのライト消して", CommandSource.Web));
        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "はい", CommandSource.Web));

        Assert.True(response.DeviceChanged);
        Assert.Contains("消しました", response.Reply);
    }

    [Fact]
    public async Task Data_Question_Falls_Back_To_Local_Service_When_Fabric_Is_Unconfigured()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんどう？", CommandSource.Line));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    [Fact]
    public async Task Every_Turn_Is_Logged_As_AiRequestLog_And_FamilyMessage()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, db.ResidentId, "リビングのライトつけて", CommandSource.Web));

        Assert.NotEmpty(db.Context.AiRequestLogs);
        // one user message + one AI reply
        Assert.Equal(2, db.Context.FamilyMessages.Count());
    }

    [Fact]
    public async Task Unparsable_Model_Output_Never_Executes_Anything()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var broken = new BrokenAiRouterClient();
        var orchestrator = Create(db, broken);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "リビングのライトつけて", CommandSource.Web));

        Assert.False(response.DeviceChanged);
        Assert.Empty(db.Context.DeviceCommands);
        // exactly one repair attempt, then give up
        Assert.Equal(2, broken.CallCount);
    }

    private sealed class BrokenAiRouterClient : IAiRouterClient
    {
        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public string DisplayName => "BrokenRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new AiCompletionResult(true, "申し訳ありません、よく分かりません。", DisplayName, "broken/model", 1));
        }
    }
}

/// <summary>
/// Covers the guardrails around LLM-issued device control: a state change is always
/// proposed before it happens, consent has to be explicit, and the assistant cannot
/// keep cycling the home even if the model insists.
/// </summary>
public class DeviceConfirmationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static AssistantOrchestrator Create(TestDb db, TimeProvider? clock = null, IPendingActionStore? store = null) =>
        new(db.Context,
            new MockAiRouterClient(),
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, clock ?? TimeProvider.System),
            clock ?? TimeProvider.System,
            store ?? new InMemoryPendingActionStore());

    private static AssistantRequest Say(TestDb db, string message) =>
        new(db.HouseholdId, null, message, CommandSource.Web);

    [Fact]
    public async Task Turning_a_light_off_asks_before_doing_it()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var proposal = await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));

        Assert.True(proposal.AwaitingConfirmation);
        Assert.False(proposal.DeviceChanged);
        Assert.Contains("よろしいですか", proposal.Reply, StringComparison.Ordinal);

        // Nothing has been sent to the device yet.
        Assert.Empty(db.Context.DeviceCommands);

        output.WriteLine("proposal: " + proposal.Reply);
    }

    [Fact]
    public async Task Saying_no_cancels_without_touching_the_device()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));
        var response = await orchestrator.HandleAsync(Say(db, "やめて"));

        Assert.False(response.DeviceChanged);
        Assert.Contains("中止", response.Reply, StringComparison.Ordinal);
        Assert.Empty(db.Context.DeviceCommands);

        output.WriteLine("cancelled: " + response.Reply);
    }

    [Fact]
    public async Task A_confirmation_can_only_be_used_once()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));
        var first = await orchestrator.HandleAsync(Say(db, "はい"));
        var second = await orchestrator.HandleAsync(Say(db, "はい"));

        Assert.True(first.DeviceChanged);

        // The second "はい" has nothing pending, so it must not repeat the command.
        Assert.False(second.DeviceChanged);
        Assert.Single(db.Context.DeviceCommands.Where(c => c.Status == CommandStatus.Succeeded));
    }

    [Fact]
    public async Task An_unanswered_proposal_expires_rather_than_lingering()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orchestrator = Create(db, clock);

        await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));

        clock.Advance(InMemoryPendingActionStore.Lifetime + TimeSpan.FromMinutes(1));

        var response = await orchestrator.HandleAsync(Say(db, "はい"));

        Assert.False(response.DeviceChanged);
    }

    [Fact]
    public async Task Asking_for_status_never_needs_confirmation()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(Say(db, "リビングのライトはついてる？"));

        Assert.False(response.AwaitingConfirmation);
        Assert.Equal(AssistantIntent.DeviceStatus, response.Intent);
    }

    [Fact]
    public async Task Repeating_the_same_change_is_throttled()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orchestrator = Create(db, clock);

        for (var i = 0; i < DeviceSafetyPolicy.MaxIdenticalRepeats; i++)
        {
            await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));
            var ok = await orchestrator.HandleAsync(Say(db, "はい"));
            Assert.True(ok.DeviceChanged, $"repeat {i + 1} should have executed");
        }

        await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));
        var blocked = await orchestrator.HandleAsync(Say(db, "はい"));

        Assert.False(blocked.DeviceChanged);
        Assert.Contains("繰り返され", blocked.Reply, StringComparison.Ordinal);

        output.WriteLine("throttled: " + blocked.Reply);
    }

    [Fact]
    public async Task The_household_cannot_exceed_the_command_ceiling_in_the_window()
    {
        // Distinct devices so the per-device repeat guard never fires and the test
        // measures only the household-wide ceiling.
        var lights = MakeLights(DeviceSafetyPolicy.MaxStateChangesPerWindow + 3);
        using var db = await new TestDb().SeedAsync(lights);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var control = new DeviceControlService(db.Context, new MockDeviceProvider(), clock);

        var executed = 0;
        for (var i = 0; i < lights.Length; i++)
        {
            var outcome = await control.ExecuteAsync(
                db.HouseholdId, lights[i].Name, DeviceAction.TurnOn,
                1.0, "test", CommandSource.Web, null, "test/model");

            if (outcome.Executed)
            {
                executed++;
            }

            clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(DeviceSafetyPolicy.MaxStateChangesPerWindow, executed);
    }

    [Fact]
    public async Task The_ceiling_lifts_once_the_window_has_passed()
    {
        var lights = MakeLights(DeviceSafetyPolicy.MaxStateChangesPerWindow + 1);
        using var db = await new TestDb().SeedAsync(lights);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var control = new DeviceControlService(db.Context, new MockDeviceProvider(), clock);

        for (var i = 0; i < DeviceSafetyPolicy.MaxStateChangesPerWindow; i++)
        {
            await control.ExecuteAsync(
                db.HouseholdId, lights[i].Name, DeviceAction.TurnOn,
                1.0, "test", CommandSource.Web, null, "test/model");
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var last = lights[^1].Name;

        var blocked = await control.ExecuteAsync(
            db.HouseholdId, last, DeviceAction.TurnOn, 1.0, "test", CommandSource.Web, null, "test/model");
        Assert.False(blocked.Executed);

        clock.Advance(DeviceSafetyPolicy.RateLimitWindow + TimeSpan.FromMinutes(1));

        var allowed = await control.ExecuteAsync(
            db.HouseholdId, last, DeviceAction.TurnOn, 1.0, "test", CommandSource.Web, null, "test/model");
        Assert.True(allowed.Executed);
    }

    [Fact]
    public async Task Reading_status_is_never_rate_limited()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var control = new DeviceControlService(db.Context, new MockDeviceProvider(), clock);

        for (var i = 0; i < DeviceSafetyPolicy.MaxStateChangesPerWindow + 5; i++)
        {
            var outcome = await control.ExecuteAsync(
                db.HouseholdId, "リビング照明", DeviceAction.GetStatus,
                1.0, "test", CommandSource.Web, null, "test/model");

            Assert.True(outcome.Executed);
        }
    }

    private static Device[] MakeLights(int count) =>
        [.. Enumerable.Range(0, count).Select(i => TestDb.Light($"light-{i}", $"照明{i}"))];

    [Theory]
    [InlineData("はい", true)]
    [InlineData("うん", true)]
    [InlineData("OK", true)]
    [InlineData("お願いします", true)]
    [InlineData("いいえ", false)]
    [InlineData("やめて", false)]
    [InlineData("キャンセル", false)]
    [InlineData("はい、やめて", false)]
    [InlineData("リビングのライトつけて", null)]
    [InlineData("今日のお母さんどう？", null)]
    [InlineData("", null)]
    public void Confirmation_replies_are_interpreted_conservatively(string message, bool? expected)
    {
        Assert.Equal(expected, ConfirmationReply.Interpret(message));
    }

    [Fact]
    public async Task A_new_instruction_does_not_confirm_a_pending_one()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(Say(db, "リビングのライト消して"));

        // Not a yes/no, so it must be treated as a fresh request, not as consent.
        var response = await orchestrator.HandleAsync(Say(db, "今日のお母さんどう？"));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(response.DeviceChanged);
        Assert.Empty(db.Context.DeviceCommands);
    }
}

/// <summary>
/// Covers the "summarise the situation for the family" path: a data question must
/// reach the LLM with the retrieved facts, and must still answer correctly when the
/// router is down, throttled or returns nothing.
/// </summary>
public class AssistantSummaryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// Answers intent JSON like the mock, but returns a scripted summary for the
    /// summarisation call.
    /// </summary>
    /// <remarks>
    /// Matches every summary purpose, not just "summary": LINE requests use
    /// "summary-fast", so an exact match quietly delegated them to the working
    /// mock and a test named for a router failure exercised no failure at all.
    /// </remarks>
    private sealed class ScriptedSummaryRouter(
        string? summary, bool success = true, string model = "openai/gpt-4.1-mini") : IAiRouterClient
    {
        private readonly MockAiRouterClient _intent = new();

        public List<IReadOnlyList<AiMessage>> SummaryPrompts { get; } = [];

        public bool IsConfigured => true;

        public string DisplayName => "ScriptedRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            if (!purpose.StartsWith("summary", StringComparison.Ordinal))
            {
                return _intent.CompleteAsync(messages, purpose, jsonMode, ct);
            }

            SummaryPrompts.Add(messages);

            return Task.FromResult(success
                ? new AiCompletionResult(true, summary ?? string.Empty, DisplayName, model, 12)
                : new AiCompletionResult(false, string.Empty, DisplayName, model, 12, "Azure Model Router returned 429."));
        }
    }

    private static AssistantOrchestrator Create(TestDb db, IAiRouterClient ai) =>
        new(db.Context,
            ai,
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);

    [Fact]
    public async Task Data_question_is_rewritten_by_the_llm_for_the_family()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var router = new ScriptedSummaryRouter("お母さんは今朝もいつも通り起きて、日中も過ごされています。今日は少しゆっくりのようなので、夕方に一度お電話してみると安心かもしれません。");
        var orchestrator = Create(db, router);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんの様子はどう？", CommandSource.Web));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.Contains("お母さん", response.Reply, StringComparison.Ordinal);
        Assert.Equal("openai/gpt-4.1-mini", response.ResolvedModel);

        // The LLM must receive the retrieved facts, not just the question.
        var prompt = Assert.Single(router.SummaryPrompts);
        Assert.Equal("system", prompt[0].Role);
        Assert.Contains("見守り隊", prompt[0].Content, StringComparison.Ordinal);
        Assert.Contains("今日のお母さんの様子はどう？", prompt[1].Content, StringComparison.Ordinal);
        Assert.Contains("データ(", prompt[1].Content, StringComparison.Ordinal);

        output.WriteLine("--- summary system prompt ---");
        output.WriteLine(prompt[0].Content);
        output.WriteLine("--- summary user prompt ---");
        output.WriteLine(prompt[1].Content);
        output.WriteLine("--- reply ---");
        output.WriteLine(response.Reply);
    }

    [Fact]
    public async Task Router_failure_falls_back_to_the_raw_facts_instead_of_an_error()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var router = new ScriptedSummaryRouter(null, success: false);
        var orchestrator = Create(db, router);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんの様子はどう？", CommandSource.Line));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
        Assert.DoesNotContain("429", response.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("error", response.Reply, StringComparison.OrdinalIgnoreCase);

        // The reason must not reach the family, but it must reach the log --
        // otherwise a failure is only ever a count with no way to act on it.
        var logs = db.Context.AiRequestLogs.ToList();
        output.WriteLine("logs: " + string.Join(
            " | ", logs.Select(l => $"{l.Purpose}/{l.Success}/{l.Error ?? "-"}")));

        var failed = Assert.Single(logs, l => !l.Success);
        Assert.Equal("Azure Model Router returned 429.", failed.Error);
        Assert.All(logs.Where(l => l.Success), l => Assert.Null(l.Error));

        output.WriteLine("router-down reply: " + response.Reply);
    }

    [Fact]
    public async Task Empty_model_output_never_replaces_the_facts()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new ScriptedSummaryRouter("   "));

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんの様子はどう？", CommandSource.Web));

        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    [Fact]
    public async Task Summary_call_is_recorded_in_the_ai_request_log()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new ScriptedSummaryRouter("お母さんは落ち着いて過ごされています。"));

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんの様子はどう？", CommandSource.Web));

        Assert.Contains(db.Context.AiRequestLogs, l => l.Purpose == "summary");
    }

    /// <summary>
    /// Without a key the app still has to answer. The mock must not invent numbers,
    /// so the facts it was given have to survive into the reply.
    /// </summary>
    [Fact]
    public async Task Mock_router_summary_preserves_the_underlying_facts()
    {
        var ai = new MockAiRouterClient();

        var result = await ai.CompleteAsync(
            [
                AiMessage.System("要約してください。"),
                AiMessage.User("ご家族からの質問: 今日どう？\n\nデータ(LocalDatabase):\nお母さんは今朝06:41頃から活動を始め、これまでに家電を2回利用しています。")
            ],
            "summary");

        Assert.True(result.Success);
        Assert.Contains("06:41", result.Content, StringComparison.Ordinal);
        output.WriteLine("mock summary: " + result.Content);
    }
}

public class MockIntegrationTests
{
    [Fact]
    public async Task MockDeviceProvider_Turns_Devices_On_And_Off()
    {
        var provider = new MockDeviceProvider();
        var id = MockDeviceProvider.SeedDevices[0].ExternalDeviceId;

        Assert.True((await provider.TurnOnAsync(id)).Success);
        Assert.True((await provider.GetStatusAsync(id))!.IsOn);

        Assert.True((await provider.TurnOffAsync(id)).Success);
        Assert.False((await provider.GetStatusAsync(id))!.IsOn);
    }

    [Fact]
    public async Task MockDeviceProvider_Rejects_Unknown_Device()
    {
        var provider = new MockDeviceProvider();
        Assert.False((await provider.TurnOnAsync("no-such-device")).Success);
        Assert.Null(await provider.GetStatusAsync("no-such-device"));
    }

    /// <summary>
    /// The demo seeder builds Device rows from <see cref="MockDeviceProvider.SeedDevices"/>.
    /// If the two ever diverge, commands are accepted by the safety policy and then fail at
    /// the provider with "unknown device", which silently breaks the demo.
    /// </summary>
    [Fact]
    public async Task MockDeviceProvider_Knows_Every_Seed_Device()
    {
        var provider = new MockDeviceProvider();

        foreach (var device in MockDeviceProvider.SeedDevices)
        {
            Assert.NotNull(await provider.GetStatusAsync(device.ExternalDeviceId));
            Assert.True(MockDeviceProvider.SeedAliases.ContainsKey(device.ExternalDeviceId));
        }
    }

    /// <summary>
    /// The headline safety demo requires at least one appliance that cannot simply be
    /// switched on. Guarded is the interesting case now: it is the one that produces a
    /// hazard question instead of a flat refusal.
    /// </summary>
    [Fact]
    public void SeedDevices_Contain_An_Appliance_That_Needs_A_Hazard_Check()
    {
        Assert.Contains(
            MockDeviceProvider.SeedDevices,
            d => DeviceSafetyPolicy.Classify(d.DeviceType) == SafetyClass.Guarded);
    }

    [Fact]
    public async Task MockFabric_Reports_Unconfigured()
    {
        var fabric = new MockFabricDataAgentClient();
        Assert.False(fabric.IsConfigured);
        Assert.False((await fabric.AskAsync("今日どう？")).Success);
    }

    [Fact]
    public async Task MockAiRouter_Emits_Parsable_Intent_Json()
    {
        var ai = new MockAiRouterClient();
        var result = await ai.CompleteAsync(
            [AiMessage.User("リビングのライトつけて")], "intent", jsonMode: true);

        var plan = IntentParser.TryParse(result.Content);
        Assert.NotNull(plan);
        Assert.Equal(AssistantIntent.ControlDevice, plan!.Intent);
        Assert.Equal(DeviceAction.TurnOn, plan.Action);
        Assert.True(plan.Confidence >= IntentParser.MinimumConfidence);
    }

    [Fact]
    public async Task MockAiRouter_Uses_Low_Confidence_When_Action_Is_Unclear()
    {
        var ai = new MockAiRouterClient();
        var result = await ai.CompleteAsync(
            [AiMessage.User("リビングのライト")], "intent", jsonMode: true);

        var plan = IntentParser.TryParse(result.Content);
        Assert.NotNull(plan);
        Assert.True(plan!.Confidence < IntentParser.MinimumConfidence);
    }
}

public class LineSignatureTests
{
    private const string Secret = "test-channel-secret";

    private static string Sign(string body, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Valid_Signature_Is_Accepted()
    {
        const string body = """{"events":[]}""";
        Assert.True(LineSignature.Verify(Secret, body, Sign(body, Secret)));
    }

    [Fact]
    public void Signature_From_A_Different_Secret_Is_Rejected()
    {
        const string body = """{"events":[]}""";
        Assert.False(LineSignature.Verify(Secret, body, Sign(body, "attacker-secret")));
    }

    [Fact]
    public void Tampered_Body_Is_Rejected()
    {
        const string body = """{"events":[]}""";
        var signature = Sign(body, Secret);
        Assert.False(LineSignature.Verify(Secret, """{"events":[{"evil":true}]}""", signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    public void Missing_Or_Malformed_Signature_Is_Rejected(string? signature)
    {
        Assert.False(LineSignature.Verify(Secret, "{}", signature));
    }

    [Fact]
    public void Missing_Channel_Secret_Rejects_Everything()
    {
        Assert.False(LineSignature.Verify(null, "{}", Sign("{}", Secret)));
    }
    [Fact]
    public void MockLineClient_Still_Verifies_A_Configured_Secret()
    {
        var client = new MockLineMessagingClient(
            Options.Create(new LineOptions { ChannelSecret = Secret }));

        const string body = """{"events":[]}""";
        Assert.True(client.VerifySignature(body, Sign(body, Secret)));
        Assert.False(client.VerifySignature(body, Sign(body, "wrong")));
    }
}

/// <summary>
/// The Fabric Data Agent must never be able to take down an answer the local
/// database has already produced.
///
/// Measured against the live workspace a single AskAsync takes ~19s and is then
/// rejected anyway, while the LINE webhook cancels the whole event after 8s. Before
/// this was bounded, a slow data agent turned a perfectly good local answer into the
/// generic "時間がかかっています" text -- the family lost real information because an
/// optional enrichment was slow.
/// </summary>
public class FabricBudgetTests
{
    private sealed class SlowFabricClient(TimeSpan delay) : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public async Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return new FabricAnswer(true, "Fabric からの詳細な内訳です。", "Fabric", null);
        }
    }

    private sealed class ThrowingFabricClient : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
            throw new HttpRequestException("data agent unreachable");
    }

    private static AssistantOrchestrator Create(TestDb db, IFabricDataAgentClient fabric, TimeSpan budget) =>
        new(db.Context,
            new MockAiRouterClient(),
            new MockDeviceProvider(),
            fabric,
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System,
            null,
            budget);

    [Fact]
    public async Task A_slow_data_agent_falls_back_to_local_facts_within_the_budget()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new SlowFabricClient(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(200));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "先週と比べて様子はどうですか", CommandSource.Line));
        started.Stop();

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));

        // The slow agent's text must not appear: it never finished.
        Assert.DoesNotContain("Fabric からの詳細な内訳です。", response.Reply, StringComparison.Ordinal);

        // Well inside the 8s the LINE webhook allows for an entire event.
        Assert.True(started.ElapsedMilliseconds < 5000, $"took {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task A_throwing_data_agent_still_answers_from_local_facts()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new ThrowingFabricClient(), TimeSpan.FromSeconds(4));

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "先週と比べて様子はどうですか", CommandSource.Line));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    [Fact]
    public async Task A_data_agent_that_answers_in_time_is_still_used()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new SlowFabricClient(TimeSpan.Zero), TimeSpan.FromSeconds(4));

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "先週と比べて様子はどうですか", CommandSource.Line));

        // MockAiRouterClient echoes the facts it is given, so the Fabric text survives.
        Assert.Contains("Fabric からの詳細な内訳です。", response.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation by the caller means the caller has abandoned the whole request,
    /// not just the optional enrichment, so it must not be swallowed.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new SlowFabricClient(TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "先週と比べて様子はどうですか", CommandSource.Line), cts.Token));
    }
}

/// <summary>
/// The data agent is only worth its latency for questions the local database cannot
/// answer well. Asking it "how is she today?" spends seconds to add nothing, so the
/// scope decided during intent classification gates the call.
/// </summary>
public class FabricScopeTests
{
    private sealed class RecordingFabricClient : IFabricDataAgentClient
    {
        public int Calls { get; private set; }

        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new FabricAnswer(true, "Fabric からの詳細な内訳です。", "Fabric", null));
        }
    }

    private static AssistantOrchestrator Create(TestDb db, IFabricDataAgentClient fabric) =>
        new(db.Context,
            new MockAiRouterClient(),
            new MockDeviceProvider(),
            fabric,
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);

    [Theory]
    [InlineData("今日の様子を教えて")]
    [InlineData("今どうしてる?")]
    [InlineData("昨日は何時に起きた?")]
    public async Task Questions_about_the_current_state_do_not_call_the_data_agent(string message)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var fabric = new RecordingFabricClient();
        var orchestrator = Create(db, fabric);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, message, CommandSource.Line));

        Assert.Equal(0, fabric.Calls);

        // Skipping the agent must not degrade the answer: the local facts still reply.
        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    [Theory]
    [InlineData("先週と比べて活動はどうですか")]
    [InlineData("今月の平均は何時に起きていますか")]
    [InlineData("最近、夜中の活動は増えていますか")]
    public async Task Analytical_questions_still_reach_the_data_agent(string message)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var fabric = new RecordingFabricClient();
        var orchestrator = Create(db, fabric);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, message, CommandSource.Line));

        Assert.Equal(1, fabric.Calls);
    }

    /// <summary>
    /// A model that omits the field, or invents a value for it, must not silently turn
    /// every question into a paid round trip to the data agent.
    /// </summary>
    [Theory]
    [InlineData("""{"intent":"query_data","confidence":0.9,"question":"x"}""")]
    [InlineData("""{"intent":"query_data","scope":"weekly","confidence":0.9,"question":"x"}""")]
    [InlineData("""{"intent":"query_data","scope":null,"confidence":0.9,"question":"x"}""")]
    public void An_unusable_scope_falls_back_to_the_local_only_path(string json)
    {
        var plan = IntentParser.TryParse(json);

        Assert.NotNull(plan);
        Assert.Equal(QueryScope.Recent, plan!.Scope);
    }

    [Fact]
    public void An_analysis_scope_is_parsed()
    {
        var plan = IntentParser.TryParse(
            """{"intent":"query_data","scope":"analysis","confidence":0.9,"question":"x"}""");

        Assert.Equal(QueryScope.Analysis, plan!.Scope);
    }
}

/// <summary>
/// LINE cancels an event after 8 seconds; the web UI has no such limit and shows the
/// resolved model name, which is the visible evidence that the model router routed the
/// request. So the deadline-bound budget is applied per entry point, not globally.
/// </summary>
public class SummaryRoutingTests
{
    private sealed class RecordingAiClient : IAiRouterClient
    {
        private readonly MockAiRouterClient _inner = new();

        public List<string> Purposes { get; } = [];

        public bool IsConfigured => true;

        public string DisplayName => "Recording";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            Purposes.Add(purpose);
            return _inner.CompleteAsync(messages, purpose, jsonMode, ct);
        }
    }

    private static async Task<List<string>> PurposesForAsync(CommandSource source)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var ai = new RecordingAiClient();

        var orchestrator = new AssistantOrchestrator(
            db.Context,
            ai,
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, null, "今日の様子を教えて", source));
        return ai.Purposes;
    }

    [Fact]
    public async Task Line_asks_for_the_deadline_bound_model()
    {
        var purposes = await PurposesForAsync(CommandSource.Line);

        Assert.Contains("summary-fast", purposes);
        Assert.EndsWith(AzureModelRouterOptions.FastSuffix, "summary-fast", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CommandSource.Web)]
    [InlineData(CommandSource.System)]
    public async Task Other_entry_points_keep_the_auto_router(CommandSource source)
    {
        var purposes = await PurposesForAsync(source);

        Assert.Contains("summary", purposes);
        Assert.DoesNotContain("summary-fast", purposes);
    }
    /// <summary>
    /// The number in the reply is the one thing a family acts on. A smaller summarising
    /// model does turn "1回" into "4回", so an invented figure must cost the family the
    /// prettier wording rather than the truth.
    /// </summary>
    [Theory]
    [InlineData("今日は1回、家電を利用しています。", "家電を4回使われました。", true)]
    [InlineData("今日は1回、家電を利用しています。", "家電を1回使われました。", false)]
    [InlineData("活動は14:45頃から始まりました。", "45回ほど動かれました。", false)]
    [InlineData("活動は14:45頃から始まりました。", "14時45分ごろから動き始めました。", false)]
    [InlineData("活動は14:45頃から始まりました。", "16時ごろから動き始めました。", true)]
    [InlineData("通電時間は11.5時間でした。", "およそ11.5時間つけっぱなしでした。", false)]
    [InlineData("消費電力は32.7ワットでした。", "およそ33ワットでした。", false)]
    [InlineData("今日は家電の利用がありません。", "落ち着いて過ごされています。", false)]
    public void InventsNumbers_Rejects_Figures_The_Data_Never_Contained(
        string facts, string summary, bool expected)
    {
        Assert.Equal(expected, AssistantOrchestrator.InventsNumbers(facts, summary));
    }
}
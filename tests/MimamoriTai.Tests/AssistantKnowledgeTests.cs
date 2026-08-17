using System.Diagnostics;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers the questions an elderly resident actually sends to the LINE bot.
///
/// Two failures are being guarded against, both observed in production:
/// the 8s webhook budget being blown by the auto router (every question came back as
/// "しばらくたってからお試しください"), and the assistant having no product knowledge to
/// answer with even when it did reply in time.
/// </summary>
public class AssistantKnowledgeTests
{
    /// <summary>The LINE webhook cancels an event after this long (WebhookEndpoints).</summary>
    private static readonly TimeSpan LineBudget = TimeSpan.FromSeconds(8);

    private static AssistantOrchestrator Create(TestDb db, IAiRouterClient? ai = null) =>
        new(
            db.Context,
            ai ?? new MockAiRouterClient(),
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);

    /// <summary>
    /// The same question in kanji and in kana must reach the same layer. 「何が出来るの？」
    /// used to miss every keyword and fall through to the model while 「何ができるの？」
    /// was answered from the knowledge base, so two people asking the identical thing
    /// got differently worded answers.
    /// </summary>
    [Theory]
    [InlineData("何ができるの？")]
    [InlineData("何が出来るの？")]
    [InlineData("なにが出来ますか")]
    [InlineData("使い方を教えて")]
    [InlineData("使いかたを教えて")]
    public void Spelling_Variants_Reach_The_Same_Answer(string question)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(question);

        Assert.NotNull(answer);
        Assert.Equal("what-is-this", answer!.Id);
    }

    [Theory]
    [InlineData("家族の追加方法は")]
    [InlineData("家族を追加したいのですが")]
    [InlineData("かぞくの　追加　ほうほう")]
    [InlineData("息子を追加するには？")]
    [InlineData("連携のやり方を教えて")]
    public async Task Asking_How_To_Add_Family_Returns_The_Real_Procedure(string question)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line));

        // The steps must match the screens that exist: Home.razor offers 「家族の追加」,
        // SwitchBotSettings.razor issues the code, WebhookEndpoints reads it back as "連携 123456".
        Assert.Contains("家族の追加", response.Reply);
        Assert.Contains("連携コードを発行する", response.Reply);
        Assert.Contains("連携 123456", response.Reply);
    }

    [Fact]
    public async Task Common_Questions_Are_Answered_Well_Inside_The_Line_Budget()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        string[] questions =
        [
            "家族の追加方法は",
            "通知が来ない",
            "カメラで撮られてる？",
            "見張られているみたいで嫌だ",
            "個人情報は大丈夫ですか",
            "文字を大きくしたい",
            "音が鳴らない",
            "機器の追加はどうやるの",
            "間違えて押したらどうなる",
            "このLINEは何をしてくれるの"
        ];

        foreach (var question in questions)
        {
            // A token with the real budget: an answer that needs longer throws instead of
            // silently passing.
            using var cts = new CancellationTokenSource(LineBudget);
            var started = Stopwatch.GetTimestamp();

            var response = await orchestrator.HandleAsync(
                new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line), cts.Token);

            Assert.True(
                Stopwatch.GetElapsedTime(started) < LineBudget,
                $"'{question}' exceeded the {LineBudget.TotalSeconds}s LINE budget.");

            Assert.False(string.IsNullOrWhiteSpace(response.Reply));
            // The exact production symptom: every question came back as the timeout message.
            Assert.DoesNotContain("処理に時間がかかっている", response.Reply);
            Assert.DoesNotContain("少し待ってから", response.Reply);
            // Small talk boilerplate is not an answer to a question.
            Assert.DoesNotContain("承知しました。家族にも共有しておきますね。", response.Reply);
        }
    }

    [Theory]
    [InlineData("カメラで撮られてる？")]
    [InlineData("録音されてるの")]
    [InlineData("部屋が映像で見られていませんか")]
    [InlineData("写真は残るのですか")]
    public async Task Camera_Worry_Is_Answered_With_What_The_Product_Actually_Records(string question)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line));

        // Grounded in the implementation: SwitchBot open/close, motion and power events only.
        Assert.Contains("カメラはありません", response.Reply);
        Assert.Contains("記録していません", response.Reply);
    }

    /// <summary>
    /// 「写真」「映像」という語が出ただけでカメラの説明を始めないこと。
    ///
    /// 心配していない人に「カメラはありません。盗撮していません」と返すのは、
    /// こちらから盗撮の可能性を持ち出すことになり、安心させるどころか不安を作ります。
    /// 撮られる・残る・見られるという心配と組んだときだけ答えます。
    /// </summary>
    [Theory]
    [InlineData("孫の写真が届いてうれしかった")]
    [InlineData("テレビの映像が乱れるんです")]
    public async Task Merely_Mentioning_Photos_Does_Not_Trigger_The_Camera_Answer(string message)
    {
        Assert.Null(AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict));
        Assert.Null(AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Loose));
    }

    [Fact]
    public async Task Feeling_Watched_Is_Acknowledged_Not_Dismissed()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "見張られているみたいで嫌だ", CommandSource.Line));

        Assert.Contains("見張るためのものではありません", response.Reply);
        Assert.Contains("映像はなく", response.Reply);
    }

    [Theory]
    [InlineData("胸が痛い")]
    [InlineData("息苦しいです")]
    [InlineData("転んで動けない")]
    public async Task Urgent_Symptoms_Reach_The_Emergency_Route_Not_Small_Talk(string message)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, message, CommandSource.Line));

        Assert.Contains("119", response.Reply);
        Assert.Contains("助けて", response.Reply);
    }

    [Fact]
    public async Task Urgent_Symptoms_Are_Answered_Without_Waiting_For_Any_Model()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var counting = new CountingAiRouterClient();
        var orchestrator = Create(db, counting);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "胸が痛い", CommandSource.Line));

        Assert.Equal(0, counting.CallCount);
    }

    [Fact]
    public async Task Product_Questions_Cost_No_Model_Call_At_All()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var counting = new CountingAiRouterClient();
        var orchestrator = Create(db, counting);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "家族の追加方法は", CommandSource.Line));

        Assert.Equal(0, counting.CallCount);
        Assert.Equal(AssistantOrchestrator.KnowledgeBaseRouter, response.Router);
        Assert.Equal(AssistantOrchestrator.KnowledgeBaseModel, response.ResolvedModel);
    }

    [Fact]
    public async Task Questions_Are_Still_Answered_When_The_Router_Is_Down()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new DeadAiRouterClient());

        // Reaches the knowledge base before any model call.
        var howTo = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "通知が来ない", CommandSource.Line));
        Assert.Contains("ブロック", howTo.Reply);

        // Reaches it through the unparsable-plan fallback instead.
        var lonely = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "さみしい", CommandSource.Line));
        Assert.Contains("家族に連絡", lonely.Reply);
    }

    [Fact]
    public async Task A_Knowledge_Base_Answer_Is_Recorded_Like_Any_Other_Reply()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "家族の追加方法は", CommandSource.Line));

        // The resident's question and the answer both belong in the family history.
        Assert.Equal(2, db.Context.FamilyMessages.Count());
    }

    [Fact]
    public async Task Small_Talk_From_Line_Uses_The_Fast_Model_But_The_Web_Keeps_The_Auto_Router()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var recorder = new RecordingAiRouterClient();
        var orchestrator = Create(db, recorder);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "ありがとう", CommandSource.Line));
        Assert.Contains("conversation-fast", recorder.Purposes);

        recorder.Purposes.Clear();

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "ありがとう", CommandSource.Web));
        Assert.Contains("conversation", recorder.Purposes);
        Assert.DoesNotContain("conversation-fast", recorder.Purposes);
    }

    [Fact]
    public void The_Fast_Purpose_Actually_Resolves_To_The_Shorter_Budget()
    {
        var options = new AzureModelRouterOptions();

        Assert.Equal(TimeSpan.FromSeconds(options.FastTimeoutSeconds), options.ResolveTimeout("conversation-fast"));
        Assert.Equal(TimeSpan.FromSeconds(options.TimeoutSeconds), options.ResolveTimeout("conversation"));
    }

    [Fact]
    public async Task Device_Commands_And_Data_Questions_Are_Never_Hijacked_By_The_Knowledge_Base()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var control = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "リビングのライトつけて", CommandSource.Line));
        Assert.Equal(AssistantIntent.ControlDevice, control.Intent);

        var query = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日の様子はどう？", CommandSource.Line));
        Assert.Equal(AssistantIntent.QueryData, query.Intent);
    }

    [Theory]
    [InlineData("家族の追加方法は")]
    [InlineData("通知が来ない")]
    [InlineData("カメラで撮られてる？")]
    [InlineData("さみしい")]
    [InlineData("使い方")]
    public void Every_Answer_Stays_Readable_In_A_Single_Line_Bubble(string question)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(question, FaqMatchMode.Loose);

        Assert.NotNull(answer);
        Assert.True(answer!.Reply.Length <= 200, $"'{question}' answer is too long for LINE.");

        // "箇条書きは3項目まで": numbered steps never run past 3.
        Assert.DoesNotContain("4.", answer.Reply);
    }

    [Fact]
    public void Nothing_Is_Answered_When_Nothing_Is_Known()
    {
        Assert.Null(AssistantKnowledgeBase.TryAnswer("明日の天気は", FaqMatchMode.Loose));
        Assert.Null(AssistantKnowledgeBase.TryAnswer("", FaqMatchMode.Loose));
        Assert.Null(AssistantKnowledgeBase.TryAnswer(null, FaqMatchMode.Loose));
    }

    [Fact]
    public void Only_Unambiguous_Wording_Answers_Before_Intent_Classification()
    {
        // Product help is safe to answer immediately.
        Assert.NotNull(AssistantKnowledgeBase.TryAnswer("家族の追加方法は", FaqMatchMode.Strict));

        // "痛い" could equally be a family member asking about the records, so it waits for
        // the intent model and is only answered once that says small talk.
        Assert.Null(AssistantKnowledgeBase.TryAnswer("痛い", FaqMatchMode.Strict));
        Assert.NotNull(AssistantKnowledgeBase.TryAnswer("痛い", FaqMatchMode.Loose));
    }

    [Fact]
    public void Urgency_Detection_Does_Not_Fire_On_Ordinary_Messages()
    {
        Assert.True(AssistantKnowledgeBase.IsUrgent("胸が痛い"));
        Assert.True(AssistantKnowledgeBase.IsUrgent("たすけて"));

        Assert.False(AssistantKnowledgeBase.IsUrgent("家族の追加方法は"));
        Assert.False(AssistantKnowledgeBase.IsUrgent("今日の様子は"));
        Assert.False(AssistantKnowledgeBase.IsUrgent(null));
    }

    [Fact]
    public void The_Prompt_Facts_Only_Describe_Screens_That_Exist()
    {
        var facts = AssistantKnowledgeBase.ProductFacts;

        Assert.Contains("SwitchBot設定", facts);
        Assert.Contains("連携 123456", facts);
        Assert.Contains("カメラもマイクもありません", facts);
    }

    private sealed class CountingAiRouterClient : IAiRouterClient
    {
        private readonly MockAiRouterClient _inner = new();

        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public string DisplayName => "CountingRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            CallCount++;
            return _inner.CompleteAsync(messages, purpose, jsonMode, ct);
        }
    }

    /// <summary>
    /// 画面の見出しと同じ文字列を、KB が手順の中でそのまま引用しています。
    ///
    /// 引用元のラベルを画面側で変えると、LINE は存在しないボタン名を案内しはじめます。
    /// 高齢者は書かれたとおりの文字を画面上で探すので、黙って壊れると致命的です。
    /// 型では繋がらない依存なので、テキストの一致をここで固定します。
    /// 片方だけ変えたときは、このテストが両方を直すよう促します。
    /// </summary>
    [Theory]
    [InlineData("SwitchBot設定", "src/MimamoriTai.Web/Components/Pages/Home.razor")]
    [InlineData("家族の追加", "src/MimamoriTai.Web/Components/Pages/Home.razor")]
    [InlineData("ご家族の追加", "src/MimamoriTai.Web/Components/Pages/FamilySettings.razor")]
    [InlineData("連携コードを発行する", "src/MimamoriTai.Web/Components/Pages/FamilySettings.razor")]
    public void Screen_Labels_Quoted_By_The_Knowledge_Base_Still_Exist_On_The_Screen(
        string label, string uiPath)
    {
        var root = RepoRoot();

        var knowledgeBase = File.ReadAllText(
            Path.Combine(root, "src/MimamoriTai.Core/Application/AssistantKnowledgeBase.cs"));
        Assert.Contains($"「{label}」", knowledgeBase);

        var ui = File.ReadAllText(Path.Combine(root, uiPath));
        Assert.Contains(label, ui);
    }

    /// <summary>
    /// KB は「LINEで使えるボタン」としてリッチメニューのラベルを列挙しています。
    /// メニュー側を変えたら、案内も一緒に変える必要があります。
    /// </summary>
    [Theory]
    [InlineData("助けて")]
    [InlineData("体調が悪い")]
    [InlineData("大丈夫")]
    [InlineData("今日の様子")]
    [InlineData("家族に連絡")]
    public void Line_Button_Labels_Quoted_By_The_Knowledge_Base_Still_Exist_In_The_Rich_Menu(string label)
    {
        var root = RepoRoot();

        var knowledgeBase = File.ReadAllText(
            Path.Combine(root, "src/MimamoriTai.Core/Application/AssistantKnowledgeBase.cs"));
        Assert.Contains($"「{label}」", knowledgeBase);

        var richMenu = File.ReadAllText(Path.Combine(root, "scripts/setup-line-rich-menu.ps1"));
        Assert.Contains($"'{label}'", richMenu);
    }

    /// <summary>Walks up from the test binaries until the solution file is found.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MimamoriTai.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class RecordingAiRouterClient : IAiRouterClient
    {
        private readonly MockAiRouterClient _inner = new();

        public List<string> Purposes { get; } = [];

        public bool IsConfigured => true;

        public string DisplayName => "RecordingRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            Purposes.Add(purpose);
            return _inner.CompleteAsync(messages, purpose, jsonMode, ct);
        }
    }

    /// <summary>Stands in for a router outage: every call fails, nothing is parsable.</summary>
    private sealed class DeadAiRouterClient : IAiRouterClient
    {
        public bool IsConfigured => true;

        public string DisplayName => "DeadRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default) =>
            Task.FromResult(new AiCompletionResult(false, string.Empty, DisplayName, "none", 0, "unavailable"));
    }
}

using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>Records every push/reply attempt so tests can assert exactly what was (or wasn't) sent.</summary>
public sealed class FakeLineMessagingClient : ILineMessagingClient
{
    public List<(string To, string Text)> Pushed { get; } = [];

    /// <summary>Cards passed to <see cref="PushAlertAsync"/>, so tests can assert the illustration and link.</summary>
    public List<(string To, LineAlertCard Card)> PushedCards { get; } = [];

    public bool IsConfigured { get; init; } = true;

    public bool FailNext { get; set; }

    public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default) =>
        Task.FromResult(new LineSendResult(true));

    public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default)
    {
        Pushed.Add((to, text));

        if (FailNext)
        {
            FailNext = false;
            return Task.FromResult(new LineSendResult(false, "Simulated failure"));
        }

        return Task.FromResult(new LineSendResult(true));
    }

    /// <summary>
    /// Records the card and then delegates to <see cref="PushAsync"/>, so every existing
    /// assertion about the delivered text keeps holding for the card path too.
    /// </summary>
    public Task<LineSendResult> PushAlertAsync(string to, LineAlertCard card, CancellationToken ct = default)
    {
        PushedCards.Add((to, card));
        return PushAsync(to, card.Text, ct);
    }

    public bool VerifySignature(string rawBody, string? signatureHeader) => false;
}

/// <summary>Simple resolver test double: returns the configured ToId, or a fixed list of targets.</summary>
public sealed class FakeLineRecipientResolver(IReadOnlyList<string> targets) : ILineRecipientResolver
{
    public static FakeLineRecipientResolver From(string toId) =>
        new(string.IsNullOrWhiteSpace(toId) ? [] : [toId]);

    public Task<IReadOnlyList<string>> ResolveAsync(Guid householdId, CancellationToken ct = default) =>
        Task.FromResult(targets);
}

public class WatchAlertServiceTests
{
    private static async Task<TestDb> SeedHighRiskHouseholdAsync()
    {
        // A device is registered but no DeviceEvent is created for "today", and the
        // clock is set past the 10:00 no-activity threshold, so RiskAssessmentService
        // scores this as High (60 points) — well above the default Medium threshold.
        var db = await new TestDb().SeedAsync(TestDb.Light());
        return db;
    }

    private static WatchAlertService Create(
        TestDb db, FakeTimeProvider clock, FakeLineMessagingClient line, WatchAlertSettings? settings = null, ILineRecipientResolver? resolver = null)
    {
        settings ??= new WatchAlertSettings
        {
            ToId = "test-family-group",
            Threshold = RiskLevel.Medium,
            Cooldown = TimeSpan.FromHours(1)
        };

        return new(db.Context, line, clock, settings, resolver ?? FakeLineRecipientResolver.From(settings.ToId));
    }

    /// <summary>19:00 JST == 10:00 UTC, inside the evening window.</summary>
    private static readonly DateTimeOffset EveningUtc = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static FakeHeatAdvisoryProvider ColdMorningAhead(DateOnly forDate) =>
        new(null)
        {
            Forecast = new ColdForecast(forDate, 2.0, ColdAlertLevel.SevereCold, "東京", "出典：気象庁")
        };

    /// <summary>
    /// A changing room can only be warmed the evening before, so the notice has to
    /// arrive while there is still an evening to act in -- not when the cold morning
    /// is already underway and the resident is already in the bathroom.
    /// </summary>
    [Fact]
    public async Task Warns_About_Tomorrows_Cold_Morning_The_Night_Before()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var weather = ColdMorningAhead(new DateOnly(2026, 1, 2));
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"), null, weather);

        await service.EvaluateAsync(db.HouseholdId);

        var notice = Assert.Single(line.PushedCards, c => c.Card.Title == "明日の朝の冷え込み");
        Assert.Contains("脱衣所", notice.Card.Text);
        Assert.Contains("2℃", notice.Card.Text);
    }

    /// <summary>
    /// The watch job polls every few minutes all evening. Without a per-forecast-date
    /// guard the family would get the same warning a dozen times before bedtime, which
    /// is how a household learns to mute the account.
    /// </summary>
    [Fact]
    public async Task Sends_Tomorrows_Cold_Warning_Only_Once()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var weather = ColdMorningAhead(new DateOnly(2026, 1, 2));
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"), null, weather);

        await service.EvaluateAsync(db.HouseholdId);
        await service.EvaluateAsync(db.HouseholdId);

        Assert.Single(line.PushedCards, c => c.Card.Title == "明日の朝の冷え込み");
    }

    /// <summary>
    /// Sent at noon the warning is useless: it is too far from the evening to be acted
    /// on, and by the following morning it has scrolled out of the chat.
    /// </summary>
    [Fact]
    public async Task Stays_Quiet_About_The_Cold_Morning_Outside_The_Evening()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var weather = ColdMorningAhead(new DateOnly(2026, 1, 2));
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"), null, weather);

        await service.EvaluateAsync(db.HouseholdId);

        Assert.DoesNotContain(line.PushedCards, c => c.Card.Title == "明日の朝の冷え込み");
    }

    private static WatchAlertSettings Settings() => new()
    {
        ToId = "test-family-group",
        Threshold = RiskLevel.Medium,
        Cooldown = TimeSpan.FromHours(1)
    };
    /// <summary>11:00 JST == 02:00 UTC, past the 10:00 no-activity threshold.</summary>
    private static readonly DateTimeOffset NoActivityMorningUtc = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Alert_Is_Sent_When_Risk_Is_At_Or_Above_Threshold()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line);

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.True(outcome.Sent);
        Assert.Equal(RiskLevel.High, outcome.Risk!.Level);
        Assert.Single(line.Pushed);
        Assert.Equal("test-family-group", line.Pushed[0].To);
        Assert.Contains("見守りアラート", line.Pushed[0].Text);
        Assert.Contains(outcome.Risk.Score.ToString(), line.Pushed[0].Text);
        Assert.Single(db.Context.WatchAlerts);
        Assert.True(db.Context.WatchAlerts.Single().Success);
    }

    /// <summary>
    /// The alert must arrive as a mascot card, not a bare line of text: the family
    /// recognises the character before they read anything, which is the point of
    /// having one. The link takes them straight to the dashboard.
    /// </summary>
    [Fact]
    public async Task Alert_Card_Carries_The_Mascot_And_A_Link_When_A_Public_Origin_Is_Configured()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line, new WatchAlertSettings
        {
            ToId = "test-family-group",
            Threshold = RiskLevel.Medium,
            Cooldown = TimeSpan.FromHours(1),
            // Trailing slash on purpose: the URL must not end up with a double slash.
            PublicBaseUrl = "https://example.invalid/"
        });

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.True(outcome.Sent);
        var (to, card) = Assert.Single(line.PushedCards);
        Assert.Equal("test-family-group", to);
        Assert.Equal("https://example.invalid/images/mimamo-line-alert.png", card.ImageUrl);
        Assert.Equal("https://example.invalid", card.LinkUrl);
        Assert.Equal(outcome.Message, card.Text);
        Assert.False(string.IsNullOrWhiteSpace(card.RiskLabel));
    }

    /// <summary>
    /// LINE fetches the hero image from its own servers, so a host it cannot reach
    /// would render the bubble as a grey box. Without a public https origin the card
    /// must therefore carry no image and no link, and the alert still goes out.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5234")]
    public async Task Alert_Card_Has_No_Image_When_The_Origin_Is_Not_Publicly_Reachable(string origin)
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line, new WatchAlertSettings
        {
            ToId = "test-family-group",
            Threshold = RiskLevel.Medium,
            Cooldown = TimeSpan.FromHours(1),
            PublicBaseUrl = origin
        });

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.True(outcome.Sent);
        var (_, card) = Assert.Single(line.PushedCards);
        Assert.Null(card.ImageUrl);
        Assert.Null(card.LinkUrl);
        Assert.Single(line.Pushed);
    }

    [Fact]
    public async Task Duplicate_Alert_Within_Cooldown_Is_Suppressed()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line);

        var first = await service.EvaluateAsync(db.HouseholdId);
        Assert.True(first.Sent);

        clock.Advance(TimeSpan.FromMinutes(30));
        var second = await service.EvaluateAsync(db.HouseholdId);

        Assert.False(second.Sent);
        Assert.True(second.Suppressed);
        Assert.Single(line.Pushed); // still only the first push
        Assert.Single(db.Context.WatchAlerts); // no new row was written
    }

    [Fact]
    public async Task Alert_Is_Sent_Again_After_Cooldown_Expires()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line); // 1 hour cooldown

        var first = await service.EvaluateAsync(db.HouseholdId);
        Assert.True(first.Sent);

        clock.Advance(TimeSpan.FromMinutes(90)); // past the 1 hour cooldown, still the no-activity morning
        var second = await service.EvaluateAsync(db.HouseholdId);

        Assert.True(second.Sent);
        Assert.Equal(2, line.Pushed.Count);
        Assert.Equal(2, db.Context.WatchAlerts.Count());
    }

    [Fact]
    public async Task Nothing_Throws_And_Nothing_Is_Sent_When_Line_Is_Unconfigured()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        // Empty AlertToId simulates "LINE alert target is not configured".
        var service = Create(db, clock, line, new WatchAlertSettings
        {
            ToId = string.Empty,
            Threshold = RiskLevel.Medium,
            Cooldown = TimeSpan.FromHours(6)
        });

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.False(outcome.Sent);
        Assert.Equal(WatchAlertStatus.SendFailed, outcome.Status);
        Assert.Empty(line.Pushed); // PushAsync must never be called without a target
        // It still records what it would have sent, so the demo/mock path shows it.
        Assert.Single(db.Context.WatchAlerts);
        Assert.False(db.Context.WatchAlerts.Single().Success);
    }

    [Fact]
    public async Task Below_Threshold_Risk_Sends_Nothing()
    {
        // Midday with no activity yet is below the 10:00 threshold, so RiskAssessmentService
        // only reports "まだ本日の活動記録がありません" with score 0 (Low).
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero)); // 09:30 JST
        var line = new FakeLineMessagingClient();
        var service = Create(db, clock, line);

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.False(outcome.Sent);
        Assert.Equal(WatchAlertStatus.BelowThreshold, outcome.Status);
        Assert.Empty(line.Pushed);
        Assert.Empty(db.Context.WatchAlerts);
    }

    [Fact]
    public async Task Alert_Pushes_To_Every_Recipient_And_Tolerates_A_Per_Recipient_Failure()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(NoActivityMorningUtc);
        var line = new FakeLineMessagingClient();
        var resolver = new FakeLineRecipientResolver(["Utestuser0000000000000000000000001", "Utestuser0000000000000000000000002"]);
        var service = Create(db, clock, line, new WatchAlertSettings
        {
            ToId = string.Empty, // config empty: multi-recipient DB path is exercised via the resolver
            Threshold = RiskLevel.Medium,
            Cooldown = TimeSpan.FromHours(1)
        }, resolver);

        // The first recipient's push fails; the second must still be attempted.
        line.FailNext = true;

        var outcome = await service.EvaluateAsync(db.HouseholdId);

        Assert.True(outcome.Sent); // overall success, since at least one recipient received it
        Assert.Equal(2, line.Pushed.Count);
        Assert.Contains(line.Pushed, p => p.To == "Utestuser0000000000000000000000001");
        Assert.Contains(line.Pushed, p => p.To == "Utestuser0000000000000000000000002");
        Assert.True(db.Context.WatchAlerts.Single().Success);
    }
}

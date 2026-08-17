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
    /// <summary>Returns a fixed advisory list, so the notice can be tested without 気象庁.</summary>
    private sealed class FakeDisasterProvider(params DisasterAdvisory[] active) : IDisasterAdvisoryProvider
    {
        public bool IsConfigured { get; init; } = true;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<DisasterAdvisory>> GetActiveAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DisasterAdvisory>>(active);
        }
    }

    private static DisasterAdvisory Advisory(
        DisasterKind kind, string headline, string area = "東京都", string? detail = null) =>
        new(kind, headline, area, detail, new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "出典：気象庁");

    /// <summary>
    /// 緊急速報メール already tells the family it is raining. The only thing this app can
    /// add is whether the appliances have been touched, so that has to be in the text --
    /// otherwise the push is a duplicate of one they already got.
    /// </summary>
    [Fact]
    public async Task Disaster_Notice_Pairs_The_Warning_With_Whether_The_Appliances_Ran()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(Advisory(DisasterKind.Landslide, "土砂災害警戒情報")));

        await service.EvaluateAsync(db.HouseholdId);

        var notice = Assert.Single(line.PushedCards, c => c.Card.Title == "土砂災害警戒情報");
        Assert.Contains("土砂災害警戒情報が出ています", notice.Card.Text);
        // The seeded household has no activity today, and the notice must say exactly
        // that rather than rounding silence up to reassurance.
        Assert.Contains("確認できていません", notice.Card.Text);
    }

    /// <summary>
    /// A 土砂災害警戒情報 stays active for hours and the watch job polls every few
    /// minutes. Without dedupe on the advisory's own identity that is dozens of pushes.
    /// </summary>
    [Fact]
    public async Task Disaster_Notice_Is_Sent_Once_Per_Advisory()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var provider = new FakeDisasterProvider(Advisory(DisasterKind.Landslide, "土砂災害警戒情報"));
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, provider);

        await service.EvaluateAsync(db.HouseholdId);
        await service.EvaluateAsync(db.HouseholdId);

        Assert.Single(line.PushedCards, c => c.Card.Title == "土砂災害警戒情報");
    }

    /// <summary>
    /// Heavy rain and a landslide warning arrive together by design. Two pushes about
    /// the same weather is how a family learns to swipe the app away, taking the
    /// heatstroke alert with it.
    /// </summary>
    [Fact]
    public async Task Only_The_Most_Serious_Active_Advisory_Is_Sent()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(
                Advisory(DisasterKind.HeavyRainBand, "顕著な大雨に関する気象情報"),
                Advisory(DisasterKind.SpecialWarning, "大雨特別警報"),
                Advisory(DisasterKind.Landslide, "土砂災害警戒情報")));

        await service.EvaluateAsync(db.HouseholdId);

        var notice = Assert.Single(line.PushedCards, c => c.Card.RiskLabel == "ご家族の様子を確認しましょう");
        Assert.Equal("大雨特別警報", notice.Card.Title);
    }

    /// <summary>An earthquake is reported with the 震度 the household's own area felt.</summary>
    [Fact]
    public async Task Earthquake_Notice_Reports_The_Local_Intensity()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(
                Advisory(DisasterKind.Earthquake, "地震", "東京湾", "震度5弱")));

        await service.EvaluateAsync(db.HouseholdId);

        var notice = Assert.Single(line.PushedCards, c => c.Card.Title == "地震がありました");
        Assert.Contains("震度5弱", notice.Card.Text);
        Assert.Contains("東京湾", notice.Card.Text);
    }

    /// <summary>Nothing active means nothing sent, and no extra card to ignore.</summary>
    [Fact]
    public async Task No_Disaster_Notice_When_Nothing_Is_Active()
    {
        using var db = await SeedHighRiskHouseholdAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider());

        await service.EvaluateAsync(db.HouseholdId);

        Assert.DoesNotContain(line.PushedCards, c => c.Card.RiskLabel == "ご家族の様子を確認しましょう");
    }

    [Theory]
    // The exact hallucination this guard exists for: the rule could only say the room
    // was unverifiable, and the model turned it into a measurement of the air conditioner.
    [InlineData("お母さんの活動量が減り、エアコン未使用です。様子を確認してみてください。",
        "暑さ指数28.5（厳重警戒）です。冷房機器が未登録のため室内の状況は確認できません", true)]
    [InlineData("暑さが続いています。室温にお気をつけください。",
        "暑さ指数28.5（厳重警戒）です。冷房機器が未登録のため室内の状況は確認できません", false)]
    // When the rule did establish it, the model may restate it in its own words.
    [InlineData("エアコンが使われていないようです。様子を確認してみてください。",
        "暑さ指数28.5（厳重警戒）ですが、エアコンが動いていません（熱中症の恐れ）", false)]
    [InlineData("暖房が切れたままのようです。",
        "気温4.0℃（厳しい冷え込み）ですが、ヒーターが動いていません（ヒートショック・低体温症の恐れ）", false)]
    // No appliance claim at all in either direction.
    [InlineData("いつもより活動が少なめです。お電話してみてはいかがでしょうか。",
        "普段（平均5.1回）より活動量が少なめです", false)]
    public void Generated_text_may_not_claim_an_appliance_is_idle_unless_the_rule_did(
        string text, string reason, bool rejected)
    {
        Assert.Equal(rejected, WatchAlertService.InventsApplianceState(text, reason));
    }

    /// <summary>
    /// The case this feature exists for. A warning is out, and the meters say the house
    /// has drawn almost nothing for hours -- which most often means she is not in it.
    /// Neither figure means much alone; together they are the one push in this app worth
    /// interrupting a family for.
    /// </summary>
    [Fact]
    public async Task Warning_Plus_A_Dark_House_Is_Escalated_To_Call_Her_Now()
    {
        using var db = await SeedFortnightThenSilenceAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(
                Advisory(DisasterKind.HeavyRainBand, "顕著な大雨に関する気象情報")));

        await service.EvaluateAsync(db.HouseholdId);

        var notice = Assert.Single(line.PushedCards, c => c.Card.RiskLabel == "至急ご連絡ください");
        Assert.Equal("顕著な大雨に関する気象情報", notice.Card.Title);
        Assert.Contains("顕著な大雨に関する気象情報が出ています", notice.Card.Text);
        Assert.Contains("外出されているかもしれません", notice.Card.Text);
        // The share is measured, not asserted: it has to come out of this household's own
        // hours against its own fortnight.
        Assert.Matches("いつもの[0-9]+％まで落ちています", notice.Card.Text);

        // The calm notice must not also go out; one advisory, one push.
        Assert.DoesNotContain(line.PushedCards, c => c.Card.RiskLabel == "ご家族の様子を確認しましょう");

        var row = Assert.Single(db.Context.WatchAlerts, a => a.Reason.StartsWith("防災情報 留守の可能性 "));
        Assert.Equal(RiskLevel.High, row.RiskLevel);
    }

    /// <summary>
    /// The watch job polls every few minutes and a 線状降水帯 stays up for hours. The
    /// urgent notice is deduplicated on the advisory's own identity, exactly like the
    /// calm one.
    /// </summary>
    [Fact]
    public async Task The_Escalated_Notice_Is_Sent_Once_Per_Advisory()
    {
        using var db = await SeedFortnightThenSilenceAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(
                Advisory(DisasterKind.HeavyRainBand, "顕著な大雨に関する気象情報")));

        await service.EvaluateAsync(db.HouseholdId);
        await service.EvaluateAsync(db.HouseholdId);

        Assert.Single(line.PushedCards, c => c.Card.RiskLabel == "至急ご連絡ください");
    }

    /// <summary>
    /// A household drawing its ordinary electricity through a warning gets the calm
    /// notice, not the telephone call. Escalating that would make the urgent wording
    /// mean nothing within a season.
    /// </summary>
    [Fact]
    public async Task A_House_Being_Lived_In_Through_A_Warning_Is_Not_Escalated()
    {
        using var db = await SeedFortnightThenSilenceAsync(quietToday: false);
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider(
                Advisory(DisasterKind.HeavyRainBand, "顕著な大雨に関する気象情報")));

        await service.EvaluateAsync(db.HouseholdId);

        Assert.DoesNotContain(line.PushedCards, c => c.Card.RiskLabel == "至急ご連絡ください");
        Assert.Single(line.PushedCards, c => c.Card.RiskLabel == "ご家族の様子を確認しましょう");
    }

    /// <summary>
    /// A dark house on an ordinary evening is somebody out for dinner. Without emergency
    /// information over her area there is nothing here to tell anyone.
    /// </summary>
    [Fact]
    public async Task A_Dark_House_Alone_Is_Not_An_Emergency()
    {
        using var db = await SeedFortnightThenSilenceAsync();
        var clock = new FakeTimeProvider(EveningUtc);
        var line = new FakeLineMessagingClient();
        var service = new WatchAlertService(
            db.Context, line, clock, Settings(), FakeLineRecipientResolver.From("test-family-group"),
            null, null, new FakeDisasterProvider());

        await service.EvaluateAsync(db.HouseholdId);

        Assert.DoesNotContain(line.PushedCards, c => c.Card.RiskLabel == "至急ご連絡ください");
    }

    /// <summary>
    /// A fortnight of ordinary evenings, then a today that either matches them or falls
    /// silent from 16:00. The clock is <see cref="EveningUtc"/> (19:00 JST), so the last
    /// whole hours the rule examines are 16, 17 and 18.
    /// </summary>
    private static async Task<TestDb> SeedFortnightThenSilenceAsync(bool quietToday = true)
    {
        var light = TestDb.Light();
        var db = await new TestDb().SeedAsync(light);
        var today = HouseholdTime.LocalDate(EveningUtc);

        void Draw(DateOnly date, double fromHour, double toHour, double watts = 200)
        {
            // Every quarter hour, which is inside the 30-minute integration gap, so the
            // draw reads as continuous without seeding a poll's worth of rows.
            for (var h = fromHour; h <= toHour + 0.001; h += 0.25)
            {
                db.Context.PlugMiniReadings.Add(new PlugMiniReading
                {
                    HouseholdId = db.HouseholdId,
                    DeviceId = light.Id,
                    OccurredAtUtc = HouseholdTime.StartOfLocalDayUtc(date).AddHours(h),
                    ApproxWatts = watts
                });
            }
        }

        for (var back = 1; back <= 13; back++)
        {
            Draw(today.AddDays(-back), 9, 21);
        }

        // Today: lived in all morning, then either the same evening as always or a socket
        // that keeps reporting while drawing nothing. The poll carries on either way --
        // silence in this app is a measured zero, never an absence of rows.
        if (quietToday)
        {
            Draw(today, 9, 15.75);
            Draw(today, 16, 19, watts: 0);
        }
        else
        {
            Draw(today, 9, 19);
        }

        await db.Context.SaveChangesAsync();
        return db;
    }

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

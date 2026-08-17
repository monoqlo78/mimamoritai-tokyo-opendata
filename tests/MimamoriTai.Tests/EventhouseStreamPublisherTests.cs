using System.Net;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>Returns a static access token without ever touching a real identity provider.</summary>
public sealed class FakeTokenCredential(string token = "fake-token") : TokenCredential
{
    public int CallCount { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        CallCount++;
        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        CallCount++;
        return new ValueTask<AccessToken>(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
    }
}

/// <summary>Captures the outgoing request (URL + body) and returns a canned response, mirroring the stub pattern used for SwitchBot/Model Router tests.</summary>
public sealed class StubHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public Exception? ThrowOnSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        return new HttpResponseMessage(statusCode);
    }
}

public class EventhouseStreamPublisherTests
{
    private static readonly DeviceEventRecord SampleEvent1 = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "リビング照明",
        "リビング",
        "Light",
        "PowerState",
        "on",
        12.5,
        "SwitchBotPoll",
        new DateTime(2026, 8, 8, 14, 27, 24, DateTimeKind.Utc));

    private static readonly DeviceEventRecord SampleEvent2 = SampleEvent1 with
    {
        EventId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        State = "off"
    };

    private static EventhouseOptions Options() => new()
    {
        Enabled = true,
        ClusterUri = "https://trd-test.z2.kusto.fabric.microsoft.com",
        DatabaseName = "MimamoriEventhouse",
        TableName = "DeviceEvents",
        MappingName = "DeviceEventsMapping",
        TimeoutSeconds = 30
    };

    private static (EventhouseStreamPublisher Publisher, StubHttpMessageHandler Handler, FakeTokenCredential Credential) Create(
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(statusCode);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://trd-test.z2.kusto.fabric.microsoft.com/") };
        var credential = new FakeTokenCredential();
        var publisher = new EventhouseStreamPublisher(
            http,
            Microsoft.Extensions.Options.Options.Create(Options()),
            credential,
            NullLogger<EventhouseStreamPublisher>.Instance);

        return (publisher, handler, credential);
    }

    [Fact]
    public async Task PublishAsync_Builds_NewlineDelimitedJson_With_CamelCase_And_RoundTrip_Utc()
    {
        var (publisher, handler, _) = Create();

        var result = await publisher.PublishAsync([SampleEvent1, SampleEvent2]);

        Assert.True(result.Success);
        Assert.Equal(2, result.PublishedCount);

        Assert.NotNull(handler.LastRequestBody);
        var lines = handler.LastRequestBody!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        Assert.Contains("\"eventId\":\"11111111-1111-1111-1111-111111111111\"", lines[0]);
        Assert.Contains("\"householdId\":\"22222222-2222-2222-2222-222222222222\"", lines[0]);
        Assert.Contains("\"deviceId\":\"33333333-3333-3333-3333-333333333333\"", lines[0]);
        Assert.Contains("\"deviceName\":\"リビング照明\"", lines[0]);
        Assert.Contains("\"room\":\"リビング\"", lines[0]);
        Assert.Contains("\"deviceType\":\"Light\"", lines[0]);
        Assert.Contains("\"eventType\":\"PowerState\"", lines[0]);
        Assert.Contains("\"state\":\"on\"", lines[0]);
        Assert.Contains("\"powerWatts\":12.5", lines[0]);
        Assert.Contains("\"source\":\"SwitchBotPoll\"", lines[0]);
        Assert.Contains("\"occurredAtUtc\":\"2026-08-08T14:27:24.0000000Z\"", lines[0]);

        Assert.Contains("\"state\":\"off\"", lines[1]);
    }

    [Fact]
    public async Task PublishAsync_Targets_Correct_Relative_Url_With_StreamFormat_And_MappingName()
    {
        var (publisher, handler, _) = Create();

        await publisher.PublishAsync([SampleEvent1]);

        Assert.NotNull(handler.LastRequest);
        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/v1/rest/ingest/MimamoriEventhouse/DeviceEvents", uri.AbsolutePath);
        Assert.Contains("streamFormat=json", uri.Query);
        Assert.Contains("mappingName=DeviceEventsMapping", uri.Query);
    }

    [Fact]
    public async Task PublishAsync_Returns_Failure_Without_Throwing_On_NonSuccess_Status()
    {
        var (publisher, _, _) = Create(HttpStatusCode.InternalServerError);

        var result = await publisher.PublishAsync([SampleEvent1]);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PublishAsync_Returns_Failure_Without_Throwing_On_HttpRequestException()
    {
        var (publisher, handler, _) = Create();
        handler.ThrowOnSend = new HttpRequestException("network down");

        var result = await publisher.PublishAsync([SampleEvent1]);

        Assert.False(result.Success);
        Assert.Equal(nameof(HttpRequestException), result.Error);
    }

    [Fact]
    public async Task PublishAsync_Returns_NotConfigured_Failure_When_Disabled()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://trd-test.z2.kusto.fabric.microsoft.com/") };
        var options = Options();
        options.Enabled = false;
        var publisher = new EventhouseStreamPublisher(
            http,
            Microsoft.Extensions.Options.Options.Create(options),
            new FakeTokenCredential(),
            NullLogger<EventhouseStreamPublisher>.Instance);

        var result = await publisher.PublishAsync([SampleEvent1]);

        Assert.False(result.Success);
        Assert.Null(handler.LastRequest);
    }
}

public class MockEventStreamPublisherTests
{
    [Fact]
    public async Task PublishAsync_Reports_Success_And_Input_Count()
    {
        var publisher = new MockEventStreamPublisher(NullLogger<MockEventStreamPublisher>.Instance);

        var result = await publisher.PublishAsync(
        [
            new DeviceEventRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "n", "r", "Light", "PowerState", "on", 1.0, "Manual", DateTime.UtcNow),
            new DeviceEventRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "n", "r", "Light", "PowerState", "off", null, "Manual", DateTime.UtcNow)
        ]);

        Assert.True(result.Success);
        Assert.Equal(2, result.PublishedCount);
        Assert.False(publisher.IsConfigured);
        Assert.Equal("MockEventStream", publisher.DisplayName);
    }
}

public class EventhouseOptionsTests
{
    [Fact]
    public void IsConfigured_Is_False_When_Enabled_Is_False_Even_If_Everything_Else_Is_Set()
    {
        var options = new EventhouseOptions
        {
            Enabled = false,
            ClusterUri = "https://trd-test.z2.kusto.fabric.microsoft.com",
            DatabaseName = "MimamoriEventhouse",
            TableName = "DeviceEvents",
            MappingName = "DeviceEventsMapping"
        };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_Is_True_When_Enabled_And_All_Required_Fields_Set()
    {
        var options = new EventhouseOptions
        {
            Enabled = true,
            ClusterUri = "https://trd-test.z2.kusto.fabric.microsoft.com",
            DatabaseName = "MimamoriEventhouse",
            TableName = "DeviceEvents"
        };

        Assert.True(options.IsConfigured);
    }
}

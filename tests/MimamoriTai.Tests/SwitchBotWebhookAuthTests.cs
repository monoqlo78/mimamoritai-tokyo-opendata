using Microsoft.AspNetCore.Http;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Web.Endpoints;

namespace MimamoriTai.Tests;

/// <summary>
/// Regression tests for the SwitchBot webhook's shared-secret check.
///
/// The endpoint originally accepted any POST: SwitchBot, unlike LINE, does not sign its
/// callbacks, so there was no <c>X-Line-Signature</c> counterpart to verify and none was
/// added. That let anyone who learned the URL post a fabricated state change for a known
/// device id, which keeps the inactivity watchdog quiet -- the single failure mode this
/// app exists to prevent. These tests pin the fail-closed behaviour, including the case
/// that matters most: no secret configured must mean "reject", not "allow".
/// </summary>
public class SwitchBotWebhookAuthTests
{
    private const string Secret = "s3cret-callback-value";

    [Fact]
    public void Rejects_When_No_Secret_Is_Configured()
    {
        var request = Request();
        var options = new SwitchBotOptions(); // WebhookSecret unset, no explicit opt-out

        var allowed = WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out var reason);

        Assert.False(allowed);
        Assert.Contains("WebhookSecret", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_Unauthenticated_Only_When_Explicitly_Opted_In()
    {
        var request = Request();
        var options = new SwitchBotOptions { AllowUnauthenticatedWebhook = true };

        Assert.True(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out _));
    }

    [Fact]
    public void Rejects_A_Caller_That_Presents_No_Token()
    {
        var request = Request();
        var options = new SwitchBotOptions { WebhookSecret = Secret };

        Assert.False(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out var reason));
        Assert.Contains("No webhook token", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_A_Wrong_Token()
    {
        var request = Request(header: "not-the-secret");
        var options = new SwitchBotOptions { WebhookSecret = Secret };

        Assert.False(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out var reason));
        Assert.Contains("did not match", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_A_Prefix_Of_The_Secret()
    {
        // Guards the constant-time comparison: hashing both sides first means a partial
        // match is no closer to acceptance than a completely different value.
        var request = Request(header: Secret[..5]);
        var options = new SwitchBotOptions { WebhookSecret = Secret };

        Assert.False(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out _));
    }

    [Fact]
    public void Accepts_The_Secret_In_The_Header()
    {
        var request = Request(header: Secret);
        var options = new SwitchBotOptions { WebhookSecret = Secret };

        Assert.True(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Accepts_The_Secret_On_The_Query_String()
    {
        // The SwitchBot console only lets a callback URL be configured, not headers, so
        // the query form is the one that is actually usable in production.
        var request = Request(query: Secret);
        var options = new SwitchBotOptions { WebhookSecret = Secret };

        Assert.True(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out _));
    }

    [Fact]
    public void A_Configured_Secret_Wins_Over_The_Unauthenticated_Opt_In()
    {
        // Both set: the secret must still be required, so flipping the dev escape hatch on
        // in a deployed environment cannot quietly reopen the endpoint.
        var request = Request();
        var options = new SwitchBotOptions { WebhookSecret = Secret, AllowUnauthenticatedWebhook = true };

        Assert.False(WebhookEndpoints.IsSwitchBotCallerAuthorised(request, options, out _));
    }

    private static HttpRequest Request(string? header = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;

        if (header is not null)
        {
            request.Headers[WebhookEndpoints.SwitchBotWebhookTokenHeader] = header;
        }

        if (query is not null)
        {
            request.QueryString = QueryString.Create("token", query);
        }

        return request;
    }
}

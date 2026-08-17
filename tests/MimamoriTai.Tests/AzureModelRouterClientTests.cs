using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Ai;

namespace MimamoriTai.Tests;

/// <summary>
/// Verifies the Azure AI Foundry model router transport against the documented wire
/// format (https://learn.microsoft.com/azure/ai-foundry/openai/how-to/model-router)
/// without needing a live deployment: request shape and route, both auth modes, JSON
/// mode, capture of the model the router actually selected, the deadline-bound budget,
/// retry-on-429/5xx and graceful degradation.
/// </summary>
public sealed class AzureModelRouterClientTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Endpoint = "https://mimamoritai-test.openai.azure.com";
    private const string ApiKey = "test-key-0123456789";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<string> Bodies { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedHandler Then(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            });

            return this;
        }

        public ScriptedHandler ThenTimeout()
        {
            // HttpClient surfaces its own Timeout as TaskCanceledException with the
            // caller's token still unsignalled -- the exact shape the client treats
            // as "the router took too long", distinct from a caller cancellation.
            _responses.Enqueue(() => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout elapsing."));

            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            return _responses.Count > 0
                ? _responses.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok(), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>Stand-in credential so the Entra ID path can be exercised offline.</summary>
    private sealed class StubCredential(string token) : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));

        public string[] Scopes { get; private set; } = [];
    }

    private static string Ok(string content = "こんにちは", string model = "gpt-4.1-mini-2025-04-14") =>
        JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content } } }
        });

    private static AzureModelRouterClient Create(
        ScriptedHandler handler,
        AzureModelRouterOptions? options = null,
        TokenCredential? credential = null)
    {
        options ??= new AzureModelRouterOptions { Endpoint = Endpoint, ApiKey = ApiKey };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BuildBaseAddress())
        };

        return new AzureModelRouterClient(
            http, Options.Create(options), NullLogger<AzureModelRouterClient>.Instance, credential);
    }

    private static IReadOnlyList<AiMessage> Prompt() =>
        [new AiMessage("system", "あなたは見守りアシスタントです。"), new AiMessage("user", "リビングの電気を消して")];

    [Fact]
    public async Task Posts_chat_completion_to_the_deployment_route_with_the_api_key()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler);

        var result = await client.CompleteAsync(Prompt(), "intent");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://mimamoritai-test.openai.azure.com/openai/deployments/model-router/chat/completions?api-version=2024-10-21",
            request.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Null(request.Headers.Authorization);

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;

        // The router deployment is the model; picking the underlying one is its job.
        Assert.Equal("model-router", body.GetProperty("model").GetString());
        Assert.Equal(2, body.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());

        Assert.True(result.Success);
        Assert.Equal("こんにちは", result.Content);
        Assert.Equal("Azure Model Router", result.Router);

        output.WriteLine("Model Router request body: " + handler.Bodies[0]);
    }

    /// <summary>
    /// Leaving the API version empty selects the version-less /openai/v1/ route, which
    /// Azure OpenAI now offers alongside the dated one. Both are documented; supporting
    /// the newer shape means the config can follow the resource without a code change.
    /// </summary>
    [Fact]
    public async Task Uses_the_version_less_v1_route_when_no_api_version_is_set()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            ApiVersion = string.Empty
        });

        await client.CompleteAsync(Prompt(), "intent");

        Assert.Equal(
            "https://mimamoritai-test.openai.azure.com/openai/v1/chat/completions",
            handler.Requests[0].RequestUri!.ToString());

        // The deployment still has to travel somewhere: on this route it is the body's model.
        Assert.Equal("model-router", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Sends_json_object_response_format_only_in_json_mode()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, Ok("{\"intent\":\"none\"}"))
            .Then(HttpStatusCode.OK, Ok());

        var client = Create(handler);

        await client.CompleteAsync(Prompt(), "intent", jsonMode: true);
        await client.CompleteAsync(Prompt(), "conversation");

        var jsonCall = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("json_object", jsonCall.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0, jsonCall.GetProperty("temperature").GetDouble());

        // Omitted rather than sent as null: a prose reply must stay free-form.
        Assert.False(JsonDocument.Parse(handler.Bodies[1]).RootElement.TryGetProperty("response_format", out _));
    }

    /// <summary>
    /// The response's model field names the model the router chose. Recording it is what
    /// keeps the routing decision auditable instead of a black box, and it is the value
    /// the operations console shows.
    /// </summary>
    [Fact]
    public async Task Reports_the_model_the_router_selected()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, Ok(model: "gpt-5-mini-2025-08-07"), ("apim-request-id", "req_abc123"));

        var client = Create(handler);

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.True(result.Success);
        Assert.Equal("gpt-5-mini-2025-08-07", result.ResolvedModel);
        Assert.Equal("Azure Model Router", result.Router);
    }

    /// <summary>Without a model in the response the deployment name is the honest fallback.</summary>
    [Fact]
    public async Task Falls_back_to_the_deployment_name_when_the_response_omits_the_model()
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "はい" } } }
        });

        var client = Create(new ScriptedHandler().Then(HttpStatusCode.OK, payload));

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.True(result.Success);
        Assert.Equal("model-router", result.ResolvedModel);
    }

    [Fact]
    public async Task Retries_once_after_429_and_honours_retry_after_within_the_cap()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"rate limited\"}}", ("Retry-After", "30"))
            .Then(HttpStatusCode.OK, Ok());

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 1,
            MaxRetryDelaySeconds = 0.5
        });

        var sw = Stopwatch.StartNew();
        var result = await client.CompleteAsync(Prompt(), "intent");
        sw.Stop();

        Assert.True(result.Success);
        Assert.Equal(2, handler.Requests.Count);

        // A rate limit that suggests 30s must not stall a user-facing request for 30s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Retry waited {sw.Elapsed}, ignoring the cap.");
    }

    [Fact]
    public async Task Retries_after_a_server_error()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.ServiceUnavailable, "{}")
            .Then(HttpStatusCode.OK, Ok());

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 2,
            MaxRetryDelaySeconds = 0.5
        });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.True(result.Success);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>A rejected key or a malformed request will fail identically on a retry.</summary>
    [Fact]
    public async Task Does_not_retry_a_client_error()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.Unauthorized, "{}");

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 2
        });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.False(result.Success);
        Assert.Single(handler.Requests);
        Assert.Equal("Azure Model Router returned 401.", result.Error);
    }

    /// <summary>
    /// The error is recorded and shown, so it must carry the status only. A response body
    /// can echo the prompt, which here contains household detail.
    /// </summary>
    [Fact]
    public async Task Error_text_never_carries_the_response_body()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"リビングの電気を消して is invalid\"}}");

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 0
        });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.False(result.Success);
        Assert.DoesNotContain("リビング", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_not_configured_without_calling_out()
    {
        var handler = new ScriptedHandler();
        var client = Create(handler, new AzureModelRouterOptions { Endpoint = Endpoint, ApiKey = string.Empty });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.False(client.IsConfigured);
        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
        Assert.Equal("Azure Model Router is not configured.", result.Error);
    }

    [Fact]
    public async Task Max_retries_zero_makes_exactly_one_attempt()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.TooManyRequests, "{}");

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 0
        });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.False(result.Success);
        Assert.Single(handler.Requests);
    }

    /// <summary>A slow router degrades to the deterministic wording, never to an exception.</summary>
    [Fact]
    public async Task Timeout_degrades_gracefully()
    {
        var handler = new ScriptedHandler().ThenTimeout();

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 0
        });

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.False(result.Success);
        Assert.Equal("Timeout", result.Error);
        Assert.Empty(result.Content);
    }

    /// <summary>
    /// Caller cancellation is not a router fault: it must be reported distinctly from a
    /// timeout and must not be retried, or a shutdown would fan out into extra calls.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            MaxRetries = 2
        });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await client.CompleteAsync(Prompt(), "summary", jsonMode: false, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Equal("Canceled", result.Error);

        // The point is that it stops after the first attempt: MaxRetries = 2 would
        // otherwise turn one shutdown into three calls.
        Assert.Single(handler.Requests);

        // Nothing answered, so there is no model to name. The console keys its
        // "unresolved" bar off exactly this.
        Assert.Equal(string.Empty, result.ResolvedModel);
    }

    /// <summary>
    /// Model router may route a prompt to a reasoning model that thinks for tens of
    /// seconds. LINE cancels an event after 8s, so that path gets its own shorter budget
    /// while everything else keeps the full one.
    /// </summary>
    [Fact]
    public void Deadline_bound_purposes_get_a_shorter_budget()
    {
        var options = new AzureModelRouterOptions { TimeoutSeconds = 30, FastTimeoutSeconds = 10 };

        Assert.Equal(TimeSpan.FromSeconds(10), options.ResolveTimeout("summary-fast"));
        Assert.Equal(TimeSpan.FromSeconds(10), options.ResolveTimeout("conversation-fast"));
        Assert.Equal(TimeSpan.FromSeconds(30), options.ResolveTimeout("summary"));
        Assert.Equal(TimeSpan.FromSeconds(30), options.ResolveTimeout("intent"));
        Assert.Equal(TimeSpan.FromSeconds(30), options.ResolveTimeout(null));
    }

    /// <summary>The suffix AssistantOrchestrator appends is the one the options recognise.</summary>
    [Fact]
    public void Fast_suffix_matches_the_purpose_the_orchestrator_emits()
    {
        Assert.EndsWith(AzureModelRouterOptions.FastSuffix, "summary-fast", StringComparison.Ordinal);
        Assert.EndsWith(AzureModelRouterOptions.FastSuffix, "conversation-fast", StringComparison.Ordinal);
    }

    /// <summary>A fast budget must never exceed the ordinary one, however it is configured.</summary>
    [Fact]
    public void Fast_budget_never_exceeds_the_normal_budget()
    {
        var options = new AzureModelRouterOptions { TimeoutSeconds = 5, FastTimeoutSeconds = 60 };

        Assert.Equal(TimeSpan.FromSeconds(5), options.ResolveTimeout("summary-fast"));
    }

    /// <summary>
    /// Passwordless auth is the preferred production setup: a managed identity has no
    /// key to rotate or leak into configuration.
    /// </summary>
    [Fact]
    public async Task Uses_an_entra_id_bearer_token_when_configured_and_caches_it()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, Ok())
            .Then(HttpStatusCode.OK, Ok());

        var credential = new StubCredential("stub-access-token");

        var client = Create(handler, new AzureModelRouterOptions
        {
            Endpoint = Endpoint,
            UseEntraId = true
        }, credential);

        Assert.True(client.IsConfigured);

        await client.CompleteAsync(Prompt(), "intent");
        await client.CompleteAsync(Prompt(), "intent");

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("stub-access-token", auth.Parameter);
        Assert.False(handler.Requests[0].Headers.Contains("api-key"));
        Assert.Equal("https://cognitiveservices.azure.com/.default", Assert.Single(credential.Scopes));

        // A token valid for an hour must not be re-fetched on every completion.
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public void Is_configured_only_with_an_endpoint_and_a_credential()
    {
        Assert.False(new AzureModelRouterOptions().IsConfigured);
        Assert.False(new AzureModelRouterOptions { ApiKey = ApiKey }.IsConfigured);
        Assert.False(new AzureModelRouterOptions { Endpoint = Endpoint }.IsConfigured);
        Assert.False(new AzureModelRouterOptions { Endpoint = Endpoint, ApiKey = ApiKey, Deployment = "" }.IsConfigured);

        Assert.True(new AzureModelRouterOptions { Endpoint = Endpoint, ApiKey = ApiKey }.IsConfigured);
        Assert.True(new AzureModelRouterOptions { Endpoint = Endpoint, UseEntraId = true }.IsConfigured);
    }

    /// <summary>A trailing slash in configuration must not produce a doubled path segment.</summary>
    [Fact]
    public void Base_address_normalises_a_trailing_slash()
    {
        Assert.Equal(
            "https://mimamoritai-test.openai.azure.com/",
            new AzureModelRouterOptions { Endpoint = Endpoint + "/" }.BuildBaseAddress());

        Assert.Equal(
            "https://mimamoritai-test.openai.azure.com/",
            new AzureModelRouterOptions { Endpoint = Endpoint }.BuildBaseAddress());
    }

    /// <summary>
    /// The service reports billed tokens in every successful completion, but the response
    /// DTO did not declare <c>usage</c>, so the counts were parsed away and never reached
    /// the app. That left every prompt-size decision unmeasurable -- there was no number to
    /// compare a "shorter prompt" against. These three cases pin the counts through.
    /// </summary>
    [Fact]
    public async Task Reads_the_token_usage_the_service_reports()
    {
        var body = JsonSerializer.Serialize(new
        {
            model = "gpt-4.1-mini-2025-04-14",
            choices = new[] { new { message = new { role = "assistant", content = "こんにちは" } } },
            usage = new { prompt_tokens = 812, completion_tokens = 96, total_tokens = 908 }
        });

        var client = Create(new ScriptedHandler().Then(HttpStatusCode.OK, body));

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.True(result.Success);
        Assert.Equal(812, result.PromptTokens);
        Assert.Equal(96, result.CompletionTokens);

        // Taken as reported rather than recomputed: for some models the total includes
        // tokens counted in neither field, and re-deriving it would under-report the bill.
        Assert.Equal(908, result.TotalTokens);
    }

    [Fact]
    public async Task Reports_no_tokens_when_the_service_omits_usage()
    {
        var client = Create(new ScriptedHandler().Then(HttpStatusCode.OK, Ok()));

        var result = await client.CompleteAsync(Prompt(), "intent");

        // Null, not zero: "not reported" and "cost nothing" must stay distinguishable, or
        // an aggregate over old rows reads as a free model.
        Assert.True(result.Success);
        Assert.Null(result.PromptTokens);
        Assert.Null(result.CompletionTokens);
        Assert.Null(result.TotalTokens);
    }

    [Fact]
    public async Task Reports_no_tokens_for_a_failed_call()
    {
        var client = Create(new ScriptedHandler().Then(HttpStatusCode.Unauthorized, "{}"));

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.False(result.Success);
        Assert.Null(result.TotalTokens);
    }
}

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// Chat Completions client pointed at an Azure AI Foundry <c>model-router</c> deployment.
///
/// Model router is a single deployed model that picks the best underlying LLM per prompt,
/// so this app no longer pins a model per call site. The response's <c>model</c> field
/// names the model the router actually chose, and that is what is recorded on
/// <see cref="AiCompletionResult.ResolvedModel"/> and shown on the operations console —
/// the routing decision stays visible rather than becoming a black box.
///
/// Wire format per https://learn.microsoft.com/azure/ai-foundry/openai/how-to/model-router:
/// - POST {Endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...
///   (or the version-less /openai/v1/chat/completions route when ApiVersion is empty).
/// - Auth: the <c>api-key</c> header, or an Entra ID bearer token when UseEntraId is set.
/// - 429 responses carry Retry-After, in seconds.
///
/// Never throws out to the caller: every failure becomes an unsuccessful
/// <see cref="AiCompletionResult"/> so the product degrades to its deterministic wording
/// instead of erroring.
/// </summary>
public sealed class AzureModelRouterClient(
    HttpClient http,
    IOptions<AzureModelRouterOptions> options,
    ILogger<AzureModelRouterClient> logger,
    TokenCredential? credential = null) : IAiRouterClient
{
    /// <summary>Azure's request correlation headers, safe to log and what support asks for.</summary>
    public const string RequestIdHeader = "apim-request-id";
    public const string AlternateRequestIdHeader = "x-ms-request-id";

    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly AzureModelRouterOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "Azure Model Router";

    public async Task<AiCompletionResult> CompleteAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var model = _options.ResolveModel();

        if (!IsConfigured)
        {
            return new AiCompletionResult(
                false, string.Empty, DisplayName, string.Empty, 0, "Azure Model Router is not configured.");
        }

        var attempts = Math.Max(_options.MaxRetries, 0) + 1;
        AiCompletionResult? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var (result, retryAfter) = await SendOnceAsync(messages, purpose, jsonMode, model, sw, ct);
            last = result;

            if (result.Success || retryAfter is null || attempt == attempts)
            {
                return result;
            }

            logger.LogWarning(
                "Azure Model Router request for {Purpose} is retryable ({Error}); attempt {Attempt}/{Attempts} after {Delay}s.",
                purpose, result.Error, attempt, attempts, retryAfter.Value.TotalSeconds);

            try
            {
                await Task.Delay(retryAfter.Value, ct);
            }
            catch (OperationCanceledException)
            {
                return result;
            }
        }

        return last!;
    }

    private async Task<(AiCompletionResult Result, TimeSpan? RetryAfter)> SendOnceAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode,
        string model,
        Stopwatch sw,
        CancellationToken ct)
    {
        // Model router may route a prompt to a reasoning model, so latency is bounded per
        // caller rather than globally: see AzureModelRouterOptions.FastTimeoutSeconds.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_options.ResolveTimeout(purpose));

        try
        {
            var payload = new ChatCompletionRequest
            {
                Model = model,
                Temperature = jsonMode ? 0 : 0.4,
                Messages = [.. messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content })],
                ResponseFormat = jsonMode ? new ResponseFormat { Type = "json_object" } : null
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BuildRequestUri())
            {
                Content = JsonContent.Create(payload)
            };

            await AuthenticateAsync(request, budget.Token);

            using var response = await http.SendAsync(request, budget.Token);

            // Until the body is read the router has not told us what it picked, so the
            // deployment name stands in. A failed call never gets that far: it is logged
            // with no model at all, which is what the console's "unresolved" bar counts.
            var resolvedModel = model;

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                // Deliberately excludes the response body, which may echo request data.
                logger.LogWarning(
                    "Azure Model Router request for {Purpose} failed with {Status} (deployment {Model}, request id {RequestId}).",
                    purpose, status, model, ReadRequestId(response) ?? "(none)");

                var failure = new AiCompletionResult(
                    false, string.Empty, DisplayName, string.Empty, sw.ElapsedMilliseconds,
                    $"Azure Model Router returned {status}.");

                return (failure, IsRetryable(status) ? ResolveRetryDelay(response) : null);
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: budget.Token);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;

            // The whole point of the router: this names the model it actually chose.
            if (!string.IsNullOrWhiteSpace(body?.Model))
            {
                resolvedModel = body.Model;
            }

            return (new AiCompletionResult(true, content, DisplayName, resolvedModel, sw.ElapsedMilliseconds), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation is not a router failure and must not be retried.
            logger.LogWarning("Azure Model Router request for {Purpose} was cancelled by the caller.", purpose);
            return (new AiCompletionResult(false, string.Empty, DisplayName, string.Empty, sw.ElapsedMilliseconds, "Canceled"), null);
        }
        catch (OperationCanceledException)
        {
            // Our own budget elapsed: the router was too slow for this caller, not wrong.
            logger.LogWarning("Azure Model Router request for {Purpose} exceeded its time budget.", purpose);
            return (new AiCompletionResult(false, string.Empty, DisplayName, string.Empty, sw.ElapsedMilliseconds, "Timeout"),
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or Azure.RequestFailedException)
        {
            logger.LogWarning("Azure Model Router request for {Purpose} failed: {Type}.", purpose, ex.GetType().Name);

            var result = new AiCompletionResult(
                false, string.Empty, DisplayName, string.Empty, sw.ElapsedMilliseconds, ex.GetType().Name);

            // A transport error is worth one more try; a malformed body or a rejected
            // credential is not.
            var retryable = ex is HttpRequestException;

            return (result, retryable ? TimeSpan.FromSeconds(1) : null);
        }
    }

    /// <summary>
    /// Applies the resource key, or an Entra ID bearer token when the deployment is
    /// configured for passwordless auth.
    /// </summary>
    private async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!_options.UseEntraId)
        {
            request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
            return;
        }

        var token = await GetTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow + TokenRefreshMargin)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } stillCached && stillCached.ExpiresOn > DateTimeOffset.UtcNow + TokenRefreshMargin)
            {
                return stillCached;
            }

            if (credential is null)
            {
                throw new InvalidOperationException(
                    "AzureModelRouter:UseEntraId is true but no TokenCredential is registered.");
            }

            var token = await credential.GetTokenAsync(new TokenRequestContext([_options.Scope]), ct);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>429 (throttled) and 5xx (upstream trouble) are worth another attempt; 4xx is not.</summary>
    private static bool IsRetryable(int status) => status == 429 || status >= 500;

    private TimeSpan ResolveRetryDelay(HttpResponseMessage response)
    {
        var max = TimeSpan.FromSeconds(Math.Max(_options.MaxRetryDelaySeconds, 0.5));
        var suggested = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } when
                ? when - DateTimeOffset.UtcNow
                : null);

        if (suggested is null || suggested <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return suggested.Value > max ? max : suggested.Value;
    }

    private static string? ReadRequestId(HttpResponseMessage response) =>
        ReadHeader(response, RequestIdHeader) ?? ReadHeader(response, AlternateRequestIdHeader);

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; set; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}

namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// Azure AI Foundry <c>model-router</c> settings. Endpoint, Deployment and ApiVersion are
/// public configuration; ApiKey must be supplied through User Secrets, environment
/// variables or Key Vault only — never committed.
///
/// Verified against the Microsoft Learn documentation
/// (https://learn.microsoft.com/azure/ai-foundry/openai/how-to/model-router):
/// - Model router is a single deployable model. You deploy <c>model-router</c> once and
///   do NOT deploy the underlying chat models separately.
/// - It is called through the ordinary Chat Completions API: set <c>model</c> to the
///   name of the model router deployment.
/// - The response <c>model</c> field reveals which underlying model was selected, which
///   is what this app records and shows on the console.
/// - <c>reasoning_effort</c> is not supported, and <c>temperature</c> / <c>top_p</c> are
///   ignored when the router picks an o-series model. Sending them is harmless.
/// </summary>
public sealed class AzureModelRouterOptions
{
    public const string SectionName = "AzureModelRouter";

    /// <summary>Purpose suffix marking a caller that has a hard deadline.</summary>
    public const string FastSuffix = "-fast";

    /// <summary>
    /// Resource endpoint, e.g. <c>https://my-foundry.openai.azure.com/</c>. The path is
    /// appended by <see cref="BuildRequestUri"/>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Resource key. Leave empty and set <see cref="UseEntraId"/> to authenticate
    /// passwordlessly with the app's managed identity / developer login instead.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Name of the model router deployment. Only one deployment is needed: the router
    /// selects the underlying model per request.
    /// </summary>
    public string Deployment { get; set; } = "model-router";

    /// <summary>
    /// Azure OpenAI API version. Left empty, the version-less <c>/openai/v1/</c> route is
    /// used instead, which takes the deployment name in the request body.
    /// </summary>
    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>
    /// True to authenticate with Entra ID (managed identity in Azure, developer login
    /// locally) rather than a key. Preferred in production: nothing to rotate or leak.
    /// </summary>
    public bool UseEntraId { get; set; }

    /// <summary>Token scope used when <see cref="UseEntraId"/> is true.</summary>
    public string Scope { get; set; } = "https://cognitiveservices.azure.com/.default";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Timeout applied to callers that cannot wait, selected by a purpose ending in
    /// <see cref="FastSuffix"/>.
    ///
    /// Model router decides per request how much capability a prompt needs, so a summary
    /// can legitimately be routed to a reasoning model that thinks for tens of seconds.
    /// The LINE webhook cancels an event after 8 seconds, so that path is given its own
    /// shorter budget: a fast answer that exists beats a better one that arrives after
    /// the caller has gone. Everything else keeps the full timeout.
    /// </summary>
    public int FastTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How many times a single completion is retried after a retryable failure
    /// (HTTP 429 or 5xx). 0 disables retrying. The <c>Retry-After</c> header is
    /// honoured when present, capped by <see cref="MaxRetryDelaySeconds"/>.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Upper bound applied to any server-suggested Retry-After delay.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 8;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Deployment)
        && (UseEntraId || !string.IsNullOrWhiteSpace(ApiKey));

    /// <summary>Endpoint normalised to a base address with a single trailing slash.</summary>
    public string BuildBaseAddress() => Endpoint.TrimEnd('/') + "/";

    /// <summary>
    /// Relative request path. With an API version the classic
    /// <c>openai/deployments/{deployment}/chat/completions</c> route is used; without one
    /// the newer version-less <c>openai/v1/chat/completions</c> route is used, where the
    /// deployment travels in the request body instead.
    /// </summary>
    public string BuildRequestUri() =>
        string.IsNullOrWhiteSpace(ApiVersion)
            ? "openai/v1/chat/completions"
            : $"openai/deployments/{Uri.EscapeDataString(Deployment)}/chat/completions?api-version={Uri.EscapeDataString(ApiVersion)}";

    /// <summary>
    /// Model sent in the request. Always the router deployment: choosing the model is
    /// the router's job, which is the entire reason this replaced a hand-pinned list.
    /// </summary>
    public string ResolveModel() => Deployment;

    /// <summary>Per-request budget: shorter for deadline-bound callers.</summary>
    public TimeSpan ResolveTimeout(string? purpose)
    {
        var seconds = purpose is not null
            && purpose.EndsWith(FastSuffix, StringComparison.OrdinalIgnoreCase)
            && FastTimeoutSeconds > 0
                ? Math.Min(FastTimeoutSeconds, TimeoutSeconds)
                : TimeoutSeconds;

        return TimeSpan.FromSeconds(Math.Max(seconds, 1));
    }
}

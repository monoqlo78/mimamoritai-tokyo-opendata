namespace MimamoriTai.Core.Abstractions;

/// <summary>Result of a single AI round trip, including router observability headers.</summary>
/// <param name="PromptTokens">
/// Tokens billed for the request, as reported by the service's <c>usage</c> block. Null
/// when the backend does not report usage (the mock router, or an error response). These
/// three counts are what make the cost of a prompt-shortening change measurable rather
/// than merely plausible; until they were captured, every token-reduction measure in this
/// app could only be argued for in the abstract.
/// </param>
/// <param name="CompletionTokens">Tokens billed for the generated text. Null when unreported.</param>
/// <param name="TotalTokens">Service-reported total. Null when unreported.</param>
public sealed record AiCompletionResult(
    bool Success,
    string Content,
    string Router,
    string ResolvedModel,
    long DurationMs,
    string? Error = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null);

public sealed record AiMessage(string Role, string Content)
{
    public static AiMessage System(string content) => new("system", content);
    public static AiMessage User(string content) => new("user", content);
    public static AiMessage Assistant(string content) => new("assistant", content);
}

/// <summary>
/// Chat completion abstraction. Backed by Azure AI Foundry's model router when an
/// endpoint is configured, otherwise by a deterministic mock so the whole app stays
/// demoable.
/// </summary>
public interface IAiRouterClient
{
    bool IsConfigured { get; }
    string DisplayName { get; }

    Task<AiCompletionResult> CompleteAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode = false,
        CancellationToken ct = default);
}

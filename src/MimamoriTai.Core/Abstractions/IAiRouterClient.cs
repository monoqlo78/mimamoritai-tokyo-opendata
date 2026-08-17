namespace MimamoriTai.Core.Abstractions;

/// <summary>Result of a single AI round trip, including router observability headers.</summary>
public sealed record AiCompletionResult(
    bool Success,
    string Content,
    string Router,
    string ResolvedModel,
    long DurationMs,
    string? Error = null);

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

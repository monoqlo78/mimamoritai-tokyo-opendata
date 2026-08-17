namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// One answer to a free-text question about the operator console.
///
/// <paramref name="Evidence"/> is the whole point of this record. It is the exact set
/// of figures the model was given, in the order it received them, so the operator can
/// read the answer and the numbers side by side. Without it a fluent paragraph is
/// indistinguishable from an invented one.
/// </summary>
public sealed record ConsoleAnswer(
    bool Success,
    string Answer,
    string Model,
    IReadOnlyList<string> Evidence,
    DateTimeOffset AnsweredAt,
    string? Error = null)
{
    public static ConsoleAnswer Failed(string error, DateTimeOffset at) =>
        new(false, string.Empty, string.Empty, [], at, error);
}

/// <summary>
/// Answers questions about every household at once, for the operator console.
///
/// Distinct from <see cref="ILocalDataQuestionService"/>, which answers about one
/// household for that family's own app. This one reads the same cross-household
/// rollup the console charts are drawn from, so an answer and the screen behind it
/// can never disagree.
/// </summary>
public interface IConsoleQuestionService
{
    /// <summary>False when no model router is configured, so callers can say so plainly.</summary>
    bool IsConfigured { get; }

    Task<ConsoleAnswer> AskAsync(string question, CancellationToken ct = default);
}

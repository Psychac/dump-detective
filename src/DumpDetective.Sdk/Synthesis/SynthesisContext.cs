namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Context handed to a rule alongside its matched observations.
/// </summary>
/// <remarks>
/// Deliberately minimal for this trimmed Phase 1 pass — a session-scoped concept (carrying e.g. a
/// session id, or a handle back to the full observation store for a rule that needs to look beyond
/// its own match set) is a Phase 4/5 concern, not yet built. Grows as those phases land rather than
/// being speculatively shaped now.
/// </remarks>
public sealed record SynthesisContext
{
    /// <summary>
    /// Names of the analyzers whose run failed — pipeline metadata, not a fact derived from the artifact, so it deliberately
    /// doesn't go through <see cref="Observations.Observation"/> (which exists to carry claims about the dump/trace, not about
    /// the tool's own execution). Read by <see cref="ICoverageRule"/>s; names only, because
    /// <c>DumpDetective.Core.Models.AnalyzerRunResult</c> is <c>internal</c> to a different assembly the SDK must not depend on.
    /// Empty when the caller has no run results to hand (e.g. most unit tests).
    /// </summary>
    public IReadOnlyList<string> FailedAnalyzers { get; init; } = [];
}

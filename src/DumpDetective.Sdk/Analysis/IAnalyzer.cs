namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Capability-scoped replacement for <c>Core.Abstractions.IAnalyzer</c> — same member shape,
/// retargeted to the SDK's <see cref="AnalysisContext"/>. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md for the migration this belongs
/// to; not yet implemented by any analyzer.
/// </summary>
/// <remarks>
/// <c>AnalyzeAsync</c> deliberately returns bare <see cref="ValueTask"/>, not
/// <c>ValueTask&lt;AnalyzerDomainResult&gt;</c> the way <c>Core.Abstractions.IAnalyzer</c> does.
/// <c>AnalyzerDomainResult</c> itself still lives in <c>Core.Models</c>, not the SDK, and every
/// analyzer's concrete result type derives from it and is consumed throughout
/// <c>Reporting</c> (section builders, trend comparers) — moving the base type is its own decision
/// (does <c>Core</c>'s type become a re-export of an SDK one, or does every consumer retarget too?)
/// that the pilot migration should force with a real answer, not one this first-cut interface should
/// guess at. Left unresolved on purpose rather than silently narrowing the analyzer contract.
/// </remarks>
public interface IAnalyzer : IDisposable
{
    string Name { get; }
    string Category { get; }
    IReadOnlyCollection<string> Tags => [];
    int Order => 0;
    bool IsThreadSafe => false;
    ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);

    /// <summary>Default no-op dispose so implementers remain source-compatible, matching
    /// <c>Core.Abstractions.IAnalyzer</c>'s own precedent.</summary>
    void IDisposable.Dispose() { }
}

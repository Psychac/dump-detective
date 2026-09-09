namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Capability-scoped replacement for <c>Core.Abstractions.IAnalyzer</c> — retargeted to the SDK's
/// <see cref="AnalysisContext"/>, and deliberately not the same member shape everywhere (see
/// <see cref="Category"/>'s own remarks for the one intentional divergence). See
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

    /// <summary>
    /// Required, with no default — an intentional divergence from
    /// <c>Core.Abstractions.IAnalyzer.Category</c>, which defaults to
    /// <c>AnalyzerCategory.Infer(Name)</c>: a nine-keyword substring match on the class name
    /// (`"memory"`, `"thread"`, `"handle"`, ... falling back to `"General"`) that most of today's 35
    /// analyzers actually fall through (`WcfChannelAnalyzer`, `SqlCommandAnalyzer`,
    /// `AsyncStateMachineAnalyzer`, `ObjectShapeAnalyzer`, `DominatorAnalyzer`, and more all land on
    /// `"General"`). <c>Category</c> is read directly off a live analyzer instance in 26 call sites
    /// today (CLI `--only`/`--tags` filtering, report section builders, trend composition, TOC
    /// sidebar grouping) — real, load-bearing data, populated by a heuristic that mostly doesn't
    /// categorize anything. Requiring every analyzer to state its own category explicitly, checked
    /// at compile time, replaces that coincidence detector rather than porting it forward. See
    /// docs/refactor/modularity/phase-1-sdk-review-findings.md item 1.
    /// </summary>
    string Category { get; }

    IReadOnlyCollection<string> Tags => [];
    int Order => 0;
    bool IsThreadSafe => false;
    ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);

    /// <summary>Default no-op dispose so implementers remain source-compatible, matching
    /// <c>Core.Abstractions.IAnalyzer</c>'s own precedent.</summary>
    void IDisposable.Dispose() { }
}

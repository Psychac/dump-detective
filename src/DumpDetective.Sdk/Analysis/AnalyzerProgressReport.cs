namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// A lightweight, source-agnostic progress snapshot an analyzer reports during a long-running scan.
/// Deliberately the same shape as <c>Core.Abstractions.AnalyzerProgressReport</c> and
/// <c>Platform.IndexProgress</c> — the SDK cannot reference <c>Core</c> at all (even for a type that
/// happens to be ClrMD-free, since <c>Core</c> carries the ClrMD package reference transitively), so
/// this is a third copy of the same shape by necessity, not by design choice; callers on the
/// legacy-adapter side (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) translate
/// between them.
/// </summary>
public sealed record AnalyzerProgressReport(
    long ScannedCount,
    string Phase,
    string? Detail = null,
    TimeSpan? Elapsed = null);

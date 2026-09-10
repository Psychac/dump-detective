namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// A lightweight, source-agnostic progress snapshot an analyzer reports during a long-running scan.
/// Deliberately the same shape as <c>Core.Abstractions.AnalyzerProgressReport</c> — the SDK cannot
/// reference <c>Core</c> at all (even for a type that happens to be ClrMD-free, since <c>Core</c>
/// carries the ClrMD package reference transitively), so this is a second copy of that shape by
/// necessity, not by design choice; callers on the legacy-adapter side
/// (<c>DiskBackedObjectIndexWriter.WrapForContainerProgress</c>) translate between them.
/// </summary>
/// <remarks>
/// Was a <em>third</em> copy until 2026-09-10 — <c>Platform.IndexProgress</c> duplicated this exact
/// shape because it predated this type (Phase 2's trimmed pass shipped it 2026-09-09, before this
/// SDK type existed to reference instead). Retired once this type existed and Platform's own
/// <c>ProjectReference</c> to the SDK (already required by
/// <c>PlatformProject_ShouldDependOnSdkOnly</c>) turned out to make that duplication newly
/// avoidable — see docs/refactor/modularity/phase-1-sdk-review-findings.md item 21 for the
/// consolidation and what's still left (this file remains a legitimate second copy; Core's original
/// cannot be retired the same way without Core losing its ClrMD dependency, which is out of scope
/// here).
/// </remarks>
public sealed record AnalyzerProgressReport(
    long ScannedCount,
    string Phase,
    string? Detail = null,
    TimeSpan? Elapsed = null);

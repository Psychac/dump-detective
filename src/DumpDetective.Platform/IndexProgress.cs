namespace DumpDetective.Platform;

/// <summary>
/// A lightweight, source-agnostic progress snapshot reported during index building — the shape
/// <c>IArtifactSource.IndexAsync</c> is expected to report through once it's added to the SDK (see
/// docs/refactor/modularity/source-model.md § 2), and what <c>Storage.Container.CacheContainerWriter</c>
/// reports through today. Deliberately the same shape as
/// <c>DumpDetective.Core.Abstractions.AnalyzerProgressReport</c> — Platform cannot reference Core
/// (zero deps beyond Sdk), so this is a separate type by necessity, not by design choice; callers on
/// the Analysis side adapt between the two.
/// </summary>
/// <param name="ScannedCount">Items processed so far. 0 is valid for phase-label-only reports.</param>
/// <param name="Phase">Human-readable current work phase.</param>
/// <param name="Detail">Optional supplementary detail.</param>
/// <param name="Elapsed">Optional elapsed time since the operation started.</param>
public sealed record IndexProgress(
    long ScannedCount,
    string Phase,
    string? Detail = null,
    TimeSpan? Elapsed = null);

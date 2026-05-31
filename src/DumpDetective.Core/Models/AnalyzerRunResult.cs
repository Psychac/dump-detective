namespace DumpDetective.Core.Models;

/// <summary>
/// Lightweight memory snapshot captured before and after a single analyzer run.
/// All values are sampled without forcing a GC collection to keep overhead minimal.
/// </summary>
internal sealed record AnalyzerMemoryStats(
    long WorkingSetBefore,
    long WorkingSetAfter,
    long ManagedHeapBefore,
    long ManagedHeapAfter)
{
    public long WorkingSetDelta => WorkingSetAfter - WorkingSetBefore;
    public long ManagedHeapDelta => ManagedHeapAfter - ManagedHeapBefore;
}

internal enum AnalyzerExecutionStatus
{
    Success,
    Failed,
    SkippedByFilter,
    SkippedByCancellation
}

internal sealed record AnalyzerExecutionDiagnostics(
    long ObjectScanCount,
    long CacheHits,
    long CacheMisses,
    AnalyzerMemoryStats? MemoryStats = null,
    string? FindingGeneratorError = null);

internal sealed record AnalyzerRunResult(
    string AnalyzerName,
    AnalyzerExecutionStatus Status,
    TimeSpan Duration,
    AnalyzerDomainResult? Result,
    string? ErrorMessage,
    string? ErrorType,
    string? SkipReason = null,
    IReadOnlyList<InsightFinding>? Findings = null,
    int FindingCount = 0,
    int WarningCount = 0,
    IReadOnlyList<ReportArtifact>? Artifacts = null,
    AnalyzerExecutionDiagnostics? Diagnostics = null)
{
    /// <summary>Generated findings for this run. Populated by <see cref="DumpDetective.Analysis.FindingGenerators"/> after the analyzer completes.</summary>
    public IReadOnlyList<InsightFinding> Findings { get; init; } = Findings ?? Array.Empty<InsightFinding>();
    public IReadOnlyList<ReportArtifact> Artifacts { get; init; } = Artifacts ?? Array.Empty<ReportArtifact>();

    /// <summary>
    /// Set when the <see cref="IFindingGenerator"/> for this analyzer threw during
    /// <see cref="DumpDetective.Analysis.Pipeline.FindingGenerationPipeline"/> execution.
    /// Non-null means findings may be incomplete. Surfaced as a Warning in the report and console.
    /// </summary>
    public string? FindingGeneratorError => Diagnostics?.FindingGeneratorError;

    /// <summary>
    /// Per-analyzer memory stats captured when <c>--memory-diagnostics</c> is enabled.
    /// Null when memory diagnostics are disabled (default).
    /// </summary>
    public AnalyzerMemoryStats? MemoryStats => Diagnostics?.MemoryStats;

    public long ObjectScanCount => Diagnostics?.ObjectScanCount ?? 0;
    public long CacheHits => Diagnostics?.CacheHits ?? 0;
    public long CacheMisses => Diagnostics?.CacheMisses ?? 0;
}

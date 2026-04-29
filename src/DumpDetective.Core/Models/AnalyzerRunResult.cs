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
    public long WorkingSetDelta   => WorkingSetAfter   - WorkingSetBefore;
    public long ManagedHeapDelta  => ManagedHeapAfter  - ManagedHeapBefore;
}

internal enum AnalyzerExecutionStatus
{
    Success,
    Failed,
    Skipped,
    Canceled
}

internal sealed record AnalyzerRunResult(
    string AnalyzerName,
    AnalyzerExecutionStatus Status,
    TimeSpan Duration,
    AnalyzerDomainResult? Result,
    string? ErrorMessage,
    string? ErrorType,
    IReadOnlyList<InsightFinding>? Findings = null,
    int FindingCount = 0,
    int WarningCount = 0,
    long ObjectScanCount = 0,
    long CacheHits = 0,
    long CacheMisses = 0,
    /// <summary>
    /// Set when the <see cref="IFindingGenerator"/> for this analyzer threw during
    /// <see cref="DumpDetective.Reporting.Pipeline.FindingGenerationPipeline"/> execution.
    /// Non-null means findings may be incomplete. Surfaced as a Warning in the report and console.
    /// </summary>
    string? FindingGeneratorError = null,
    /// <summary>
    /// Per-analyzer memory stats captured when <c>--memory-diagnostics</c> is enabled.
    /// Null when memory diagnostics are disabled (default).
    /// </summary>
    AnalyzerMemoryStats? MemoryStats = null)
{
    /// <summary>Generated findings for this run. Populated by <see cref="DumpDetective.Analysis.FindingGenerators"/> after the analyzer completes.</summary>
    public IReadOnlyList<InsightFinding> Findings { get; init; } = Findings ?? [];
}

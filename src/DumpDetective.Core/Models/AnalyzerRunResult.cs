namespace DumpDetective.Core.Models;

/// <summary>
/// Lightweight memory snapshot captured before and after a single analyzer run (or any timed scope).
/// All values are sampled without forcing a GC collection to keep overhead minimal.
///
/// <para><b>Which number answers which question:</b> use <see cref="AllocatedDelta"/> for "how much
/// memory did this cost" and <see cref="WorkingSetDelta"/> for "how much did this grow the process."
/// Do not use <see cref="ManagedHeapDelta"/> for either — see its own remarks.</para>
/// </summary>
internal sealed record AnalyzerMemoryStats(
    long WorkingSetBefore,
    long WorkingSetAfter,
    long ManagedHeapBefore,
    long ManagedHeapAfter,
    long AllocatedBefore,
    long AllocatedAfter)
{
    public long WorkingSetDelta => WorkingSetAfter - WorkingSetBefore;

    /// <summary>
    /// Total bytes allocated during the scope, from <c>GC.GetTotalAllocatedBytes</c>. Monotonic —
    /// collections never reduce it — so it attributes work to the code that performed it regardless of
    /// when the GC happens to run. This is the honest "what did this cost" metric.
    /// </summary>
    public long AllocatedDelta => AllocatedAfter - AllocatedBefore;

    /// <summary>
    /// ⚠️ <b>Net managed heap SIZE change, not memory used.</b> Almost never the number you want, and
    /// actively misleading for anything allocation-heavy: a scope's own allocations trigger gen2/LOH
    /// collections that reclaim *earlier* work's garbage, so this reads low or negative while the
    /// process commits hundreds of MB. Measured on a real dump, one analyzer reported -686 MB here
    /// while allocating 2.7 GB and growing the working set by 1.3 GB.
    ///
    /// Kept only because <see cref="ManagedHeapBefore"/>/<see cref="ManagedHeapAfter"/> are useful as
    /// absolute heap-size readings. Prefer <see cref="AllocatedDelta"/>. See
    /// docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 1.
    /// </summary>
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

/// <summary>
/// Post-hoc inter-analyzer result bus: a typed lookup over a completed run list.
/// Deliberately not exposed mid-run (via <see cref="DumpDetective.Core.Abstractions.AnalysisContext"/>) —
/// analyzers stay independent and order-agnostic during their own execution. Consumers (insight
/// correlation, evidence building, ranking) query this only after the full pipeline finishes.
/// </summary>
internal static class AnalyzerRunResultsExtensions
{
    /// <summary>Returns the first domain result of type <typeparamref name="T"/> in the run list, or null if none ran or produced one.</summary>
    public static T? GetResult<T>(this IReadOnlyList<AnalyzerRunResult> runs) where T : AnalyzerDomainResult
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is T typed)
                return typed;
        }
        return null;
    }
}

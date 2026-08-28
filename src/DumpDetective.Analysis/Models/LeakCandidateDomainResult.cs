using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

internal enum LeakClass
{
    StaticRetention,
    EventLeak,
    CacheLeak,
    ThreadLocalLeak,
    FinalizerRetention,
    GCHandleRetention,
    DependentHandleLeak,
    TimerLeak,
    Unknown
}

internal sealed record LeakCandidateRecord(
    string TypeName,
    ulong TotalSize,
    long InstanceCount,
    double Gen2Pct,
    int SuspicionScore,
    FindingSeverity Severity,
    LeakClass Classification,
    string? RootKind,
    bool IsFinalizable,
    bool IsContainer,
    double ReferenceFieldRatio,
    /// <summary>
    /// P3-1/P3-2 (docs/analysis/phase1/gcroot-analyzer-audit.md): genuine "why is this alive"
    /// chain from a GC root down to this candidate's sample object — either a direct hit against
    /// <see cref="GCRootDomainResult.TopRootsBySeverity"/> (the candidate's sample address is
    /// itself a recorded root target) or a bounded <c>RootPathFinder</c> BFS using the same
    /// reverse-reference index other analyzers already rely on. Computed only for the top-scored
    /// candidates (see <c>LeakCandidateAnalyzer.RootChainTopN</c>) — cosmetic enrichment only;
    /// <see langword="null"/> means unattempted or unreachable within search bounds, not "not
    /// leaked". Unlike <see cref="RootKind"/> (a heuristic classification guess), this is a
    /// verified chain when non-null.
    /// </summary>
    string? RootChain = null);

internal sealed record LeakCandidateDomainResult(
    int TotalCandidates,
    IReadOnlyList<LeakCandidateRecord> TopCandidates,
    IReadOnlyDictionary<LeakClass, int> CandidatesByClass,
    bool HeuristicOnly = true) : AnalyzerDomainResult;
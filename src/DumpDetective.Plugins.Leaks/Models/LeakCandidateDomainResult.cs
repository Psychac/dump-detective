using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Utilities;

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
    bool IsFinalizable,
    bool IsContainer,
    double ReferenceFieldRatio,
    /// <summary>Raw fact, not a derived judgment — a direct <c>IHeapRootQuery</c> membership
    /// check against this type's sample address. Feeds <c>Classify</c>'s composite classification
    /// but is itself measurement, not judgment; see <c>LeakCandidateAnalyzer.EmitObservationsAsync</c>'s
    /// own remarks (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.1).</summary>
    bool IsStaticRooted = false,
    /// <summary>Raw fact from <c>HeapTypeStatistics.IsDelegateType</c>, carried onto the record for
    /// the same reason as <see cref="IsStaticRooted"/> — added alongside it, phase 5 step 6.1.</summary>
    bool IsDelegate = false,
    /// <summary>Raw <c>HeapTypeStatistics.Gen2Count</c> — <see cref="Gen2Pct"/> alone can't
    /// reconstruct this exactly (it's a rounded percentage), and <c>Score</c>'s
    /// isFinalizable/Gen2Count&gt;1000 branch needs the exact count. Added alongside
    /// <see cref="IsStaticRooted"/>/<see cref="IsDelegate"/>, phase 5 step 6.1.</summary>
    long Gen2Count = 0,
    /// <summary>Raw fact: the type is a pinned-handle target in the GC Handle analyzer's result.</summary>
    bool IsPinned = false,
    /// <summary>Raw fact: the type is a dependent-handle target in the GC Handle analyzer's result.</summary>
    bool IsDependent = false,
    /// <summary>
    /// P3-1/P3-2 (docs/analysis/phase1/gcroot-analyzer-audit.md): genuine "why is this alive"
    /// chain from a GC root down to this candidate's sample object — either a direct hit against
    /// <see cref="GCRootDomainResult.TopRootsBySeverity"/> (the candidate's sample address is
    /// itself a recorded root target) or a bounded <c>RootPathFinder</c> BFS using the same
    /// reverse-reference index other analyzers already rely on. Computed only for the top-scored
    /// candidates (see <c>LeakCandidateAnalyzer.RootChainTopN</c>) — cosmetic enrichment only;
    /// <see langword="null"/> means unattempted or unreachable within search bounds, not "not
    /// leaked". Unlike <see cref="LeakCandidateAssessment.RootKind"/> (a heuristic classification guess), this is a
    /// verified chain when non-null. <see cref="RootChainSearch"/> says which of "unattempted", "unreachable" or "truncated" a null is.
    /// </summary>
    string? RootChain = null,
    /// <summary>What the root-chain search concluded for this candidate, so a null <see cref="RootChain"/> is never ambiguous.</summary>
    RootChainSearch RootChainSearch = RootChainSearch.NotSearched);

internal enum RootChainSearch
{
    /// <summary>Not attempted: beyond the top-scored candidates, or the type has no sample object to search from.</summary>
    NotSearched,

    /// <summary>A direct root hit or a path was found; <see cref="LeakCandidateRecord.RootChain"/> holds it.</summary>
    Found,

    /// <summary>Searched and confirmed unreachable within the search bounds.</summary>
    Unreachable,

    /// <summary>The search hit its bound before concluding either way.</summary>
    SearchTruncated,
}

internal sealed record LeakCandidateDomainResult(
    int TotalCandidates,
    IReadOnlyList<LeakCandidateRecord> TopCandidates,
    /// <summary>
    /// Raw input to <see cref="LeakCandidateClassifier"/>'s timer classification, from the Timer
    /// Leak analyzer's result — carried so consumers can recompute each candidate's assessment
    /// exactly, the same way <c>EventLeakDomainResult.ScoringWeights</c> does.
    /// </summary>
    int LogicalTimerCount,
    bool HeuristicOnly = true) : AnalyzerDomainResult;
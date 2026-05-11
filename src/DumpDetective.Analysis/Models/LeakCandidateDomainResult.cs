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
    Unknown
}

internal sealed record LeakCandidateRecord(
    string TypeName,
    ulong TotalSize,
    long InstanceCount,
    double Gen2Pct,
    int SuspicionScore,
    LeakClass Classification,
    string? RootKind,
    bool IsFinalizable,
    bool IsContainer,
    double ReferenceFieldRatio);

internal sealed record LeakCandidateDomainResult(
    int TotalCandidates,
    IReadOnlyList<LeakCandidateRecord> TopCandidates,
    IReadOnlyDictionary<LeakClass, int> CandidatesByClass,
    bool HeuristicOnly = true) : AnalyzerDomainResult;
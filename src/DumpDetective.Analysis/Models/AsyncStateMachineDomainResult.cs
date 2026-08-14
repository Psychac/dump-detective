using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// AsyncStateMachineAnalyzer domain models

internal sealed record StateMachineTypeProfile(
    string TypeName,
    string OriginatingMethod,
    string DeclaringType,
    int Count,
    ulong TotalBytes,
    int DominantState,
    IReadOnlyList<(int State, int Count)> StateDistribution,
    int ReferenceFieldCount,
    long Gen2Count,
    double Gen2Fraction,
    bool IsAsyncVoid);

internal sealed record HighCaptureStateMachine(
    ulong Address,
    string TypeName,
    ulong TotalCapturedRefBytes,
    IReadOnlyList<string> LargeCaptures);

internal sealed record SuspendedMethodEntry(
    string DeclaringType,
    string MethodName,
    int SuspendedCount,
    ulong TotalBytes);

internal sealed record AsyncStateMachineDomainResult(
    int TotalStateMachines,
    ulong TotalStateMachineBytes,
    IReadOnlyList<StateMachineTypeProfile> TopStateMachineTypes,
    IReadOnlyList<HighCaptureStateMachine> TopByCapturedSize,
    IReadOnlyList<SuspendedMethodEntry> SuspendedMethodMap,
    bool ScanLimited,
    // Summed over ALL candidate types (bounded by TypeCandidateLimit), not just
    // TopStateMachineTypes (bounded by TopTypeLimit) — keeps this consistent with
    // TotalStateMachines as a denominator for Gen2 fraction (see AsyncStateMachineTrendComparer).
    long TotalGen2Count = 0,
    // When ScanLimited=true, these quantify truncation impact: how many types were skipped
    // and what fraction of total bytes they represent (vs. analyzed types).
    int SkippedTypeCount = 0,
    double SkippedBytesFraction = 0.0) : AnalyzerDomainResult;

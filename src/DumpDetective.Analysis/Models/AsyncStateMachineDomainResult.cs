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
    // Summed over all detected state-machine types, same population as TopStateMachineTypes,
    // so it's a consistent denominator for Gen2 fraction (see AsyncStateMachineTrendComparer).
    long TotalGen2Count = 0) : AnalyzerDomainResult;

using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// AsyncStateMachineAnalyzer domain models

internal sealed record StateMachineTypeProfile(
    string TypeName,
    string OriginatingMethod,
    string DeclaringType,
    int Count,
    ulong TotalBytes,
    int AvgStateValue,
    int ReferenceFieldCount,
    long Gen2Count,
    double Gen2Fraction);

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
    bool ScanLimited) : AnalyzerDomainResult;

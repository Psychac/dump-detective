using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Async Task

internal sealed record OrphanedTaskSnapshot(
    ulong Address,
    string TaskType,
    string? ResultType,
    ulong Size,
    string? ExceptionType = null,
    string? ExceptionMessage = null);

internal sealed record ContinuationChainSnapshot(
    ulong RootAddress,
    string RootType,
    int Depth,
    IReadOnlyList<string> ChainTypes);

internal sealed record FaultedTaskTypeProfile(
    string TaskType,
    int TotalCount,
    IReadOnlyList<NameCountEntry> ExceptionTypes);

internal sealed record NameSizeCountEntry(
    string Name,
    long TotalBytes,
    int Count);

// A TaskCompletionSource<T> whose inner Task was still non-terminal (Pending/Running) at dump
// time — either a normal in-flight promise, or a leaked one nobody will ever resolve. Generation
// (of the TCS object itself, not the inner task) is the leak-strength proxy: a fresh in-flight
// TCS and a genuinely stuck one look identical at the instant of the dump, but only the latter
// survives multiple GC cycles into Gen2/LOH.
internal sealed record UnresolvedTcsSnapshot(
    ulong Address,
    string TypeName,
    ulong Size,
    int Generation);

// An IValueTaskSource implementer (built on ManualResetValueTaskSourceCore<T>, see
// ValueTaskSourcePattern) whose embedded core's _completed flag was still false at dump time.
// Same leak-strength caveat as UnresolvedTcsSnapshot: only Gen2/LOH residency distinguishes a
// genuinely stuck source from one that just hasn't completed its (usually fast) operation yet.
internal sealed record PendingValueTaskSourceSnapshot(
    ulong Address,
    string TypeName,
    ulong Size,
    int Generation);

internal sealed record AsyncTaskDomainResult(
    int TotalTasks,
    int PendingTasks,
    int RunningTasks,
    int FaultedTasks,
    int CanceledTasks,
    int CompletedTasks,
    int OrphanedTasks,
    int TotalTaskContinuations,
    int MaxContinuationDepth,
    double AvgContinuationDepth,
    IReadOnlyList<NameCountEntry> TopPendingTaskTypes,
    IReadOnlyList<NameCountEntry> TopFaultedTaskTypes,
    IReadOnlyList<NameCountEntry> TopContinuationTypes,
    IReadOnlyList<OrphanedTaskSnapshot> TopOrphanedTasks,
    IReadOnlyList<ContinuationChainSnapshot> TopDeepestChains,
    IReadOnlyList<FaultedTaskTypeProfile> FaultedTaskExceptionHistograms = default!,
    int MultiContinuationNodeCount = 0,
    int MaxContinuationFanOut = 0,
    IReadOnlyList<NameCountEntry>? TopContinuationFanoutTypes = default,
    int DepthSampleCount = 0,
    bool CycleDetected = false,
    int PendingGen0 = 0,
    int PendingGen1 = 0,
    int PendingGen2 = 0,
    int PendingLOH = 0,
    IReadOnlyList<NameSizeCountEntry>? TopPendingTaskTypesByBytes = default,
    int TotalTaskCompletionSources = 0,
    int UnresolvedTaskCompletionSources = 0,
    int UnresolvedTcsGen2Count = 0,
    IReadOnlyList<UnresolvedTcsSnapshot>? TopUnresolvedTaskCompletionSources = default,
    int TotalValueTaskSources = 0,
    int PendingValueTaskSources = 0,
    int PendingVtsGen2Count = 0,
    IReadOnlyList<PendingValueTaskSourceSnapshot>? TopPendingValueTaskSources = default) : AnalyzerDomainResult;

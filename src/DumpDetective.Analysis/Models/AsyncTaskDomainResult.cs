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
    bool TaskScanLimited,
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
    bool CycleDetected = false) : AnalyzerDomainResult;

using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Async Task

internal sealed record OrphanedTaskSnapshot(ulong Address, string TaskType, string? ResultType, ulong Size);

internal sealed record ContinuationChainSnapshot(
    ulong RootAddress,
    string RootType,
    int Depth,
    IReadOnlyList<string> ChainTypes);

internal sealed record AsyncTaskDomainResult(
    int TotalTasks,
    int PendingTasks,
    int RunningTasks,
    int FaultedTasks,
    int CanceledTasks,
    int CompletedTasks,
    int OrphanedTasks,
    int MaxContinuationDepth,
    double AvgContinuationDepth,
    bool TaskScanLimited,
    IReadOnlyList<NameCountEntry> TopPendingTaskTypes,
    IReadOnlyList<NameCountEntry> TopFaultedTaskTypes,
    IReadOnlyList<NameCountEntry> TopContinuationTypes,
    IReadOnlyList<OrphanedTaskSnapshot> TopOrphanedTasks,
    IReadOnlyList<ContinuationChainSnapshot> TopDeepestChains) : AnalyzerDomainResult;

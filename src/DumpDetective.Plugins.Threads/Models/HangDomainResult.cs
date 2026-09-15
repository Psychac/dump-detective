using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Hang

internal sealed record HangDomainResult(
    int TotalAliveThreads,
    int WaitingThreadCount,
    int ThreadsHoldingLocks,
    double WaitingPercent,
    IReadOnlyDictionary<string, int> WaitCategoryBreakdown,
    int TotalTaskContinuations,
    int QueuedWorkItems,
    int TotalTasks,
    int PendingTasks,
    int FaultedTasks,
    int CanceledTasks,
    bool RuntimeThreadPoolDataAvailable,
    int RuntimeMinThreads,
    int RuntimeMaxThreads,
    int RuntimeActiveWorkerThreads,
    int RuntimeIdleWorkerThreads,
    int RuntimeRetiredWorkerThreads,
    int? RuntimeQueueLength,
    int RuntimeCpuUtilization,
    bool IsStarved,
    int HealthScore,
    IReadOnlyList<WaitingThreadSnapshot>? TopWaitingThreads = null,
    IReadOnlyList<NameCountEntry>? TopContinuationTypes = null) : AnalyzerDomainResult;

internal sealed record WaitingThreadSnapshot(
    uint ThreadId,
    uint OSThreadId,
    string WaitType,
    string WaitReason,
    int LockCount,
    string TopStackFrame);

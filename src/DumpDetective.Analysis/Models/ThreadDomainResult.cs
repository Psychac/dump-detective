using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Threads

internal sealed record ThreadDomainResult(
    int TotalThreadCount,
    int AliveThreadCount,
    int InactiveThreadCount,
    int GcThreadCount,
    int BlockedThreadCount,
    int LockHoldingThreadCount,
    int ThreadsWithActiveExceptionsCount,
    int BackgroundThreadCount,
    IReadOnlyDictionary<string, int> WaitPatternBreakdown,
    IReadOnlyDictionary<string, int>? ThreadStateDistribution = null,
    IReadOnlyDictionary<string, int>? AppDomainDistribution = null,
    IReadOnlyDictionary<string, int>? GcModeDistribution = null,
    IReadOnlyList<ThreadStateSnapshot>? TopLockedThreads = null,
    IReadOnlyList<ThreadStateSnapshot>? TopBlockedThreads = null,
    IReadOnlyList<ThreadExceptionSnapshot>? ThreadsWithActiveExceptions = null,
    IReadOnlyList<NameCountEntry>? TopStackHotspots = null,
    IReadOnlyList<NameCountEntry>? TopActiveThreadHotspots = null,
    int ThreadPoolWorkerCount = 0,
    int FinalizerThreadCount = 0,
    bool FinalizerThreadBlocked = false,
    uint? FinalizerManagedThreadId = null,
    uint? FinalizerOsThreadId = null,
    int FinalizerLockCount = 0,
    IReadOnlyList<string>? FinalizerFrames = null,
    int AsyncChainThreadCount = 0,
    int MaxAsyncChainDepth = 0) : AnalyzerDomainResult;

internal sealed record ThreadStateSnapshot(
    uint ThreadId,
    uint OSThreadId,
    int LockCount,
    string ThreadState,
    string GcMode,
    string? WaitCategory,
    string? WaitReason,
    IReadOnlyList<string> TopFrames,
    int StackRootCount);

internal sealed record ThreadExceptionSnapshot(
    uint ThreadId,
    uint OSThreadId,
    string ExceptionType,
    string? ExceptionMessage,
    string ThreadState,
    string GcMode,
    int LockCount,
    IReadOnlyList<string> TopFrames,
    int StackRootCount);

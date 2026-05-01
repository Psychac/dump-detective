using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Lock Graph

internal sealed record DeadlockCandidateSnapshot(
    uint ManagedThreadId,
    uint OsThreadId,
    IReadOnlyList<string> LockObjectTypes,
    string CycleSummary);

internal sealed record ContestedLockSnapshot(
    ulong ObjectAddress,
    string ObjectTypeName,
    int WaitingThreadCount,
    uint? OwnerManagedThreadId,
    int RecursionCount);

internal sealed record LockGraphDomainResult(
    int TotalHeldLocks,
    int ContestedLockCount,
    int MaxWaitersOnSingleLock,
    int DeadlockCandidateCount,
    IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null,
    IReadOnlyList<DeadlockCandidateSnapshot>? DeadlockCandidateDetails = null,
    IReadOnlyList<ContestedLockSnapshot>? ContestedLockDetails = null) : AnalyzerDomainResult;

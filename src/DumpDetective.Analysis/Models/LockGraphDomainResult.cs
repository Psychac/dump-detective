using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Lock Graph

internal sealed record DeadlockCandidateSnapshot(
    uint ManagedThreadId,
    uint OsThreadId,
    IReadOnlyList<string> LockObjectTypes,
    IReadOnlyList<ulong> LockObjectAddresses,
    string BlockedAtFrame,
    IReadOnlyList<string> OwnerThreadFrames = null!);

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
    int UnresolvedOwnerCount = 0,
    int LocksWithOwnerAddress = 0,
    IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null,
    IReadOnlyList<DeadlockCandidateSnapshot>? DeadlockCandidateDetails = null,
    IReadOnlyList<ContestedLockSnapshot>? ContestedLockDetails = null) : AnalyzerDomainResult;

internal static class LockGraphDomainResultExtensions
{
    public static double CalculateOwnerResolutionConfidence(this LockGraphDomainResult result)
    {
        if (result.LocksWithOwnerAddress == 0)
            return 0.85;
        double resolutionRate = (double)(result.LocksWithOwnerAddress - result.UnresolvedOwnerCount) / result.LocksWithOwnerAddress;
        return 0.5 + (resolutionRate * 0.35);
    }
}

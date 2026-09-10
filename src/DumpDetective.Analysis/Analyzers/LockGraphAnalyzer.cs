using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch, thread-domain quartet item 1
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md): retyped onto the SDK's
/// capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing sync blocks through
/// <see cref="IHeapSyncBlockQuery"/> (<c>runtime.locks</c>), object type names through
/// <see cref="IHeapObjectLookup"/>, and threads/stack frames through <see cref="IRuntimeThreadQuery"/>.
/// Runs through the existing pipeline via <see cref="LockGraphAnalyzerLegacyAdapter"/>, which also
/// carries the <c>IThreadStackScanParticipant</c> implementation this analyzer's stack-frame reads
/// rely on the pipeline sharing across the whole thread-domain quartet — see that adapter's own
/// remarks and the plan doc's § 3 for why that lives on the adapter, not here.
/// </summary>
/// <remarks>
/// Re-verified during this retyping (not just grepped, per this project's own convention): this
/// analyzer reads no live object field values anywhere — <c>heap.EnumerateSyncBlocks()</c>,
/// per-address type-name resolution, and thread/frame facts are all coarse/structural. It was
/// originally miscategorized as Tier 1 + Tier 2 in the plan's own grep-derived split; corrected
/// there once this was confirmed.
/// </remarks>
public sealed class LockGraphAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    // A thread physically blocked inside Monitor.Enter/Wait has one or more native "Runtime"
    // transition frames (no Method, empty signature) ahead of the actual managed
    // Monitor.Enter_Slowpath/ObjWait frame — verified via a live self-attach snapshot of a real
    // blocked thread. Frame index 0 alone is almost always one of those native frames, so this
    // scans up to FrameScanDepth frames for the first one with a resolvable Method signature.
    internal const int FrameScanDepth = 8;

    // How many of a deadlock candidate's own resolvable frames to capture for the report. Reuses
    // the same up-to-FrameScanDepth frame set the pipeline's shared scan already captured rather
    // than a second independent walk (the pre-retyping analyzer's own CaptureOwnerThreadFrames did
    // walk again, unbounded) — accepted, documented narrowing: if fewer than 3 of a candidate's
    // first 8 frames resolve to a method signature, this returns fewer than 3 instead of continuing
    // to walk further down the stack. Deadlock candidates are inherently rare and FrameScanDepth
    // was already chosen as "enough to find one resolvable frame in virtually all real stacks", so
    // finding 3 within the same 8 is the same bet, not a new one.
    private const int OwnerFrameCaptureCount = 3;

    public string Name => "Lock Graph Analysis";
    public string Category => "Locks";

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapSyncBlockQuery syncBlockQuery = context.HeapSyncBlocks
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeLocks}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        IRuntimeThreadQuery threadQuery = context.RuntimeThreads
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

        LastResult = Analyze(syncBlockQuery, objectLookup, threadQuery, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private readonly record struct LockEntry(ulong ObjectAddress, string TypeName, RuntimeThreadRef? Owner, int RecursionCount, int WaitingThreadCount);

    private static LockGraphDomainResult Analyze(
        IHeapSyncBlockQuery syncBlockQuery,
        IHeapObjectLookup objectLookup,
        IRuntimeThreadQuery threadQuery,
        CancellationToken cancellationToken)
    {
        var threadsByOsId = new Dictionary<uint, RuntimeThreadRef>();
        foreach (RuntimeThreadRef t in threadQuery.EnumerateThreads())
            threadsByOsId[t.Thread.OsThreadId] = t;

        var allHeldLocks = new List<LockEntry>();
        var contestedLocks = new List<LockEntry>();
        int unresolvedOwnerCount = 0;
        int locksWithOwnerAddress = 0;

        foreach (HeapSyncBlockRef sb in syncBlockQuery.EnumerateSyncBlocks())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!sb.IsMonitorHeld || sb.ObjectAddress == 0)
                continue;

            string typeName = objectLookup.TryGetObject(sb.ObjectAddress, out HeapObjectRef obj) ? obj.TypeDisplayName : "Unknown";

            if (sb.HasHoldingThread)
                locksWithOwnerAddress++;

            RuntimeThreadRef? owner = sb.HoldingOsThreadId is uint osThreadId && threadsByOsId.TryGetValue(osThreadId, out RuntimeThreadRef ownerRef)
                ? ownerRef
                : null;

            if (owner is null && sb.HasHoldingThread)
                unresolvedOwnerCount++;

            var entry = new LockEntry(sb.ObjectAddress, typeName, owner, sb.RecursionCount, sb.WaitingThreadCount);
            allHeldLocks.Add(entry);

            if (sb.WaitingThreadCount > 0)
                contestedLocks.Add(entry);
        }

        contestedLocks.Sort((a, b) => b.WaitingThreadCount.CompareTo(a.WaitingThreadCount));

        // Pre-build lock-by-owner-thread map to avoid O(M×N) lookup in the deadlock scan below.
        var locksByOwnerManagedId = new Dictionary<int, List<LockEntry>>();
        foreach (LockEntry lockEntry in allHeldLocks)
        {
            if (lockEntry.Owner is RuntimeThreadRef owner && owner.Thread.ManagedThreadId is int managedId)
            {
                if (!locksByOwnerManagedId.TryGetValue(managedId, out List<LockEntry>? lockList))
                {
                    lockList = new List<LockEntry>();
                    locksByOwnerManagedId[managedId] = lockList;
                }
                lockList.Add(lockEntry);
            }
        }

        // Deadlock candidates: threads that own at least one inflated lock AND are blocked on a monitor.
        var ownerManagedIds = new HashSet<int>(locksByOwnerManagedId.Keys);
        var deadlockCandidates = new List<DeadlockCandidateSnapshot>();

        foreach (RuntimeThreadRef thread in threadsByOsId.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!thread.IsAlive || thread.LockCount == 0)
                continue;
            if (thread.Thread.ManagedThreadId is not int managedThreadId || !ownerManagedIds.Contains(managedThreadId))
                continue;

            string? topFrameSignature = null;
            int scanned = 0;
            foreach (ThreadStackFrameRef frame in threadQuery.EnumerateStackFrames(thread))
            {
                if (frame.HasMethod && !string.IsNullOrEmpty(frame.MethodDisplayName))
                {
                    topFrameSignature = frame.MethodDisplayName;
                    break;
                }
                if (++scanned >= FrameScanDepth)
                    break;
            }

            if (topFrameSignature is null)
                continue;
            if (!topFrameSignature.Contains("monitor.wait", StringComparison.OrdinalIgnoreCase) &&
                !topFrameSignature.Contains("monitor.enter", StringComparison.OrdinalIgnoreCase))
                continue;

            List<LockEntry> locksHeld = locksByOwnerManagedId.TryGetValue(managedThreadId, out List<LockEntry>? locks) ? locks : [];
            var lockTypes = new List<string>(locksHeld.Count);
            var lockAddresses = new List<ulong>(locksHeld.Count);
            foreach (LockEntry lh in locksHeld)
            {
                lockTypes.Add(lh.TypeName);
                lockAddresses.Add(lh.ObjectAddress);
            }

            string summary = $"Thread {managedThreadId} (OS: {thread.Thread.OsThreadId}) holds {locksHeld.Count} lock(s), blocked at: {topFrameSignature}";
            List<string> ownerFrames = CaptureFrames(threadQuery, thread, OwnerFrameCaptureCount);

            deadlockCandidates.Add(new DeadlockCandidateSnapshot(
                (uint)managedThreadId, thread.Thread.OsThreadId, lockTypes, lockAddresses, summary, ownerFrames));
        }

        var typeWaiters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LockEntry cl in contestedLocks)
            typeWaiters[cl.TypeName] = typeWaiters.TryGetValue(cl.TypeName, out int existing) ? existing + cl.WaitingThreadCount : cl.WaitingThreadCount;
        var topContestedTypes = new List<NameCountEntry>(typeWaiters.Count);
        foreach (KeyValuePair<string, int> kvp in typeWaiters)
            topContestedTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));
        topContestedTypes.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var contestedLockDetails = new List<ContestedLockSnapshot>(contestedLocks.Count);
        foreach (LockEntry cl in contestedLocks)
        {
            uint? ownerManagedId = cl.Owner is RuntimeThreadRef owner && owner.Thread.ManagedThreadId is int mid ? (uint)mid : null;
            contestedLockDetails.Add(new ContestedLockSnapshot(cl.ObjectAddress, cl.TypeName, cl.WaitingThreadCount, ownerManagedId, cl.RecursionCount));
        }

        return new LockGraphDomainResult(
            allHeldLocks.Count,
            contestedLocks.Count,
            contestedLocks.Count > 0 ? contestedLocks[0].WaitingThreadCount : 0,
            deadlockCandidates.Count,
            unresolvedOwnerCount,
            locksWithOwnerAddress,
            topContestedTypes,
            deadlockCandidates,
            contestedLockDetails);
    }

    private static List<string> CaptureFrames(IRuntimeThreadQuery threadQuery, RuntimeThreadRef thread, int maxFrames)
    {
        var frames = new List<string>(maxFrames);
        if (!thread.IsAlive)
            return frames;

        foreach (ThreadStackFrameRef frame in threadQuery.EnumerateStackFrames(thread))
        {
            if (frames.Count >= maxFrames)
                break;
            if (frame.HasMethod && !string.IsNullOrEmpty(frame.MethodDisplayName))
                frames.Add(frame.MethodDisplayName);
        }
        return frames;
    }

    public void Dispose() { }
}

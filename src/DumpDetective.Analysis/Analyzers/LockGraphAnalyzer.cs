using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class LockGraphAnalyzer : IAnalyzer, IThreadStackScanParticipant
    {
        public string Name => "Lock Graph Analysis";
        public string Category => "Locks";

        // Instance accumulator state for the IThreadStackScanParticipant path — only the top few
        // frames are needed (deadlock-candidate detection is a single monitor.wait/monitor.enter
        // check), so this shares ThreadStackScanDispatcher's single EnumerateStackTrace() pass
        // with ThreadAnalyzer/HangAnalyzer/ThreadStackClusterAnalyzer instead of independently
        // walking stacks for the (typically small) set of lock-holding threads.
        //
        // A thread physically blocked inside Monitor.Enter/Wait has one or more native "Runtime"
        // transition frames (no Method, empty signature) ahead of the actual managed
        // Monitor.Enter_Slowpath/ObjWait frame — verified via a live self-attach snapshot of a real
        // blocked thread. Frame index 0 alone is almost always one of those native frames, so this
        // scans up to FrameScanDepth frames for the first one with a resolvable Method signature.
        private const int FrameScanDepth = 8;

        private Dictionary<ulong, string?>? _participantTopFrameSignatureByThreadAddress;
        private bool _participantScanSucceeded;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public int GetRequiredFrameCount(AnalysisContext context) => FrameScanDepth;

        public void BeforeThreadStackScan(AnalysisContext context)
        {
            _participantTopFrameSignatureByThreadAddress = new Dictionary<ulong, string?>();
        }

        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => OnThreadStack(in snapshot);

        private void OnThreadStack(in ThreadStackSnapshot snapshot)
        {
            ClrThread thread = snapshot.Thread;
            if (!thread.IsAlive || thread.LockCount == 0 || thread.Address == 0)
                return;

            string? signature = null;
            foreach (ClrStackFrame frame in snapshot.TopFrames)
            {
                if (frame.Method?.Signature is { Length: > 0 } sig)
                {
                    signature = sig;
                    break;
                }
            }

            _participantTopFrameSignatureByThreadAddress![thread.Address] = signature;
        }

        public void OnThreadStackScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, cache: null, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var graph = BuildLockGraph(runtime, heap, cache, progress);

            var typeWaiters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cl in graph.ContestedLocks)
            {
                typeWaiters.TryGetValue(cl.ObjectTypeName, out int existing);
                typeWaiters[cl.ObjectTypeName] = existing + cl.WaitingThreadCount;
            }
            var topContestedTypes = new List<NameCountEntry>(typeWaiters.Count);
            foreach (var kvp in typeWaiters)
                topContestedTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));
            topContestedTypes.Sort(static (a, b) => b.Count.CompareTo(a.Count));

            var contestedLockDetails = new List<ContestedLockSnapshot>(graph.ContestedLocks.Count);
            foreach (var cl in graph.ContestedLocks)
            {
                uint? ownerManagedId = cl.OwnerThread != null ? (uint)cl.OwnerThread.ManagedThreadId : null;
                contestedLockDetails.Add(new ContestedLockSnapshot(
                    cl.ObjectAddress,
                    cl.ObjectTypeName,
                    cl.WaitingThreadCount,
                    ownerManagedId,
                    cl.RecursionCount));
            }

            var deadlockDetails = new List<DeadlockCandidateSnapshot>(graph.DeadlockCandidates.Count);
            foreach (var dc in graph.DeadlockCandidates)
            {
                var lockTypes = new List<string>(dc.LocksHeld.Count);
                var lockAddresses = new List<ulong>(dc.LocksHeld.Count);
                foreach (var lh in dc.LocksHeld)
                {
                    lockTypes.Add(lh.ObjectTypeName);
                    lockAddresses.Add(lh.ObjectAddress);
                }

                string summary = $"Thread {dc.Thread.ManagedThreadId} (OS: {dc.Thread.OSThreadId}) holds {dc.LocksHeld.Count} lock(s), blocked at: {dc.TopFrame}";

                var ownerFrames = CaptureOwnerThreadFrames(dc.Thread, maxFrames: 3);

                deadlockDetails.Add(new DeadlockCandidateSnapshot(
                    (uint)dc.Thread.ManagedThreadId,
                    (uint)dc.Thread.OSThreadId,
                    lockTypes,
                    lockAddresses,
                    summary,
                    ownerFrames));
            }

            return new LockGraphDomainResult(
                    graph.AllHeldLocks.Count,
                    graph.ContestedLocks.Count,
                    graph.ContestedLocks.Count > 0 ? graph.ContestedLocks[0].WaitingThreadCount : 0,
                    graph.DeadlockCandidates.Count,
                    graph.UnresolvedOwnerCount,
                    graph.LocksWithOwnerAddress,
                    topContestedTypes,
                    deadlockDetails,
                    contestedLockDetails);
        }

        private LockGraphAnalysis BuildLockGraph(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var result = new LockGraphAnalysis();

            // Build thread-address â†’ ClrThread lookup for owner resolution
            var threadByAddress = new Dictionary<ulong, ClrThread>();
            var threads = new List<ClrThread>(runtime.Threads);
            foreach (var t in threads)
            {
                if (t.Address != 0)
                    threadByAddress[t.Address] = t;
            }
            result.ThreadByAddress = threadByAddress;

            var scanCounter = new ObjectScanCounter("scanning sync blocks", progress, reportEveryObjects: 50);
            foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
            {
                scanCounter.Tick();

                if (!sb.IsMonitorHeld || sb.Object == 0)
                    continue;

                string typeName = ResolveTypeNameByAddress(heap, cache, sb.Object);

                if (sb.HoldingThreadAddress != 0)
                    result.LocksWithOwnerAddress++;

                bool ownerResolved = threadByAddress.TryGetValue(sb.HoldingThreadAddress, out ClrThread? ownerThread);
                if (!ownerResolved && sb.HoldingThreadAddress != 0)
                    result.UnresolvedOwnerCount++;

                var entry = new LockContention
                {
                    ObjectAddress = sb.Object,
                    ObjectTypeName = typeName,
                    OwnerThread = ownerThread,
                    HoldingThreadAddress = sb.HoldingThreadAddress,
                    RecursionCount = sb.RecursionCount,
                    WaitingThreadCount = sb.WaitingThreadCount
                };

                result.AllHeldLocks.Add(entry);

                if (sb.WaitingThreadCount > 0)
                    result.ContestedLocks.Add(entry);
            }
            scanCounter.Complete();

            result.ContestedLocks.Sort((a, b) => b.WaitingThreadCount.CompareTo(a.WaitingThreadCount));

            // Pre-build lock-by-owner-thread map to avoid O(M×N) lookup in the loop below
            var locksByOwnerManagedId = new Dictionary<int, List<LockContention>>();
            foreach (var lockEntry in result.AllHeldLocks)
            {
                if (lockEntry.OwnerThread != null)
                {
                    if (!locksByOwnerManagedId.TryGetValue(lockEntry.OwnerThread.ManagedThreadId, out var lockList))
                    {
                        lockList = new List<LockContention>();
                        locksByOwnerManagedId[lockEntry.OwnerThread.ManagedThreadId] = lockList;
                    }
                    lockList.Add(lockEntry);
                }
            }

            // Deadlock candidates: threads that own at least one inflated lock AND are blocked on a monitor
            var ownerManagedIds = new HashSet<int>(locksByOwnerManagedId.Keys);

            foreach (var thread in threads)
            {
                if (!thread.IsAlive || thread.LockCount == 0) continue;
                if (!ownerManagedIds.Contains(thread.ManagedThreadId)) continue;

                string? topFrameSignature;
                if (_participantScanSucceeded)
                {
                    // BeforeThreadStackScan/OnThreadStack already ran via the pipeline's
                    // ThreadStackScanDispatcher — read back the captured top frame instead of a
                    // second independent EnumerateStackTrace() walk.
                    _participantTopFrameSignatureByThreadAddress!.TryGetValue(thread.Address, out topFrameSignature);
                }
                else
                {
                    // Fallback (non-participant) path: used when this analyzer is invoked
                    // directly (tests, benchmarks) instead of through AnalysisPipeline's
                    // dispatcher.
                    topFrameSignature = null;
                    int scanned = 0;
                    foreach (var frame in thread.EnumerateStackTrace())
                    {
                        if (frame.Method?.Signature is { Length: > 0 } sig)
                        {
                            topFrameSignature = sig;
                            break;
                        }
                        if (++scanned >= FrameScanDepth)
                            break;
                    }
                }

                if (topFrameSignature == null) continue;

                if (!topFrameSignature.Contains("monitor.wait", StringComparison.OrdinalIgnoreCase) &&
                    !topFrameSignature.Contains("monitor.enter", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.DeadlockCandidates.Add(new DeadlockCandidate
                {
                    Thread = thread,
                    TopFrame = topFrameSignature,
                    LocksHeld = locksByOwnerManagedId.TryGetValue(thread.ManagedThreadId, out var locks) ? locks : []
                });
            }

            return result;
        }

        private static string ResolveTypeNameByAddress(ClrHeap heap, IHeapAnalysisCache? cache, ulong objectAddress)
        {
            if (objectAddress == 0)
                return StringConstants.UnknownType;

            // OPT (docs/cache/cache-architecture.md Phase 6): address-only caller (from
            // sync-block enumeration) — resolve via the disk-backed address index when available.
            if (cache is not null)
            {
                return cache.TryGetObjectMetadata(heap, objectAddress, out ulong methodTable, out _) && methodTable != 0
                    ? heap.GetTypeByMethodTable(methodTable)?.Name ?? StringConstants.UnknownType
                    : StringConstants.UnknownType;
            }

            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid || obj.Type == null)
                return StringConstants.UnknownType;

            return obj.Type.Name ?? StringConstants.UnknownType;
        }

        private static IReadOnlyList<string> CaptureOwnerThreadFrames(ClrThread thread, int maxFrames)
        {
            var frames = new List<string>(maxFrames);
            if (thread == null || !thread.IsAlive)
                return frames;

            int frameCount = 0;
            foreach (var frame in thread.EnumerateStackTrace())
            {
                if (frameCount >= maxFrames)
                    break;
                if (frame.Method?.Signature != null)
                {
                    frames.Add(frame.Method.Signature);
                    frameCount++;
                }
            }
            return frames;
        }

        public void Dispose() { }
    }

    internal class LockContention
    {
        public ulong ObjectAddress { get; set; }
        public string ObjectTypeName { get; set; } = string.Empty;
        public ClrThread? OwnerThread { get; set; }
        public ulong HoldingThreadAddress { get; set; }
        public int RecursionCount { get; set; }
        public int WaitingThreadCount { get; set; }
    }

    internal class DeadlockCandidate
    {
        public required ClrThread Thread { get; set; }
        public string TopFrame { get; set; } = string.Empty;
        public List<LockContention> LocksHeld { get; set; } = new();
    }

    internal class LockGraphAnalysis
    {
        public List<LockContention> AllHeldLocks { get; } = new();
        public List<LockContention> ContestedLocks { get; } = new();
        public List<DeadlockCandidate> DeadlockCandidates { get; } = new();
        public Dictionary<ulong, ClrThread> ThreadByAddress { get; set; } = new();
        public int UnresolvedOwnerCount { get; set; }
        public int LocksWithOwnerAddress { get; set; }
    }

}



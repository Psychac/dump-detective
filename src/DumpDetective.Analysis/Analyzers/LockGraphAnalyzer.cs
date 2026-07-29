using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class LockGraphAnalyzer : IAnalyzer, IThreadStackScanParticipant
    {
        public string Name => "Lock Graph Analysis";
        public string Category => "Locks";

        // Instance accumulator state for the IThreadStackScanParticipant path — only the top
        // frame is needed (deadlock-candidate detection is a single monitor.wait/monitor.enter
        // check), so this shares ThreadStackScanDispatcher's single EnumerateStackTrace() pass
        // with ThreadAnalyzer/HangAnalyzer/ThreadStackClusterAnalyzer instead of independently
        // walking stacks for the (typically small) set of lock-holding threads.
        private Dictionary<ulong, string?>? _participantTopFrameSignatureByThreadAddress;
        private bool _participantScanSucceeded;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LockGraphAnalysisOptions options = context.AnalysisOptions.LockGraphAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Progress, options).Stamp(this));
        }

        public int GetRequiredFrameCount(AnalysisContext context) => 1;

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

            _participantTopFrameSignatureByThreadAddress![thread.Address] =
                snapshot.TopFrames.Count > 0 ? snapshot.TopFrames[0].Method?.Signature : null;
        }

        public void OnThreadStackScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, progress: null, new LockGraphAnalysisOptions());
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IProgress<AnalyzerProgressReport>? progress, LockGraphAnalysisOptions options)
        {
            var graph = BuildLockGraph(runtime, heap, progress);

            var topContestedTypes = new List<NameCountEntry>(options.MaxContestedLocksToShow);
            var typeWaiters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cl in graph.ContestedLocks)
            {
                typeWaiters.TryGetValue(cl.ObjectTypeName, out int existing);
                typeWaiters[cl.ObjectTypeName] = existing + cl.WaitingThreadCount;
            }
            foreach (var kvp in typeWaiters.OrderByDescending(k => k.Value).Take(options.MaxContestedLocksToShow))
                topContestedTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));

            var contestedLockDetails = new List<ContestedLockSnapshot>(Math.Min(graph.ContestedLocks.Count, options.MaxContestedLocksToShow));
            int contestedLimit = Math.Min(graph.ContestedLocks.Count, options.MaxContestedLocksToShow);
            for (int i = 0; i < contestedLimit; i++)
            {
                var cl = graph.ContestedLocks[i];
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

                deadlockDetails.Add(new DeadlockCandidateSnapshot(
                    (uint)dc.Thread.ManagedThreadId,
                    (uint)dc.Thread.OSThreadId,
                    lockTypes,
                    lockAddresses,
                    summary));
            }

            return new LockGraphDomainResult(
                    graph.AllHeldLocks.Count,
                    graph.ContestedLocks.Count,
                    graph.ContestedLocks.Count > 0 ? graph.ContestedLocks[0].WaitingThreadCount : 0,
                    graph.DeadlockCandidates.Count,
                    topContestedTypes,
                    deadlockDetails,
                    contestedLockDetails);
        }

        private LockGraphAnalysis BuildLockGraph(ClrRuntime runtime, ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
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

            var scanCounter = new ObjectScanCounter("scanning sync blocks", progress, reportEveryObjects: 1000);
            foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
            {
                scanCounter.Tick();

                if (!sb.IsMonitorHeld || sb.Object == 0)
                    continue;

                string typeName = ResolveTypeNameByAddress(heap, sb.Object);

                threadByAddress.TryGetValue(sb.HoldingThreadAddress, out ClrThread? ownerThread);

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

            // Deadlock candidates: threads that own at least one inflated lock AND are blocked on a monitor
            var ownerManagedIds = new HashSet<uint>(
                result.AllHeldLocks
                    .Where(l => l.OwnerThread != null)
                    .Select(l => (uint)l.OwnerThread!.ManagedThreadId));

            foreach (var thread in threads)
            {
                if (!thread.IsAlive || thread.LockCount == 0) continue;
                if (!ownerManagedIds.Contains((uint)thread.ManagedThreadId)) continue;

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
                    ClrStackFrame? topFrame = null;
                    foreach (var frame in thread.EnumerateStackTrace())
                    {
                        topFrame = frame;
                        break;
                    }
                    topFrameSignature = topFrame?.Method?.Signature;
                }

                if (topFrameSignature == null) continue;

                string sig = topFrameSignature.ToLowerInvariant();
                if (!sig.Contains("monitor.wait") && !sig.Contains("monitor.enter"))
                    continue;

                result.DeadlockCandidates.Add(new DeadlockCandidate
                {
                    Thread = thread,
                    TopFrame = topFrameSignature,
                    LocksHeld = result.AllHeldLocks
                        .Where(l => l.OwnerThread?.ManagedThreadId == thread.ManagedThreadId)
                        .ToList()
                });
            }

            return result;
        }

        private static string ResolveTypeNameByAddress(ClrHeap heap, ulong objectAddress)
        {
            if (objectAddress == 0)
                return StringConstants.UnknownType;

            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid || obj.Type == null)
                return StringConstants.UnknownType;

            return obj.Type.Name ?? StringConstants.UnknownType;
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
    }

}



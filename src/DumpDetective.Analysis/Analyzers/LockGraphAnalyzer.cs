using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    internal class LockGraphAnalyzer : IAnalyzer
    {
        private const int MaxContestedLocksToShow = 15;

        public string Name => "Lock Graph Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzerExecutionResult executionResult = Analyze(context.Runtime, context.Heap);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            var graph = BuildLockGraph(runtime, heap);

            var topContestedTypes = graph.ContestedLocks
                .GroupBy(c => c.ObjectTypeName, StringComparer.Ordinal)
                .Select(g => new NameCountEntry(g.Key, g.Sum(x => x.WaitingThreadCount)))
                .OrderByDescending(x => x.Count)
                .Take(MaxContestedLocksToShow)
                .ToList();

            return new AnalyzerExecutionResult(
                [CreateFinding(graph)],
                new LockGraphDomainResult(
                    graph.AllHeldLocks.Count,
                    graph.ContestedLocks.Count,
                    graph.ContestedLocks.Count > 0 ? graph.ContestedLocks[0].WaitingThreadCount : 0,
                    graph.DeadlockCandidates.Count,
                    topContestedTypes));
        }

        private static InsightFinding CreateFinding(LockGraphAnalysis graph)
        {
            FindingSeverity severity = graph.DeadlockCandidates.Count >= 2
                ? FindingSeverity.Critical
                : graph.ContestedLocks.Count > 0
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(LockGraphAnalyzer),
                Category: "Threading",
                Severity: severity,
                Title: "Lock contention and deadlock graph",
                Evidence: $"{graph.AllHeldLocks.Count} inflated monitor lock(s) held; {graph.ContestedLocks.Count} contested; {graph.DeadlockCandidates.Count} deadlock candidate(s).",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Deadlock candidates detected. Review lock acquisition order and confirm circular-wait cycle."
                    : graph.ContestedLocks.Count > 0
                        ? "Reduce lock scope on contested objects or switch to lock-free patterns."
                        : "No lock contention detected in this snapshot.",
                Tags: ["locks", "deadlock", "contention", "monitor"],
                MetricValue: graph.DeadlockCandidates.Count,
                MetricUnit: "deadlock-candidates");
        }

        private LockGraphAnalysis BuildLockGraph(ClrRuntime runtime, ClrHeap heap)
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

            var scanCounter = new ObjectScanCounter("Lock graph sync block scan", reportEveryObjects: 1000);
            foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
            {
                scanCounter.Tick();

                if (!sb.IsMonitorHeld || sb.Object == 0)
                    continue;

                var obj = heap.GetObject(sb.Object);
                string typeName = obj.IsValid && obj.Type != null
                    ? (obj.Type.Name ?? StringConstants.UnknownType)
                    : StringConstants.UnknownType;

                threadByAddress.TryGetValue(sb.HoldingThreadAddress, out ClrThread? ownerThread);

                var entry = new LockContention
                {
                    ObjectAddress        = sb.Object,
                    ObjectTypeName       = typeName,
                    OwnerThread          = ownerThread,
                    HoldingThreadAddress = sb.HoldingThreadAddress,
                    RecursionCount       = sb.RecursionCount,
                    WaitingThreadCount   = sb.WaitingThreadCount
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

                ClrStackFrame? topFrame = null;
                foreach (var frame in thread.EnumerateStackTrace())
                {
                    topFrame = frame;
                    break;
                }

                if (topFrame?.Method?.Signature == null) continue;

                string sig = topFrame.Method.Signature.ToLowerInvariant();
                if (!sig.Contains("monitor.wait") && !sig.Contains("monitor.enter"))
                    continue;

                result.DeadlockCandidates.Add(new DeadlockCandidate
                {
                    Thread    = thread,
                    TopFrame  = topFrame.Method.Signature,
                    LocksHeld = result.AllHeldLocks
                        .Where(l => l.OwnerThread?.ManagedThreadId == thread.ManagedThreadId)
                        .ToList()
                });
            }

            return result;
        }
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



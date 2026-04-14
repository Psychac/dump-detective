using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class LockGraphAnalyzer
    {
        private const int MaxContestedLocksToShow = 15;
        private const int MaxCausalityNodesToShow = 20;

        private readonly OutputWriter _writer;

        public LockGraphAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            _writer.WriteHeader("LOCK GRAPH ANALYSIS:");

            var graph = BuildLockGraph(runtime, heap);

            PrintContestedLocks(graph);
            PrintDeadlockCandidates(graph);
            PrintCausalityChain(graph);

            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput(
                [CreateFinding(graph)],
                new LockGraphDomainResult(
                    graph.AllHeldLocks.Count,
                    graph.ContestedLocks.Count,
                    graph.ContestedLocks.Count > 0 ? graph.ContestedLocks[0].WaitingThreadCount : 0,
                    graph.DeadlockCandidates.Count));
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

            // Build thread-address → ClrThread lookup for owner resolution
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

        private void PrintContestedLocks(LockGraphAnalysis graph)
        {
            _writer.WriteLine("LOCK CONTENTION HOTSPOTS:");
            _writer.WriteSeparator();

            if (graph.AllHeldLocks.Count == 0)
            {
                _writer.WriteLine("No inflated monitor locks found in this dump.");
                _writer.WriteLine("Uncontested locks held via thin-lock encoding in object headers are not shown here.");
                return;
            }

            _writer.WriteLine($"Inflated locks held: {graph.AllHeldLocks.Count}");
            _writer.WriteLine($"Contested locks:     {graph.ContestedLocks.Count}");

            if (graph.ContestedLocks.Count == 0)
            {
                _writer.WriteLine("\nNo contested monitor locks detected.");
                return;
            }

            _writer.WriteLine(string.Empty);
            foreach (var entry in graph.ContestedLocks.Take(MaxContestedLocksToShow))
            {
                string owner = entry.OwnerThread != null
                    ? $"Thread {entry.OwnerThread.ManagedThreadId} (OS: {entry.OwnerThread.OSThreadId})"
                    : $"0x{entry.HoldingThreadAddress:X} (unresolved)";

                _writer.WriteLine($"🔒 {FormatHelper.TruncateString(entry.ObjectTypeName, 62)}");
                _writer.WriteLine($"   Address:  0x{entry.ObjectAddress:X}");
                _writer.WriteLine($"   Owner:    {owner}  (recursion: {entry.RecursionCount})");

                string waiterSuffix = entry.WaitingThreadCount >= 5 ? "  ⚠️  HIGH CONTENTION" : string.Empty;
                _writer.WriteLine($"   Waiters:  {entry.WaitingThreadCount} thread(s){waiterSuffix}");
                _writer.WriteLine(string.Empty);
            }
        }

        private void PrintDeadlockCandidates(LockGraphAnalysis graph)
        {
            _writer.WriteLine("DEADLOCK CANDIDATES:");
            _writer.WriteSeparator();

            if (graph.DeadlockCandidates.Count == 0)
            {
                _writer.WriteLine("No threads detected both holding an inflated lock and waiting on a monitor.");
                return;
            }

            if (graph.DeadlockCandidates.Count >= 2)
            {
                _writer.WriteLine($"🔴 PROBABLE DEADLOCK: {graph.DeadlockCandidates.Count} thread(s) each hold a lock while waiting on a monitor.");
                _writer.WriteLine(string.Empty);
            }
            else
            {
                _writer.WriteLine($"⚠️  DEADLOCK CANDIDATE: Thread {graph.DeadlockCandidates[0].Thread.ManagedThreadId} holds a lock while waiting on a monitor.");
                _writer.WriteLine($"    A second thread contesting this thread's lock would complete the cycle.");
                _writer.WriteLine(string.Empty);
            }

            foreach (var candidate in graph.DeadlockCandidates)
            {
                var t = candidate.Thread;
                _writer.WriteLine($"Thread {t.ManagedThreadId} (OS: {t.OSThreadId}):");
                _writer.WriteLine($"  Waiting at: {FormatHelper.TruncateString(candidate.TopFrame, 75)}");

                if (candidate.LocksHeld.Count > 0)
                {
                    _writer.WriteLine($"  Holding {candidate.LocksHeld.Count} lock(s):");
                    foreach (var held in candidate.LocksHeld)
                        _writer.WriteLine($"    → {FormatHelper.TruncateString(held.ObjectTypeName, 55)}  @ 0x{held.ObjectAddress:X}  ({held.WaitingThreadCount} waiter(s))");
                }

                _writer.WriteLine(string.Empty);
            }

            if (graph.DeadlockCandidates.Count >= 2)
            {
                _writer.WriteLine("💡 INVESTIGATION STEPS:");
                _writer.WriteLine("   1. Each thread above holds an object lock it won't release until it acquires another.");
                _writer.WriteLine("   2. If Thread A holds what Thread B wants, and Thread B holds what Thread A wants → deadlock.");
                _writer.WriteLine("   3. Compare each thread's 'Holding' list against the contested lock owners above.");
                _writer.WriteLine("   4. Fix: enforce a consistent global lock acquisition order across all code paths.");
            }
        }

        private void PrintCausalityChain(LockGraphAnalysis graph)
        {
            _writer.WriteLine("LOCK CAUSALITY CHAIN:");
            _writer.WriteSeparator();

            if (graph.AllHeldLocks.Count == 0)
            {
                _writer.WriteLine("No inflated locks available for causality chain.");
                return;
            }

            // Group by owner thread, sort by total downstream thread pressure
            var byOwner = new Dictionary<int, List<LockContention>>();
            foreach (var entry in graph.AllHeldLocks)
            {
                if (entry.OwnerThread == null) continue;
                int id = entry.OwnerThread.ManagedThreadId;
                if (!byOwner.TryGetValue(id, out var list))
                {
                    list = new List<LockContention>();
                    byOwner[id] = list;
                }
                list.Add(entry);
            }

            if (byOwner.Count == 0)
            {
                _writer.WriteLine("Could not resolve any lock owners to managed threads.");
                return;
            }

            var deadlockIds = new HashSet<int>(graph.DeadlockCandidates.Select(d => d.Thread.ManagedThreadId));

            int shown = 0;
            foreach (var kvp in byOwner.OrderByDescending(k => k.Value.Sum(l => l.WaitingThreadCount)))
            {
                if (shown >= MaxCausalityNodesToShow) break;

                int tid = kvp.Key;
                var locks = kvp.Value;
                int totalWaiters = locks.Sum(l => l.WaitingThreadCount);
                bool isCandidate = deadlockIds.Contains(tid);

                string prefix = isCandidate ? "⚠️ " : "   ";
                string suffix = isCandidate ? "  ← also waiting on a monitor" : string.Empty;
                _writer.WriteLine($"{prefix}Thread {tid}{suffix}");

                foreach (var l in locks)
                    _writer.WriteLine($"     → holds: {FormatHelper.TruncateString(l.ObjectTypeName, 55)}  ({l.WaitingThreadCount} thread(s) waiting)");

                if (totalWaiters > 0)
                    _writer.WriteLine($"     Total downstream pressure: {totalWaiters} thread(s) blocked by this thread");

                _writer.WriteLine(string.Empty);
                shown++;
            }
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

using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadConcurrencySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => ["ThreadAnalyzer", "HangAnalyzer", "LockGraphAnalyzer"];

    public string SectionId => "prof.thread-concurrency";
    public string DisplayTitle => "Thread & Concurrency";
    public int SortOrder => 1300;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<ThreadDomainResult>() is not null
        || results.Get<HangDomainResult>() is not null
        || results.Get<LockGraphDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        ThreadDomainResult? threads = results.Get<ThreadDomainResult>();
        HangDomainResult? hang = results.Get<HangDomainResult>();
        LockGraphDomainResult? lockGraph = results.Get<LockGraphDomainResult>();

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.85, ["Derived from recorded thread, wait, and lock-state data."]),
            T("Thread counts, waits, lock contention, and deadlock candidates are summarized here."),
        };

        var keyMetrics = new List<SectionKeyMetric>();

        if (threads is not null)
        {
            tables.Add(ST(
                "Thread lifecycle",
                ["Signal", "Value", "Notes"],
                [
                    Row(Cell("Total"),            Cell(threads.TotalThreadCount.ToString("N0"),               threads.TotalThreadCount),             Cell("All recorded threads")),
                    Row(Cell("Alive"),             Cell(threads.AliveThreadCount.ToString("N0"),               threads.AliveThreadCount),             Cell("Alive in the runtime")),
                    Row(Cell("Inactive"),          Cell(threads.InactiveThreadCount.ToString("N0"),            threads.InactiveThreadCount),          Cell("Not currently active")),
                    Row(Cell("Background"),        Cell(threads.BackgroundThreadCount.ToString("N0"),          threads.BackgroundThreadCount),        Cell("Background threads")),
                    Row(Cell("GC threads"),        Cell(threads.GcThreadCount.ToString("N0"),                  threads.GcThreadCount),                Cell("GC helper threads")),
                    Row(Cell("Blocked"),           Cell(threads.BlockedThreadCount.ToString("N0"),             threads.BlockedThreadCount),           Cell("Threads in a blocked wait pattern")),
                    Row(Cell("Lock-holding"),      Cell(threads.LockHoldingThreadCount.ToString("N0"),         threads.LockHoldingThreadCount),       Cell("Threads currently holding locks")),
                    Row(Cell("Active exceptions"), Cell(threads.ThreadsWithActiveExceptionsCount.ToString("N0"), threads.ThreadsWithActiveExceptionsCount), Cell("Threads with active exceptions")),
                    Row(Cell("Async chain threads"), Cell(threads.AsyncChainThreadCount.ToString("N0"),        threads.AsyncChainThreadCount),        Cell("Threads in async chains")),
                    Row(Cell("Max async depth"),   Cell(threads.MaxAsyncChainDepth.ToString("N0"),             threads.MaxAsyncChainDepth),           Cell("Longest observed async chain")),
                ]));

            if (threads.WaitPatternBreakdown.Count > 0)
            {
                var waitRows = new List<TableRow>(threads.WaitPatternBreakdown.Count);
                foreach (KeyValuePair<string, int> kvp in threads.WaitPatternBreakdown.OrderByDescending(kvp => kvp.Value))
                    waitRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
                tables.Add(ST("Wait category breakdown", ["Category", "Count"], waitRows));
            }

            if (threads.TopBlockedThreads is { Count: > 0 })
            {
                var blockedRows = new List<TableRow>(threads.TopBlockedThreads.Count);
                for (int i = 0; i < threads.TopBlockedThreads.Count; i++)
                {
                    ThreadStateSnapshot snapshot = threads.TopBlockedThreads[i];
                    long stackSize = snapshot.StackSizeBytes > (ulong)long.MaxValue ? long.MaxValue : (long)snapshot.StackSizeBytes;
                    blockedRows.Add(Row(
                        Cell(snapshot.OSThreadId.ToString("N0"), snapshot.OSThreadId),
                        Cell(snapshot.WaitCategory ?? "—"),
                        Cell(snapshot.WaitReason ?? "—"),
                        Cell(snapshot.LockCount.ToString("N0"), snapshot.LockCount),
                        Cell(stackSize > 0 ? stackSize.ToString("N0") : "—", stackSize),
                        Cell(snapshot.TopFrames.Count > 0 ? snapshot.TopFrames[0] : "—")));
                }
                tables.Add(ST("Blocked threads", ["OS Thread", "Wait Category", "Wait Reason", "Locks", "Stack Size", "Top Frame"], blockedRows));
            }

            if (threads.TopLockedThreads is { Count: > 0 })
            {
                var lockedRows = new List<TableRow>(threads.TopLockedThreads.Count);
                for (int i = 0; i < threads.TopLockedThreads.Count; i++)
                {
                    ThreadStateSnapshot snapshot = threads.TopLockedThreads[i];
                    long stackSize = snapshot.StackSizeBytes > (ulong)long.MaxValue ? long.MaxValue : (long)snapshot.StackSizeBytes;
                    lockedRows.Add(Row(
                        Cell(snapshot.OSThreadId.ToString("N0"), snapshot.OSThreadId),
                        Cell(snapshot.LockCount.ToString("N0"), snapshot.LockCount),
                        Cell(snapshot.GcMode),
                        Cell(stackSize > 0 ? stackSize.ToString("N0") : "—", stackSize),
                        Cell(snapshot.TopFrames.Count > 0 ? snapshot.TopFrames[0] : "—")));
                }
                tables.Add(ST("Lock-holding threads", ["OS Thread", "Lock Count", "GC Mode", "Stack Size", "Top Frame"], lockedRows));
            }

            if (threads.TopStackHotspots is { Count: > 0 })
            {
                var hotspotRows = new List<TableRow>(threads.TopStackHotspots.Count);
                for (int i = 0; i < threads.TopStackHotspots.Count; i++)
                    hotspotRows.Add(Row(Cell(threads.TopStackHotspots[i].Name), Cell(threads.TopStackHotspots[i].Count.ToString("N0"), threads.TopStackHotspots[i].Count)));
                tables.Add(ST("Top stack hotspots", ["Frame", "Count"], hotspotRows));
            }

            if (threads.GcModeDistribution is { Count: > 0 })
            {
                var gcRows = new List<TableRow>(threads.GcModeDistribution.Count);
                foreach (KeyValuePair<string, int> kvp in threads.GcModeDistribution.OrderByDescending(kvp => kvp.Value))
                    gcRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
                tables.Add(ST("GC mode distribution", ["Mode", "Count"], gcRows));
            }
        }

        if (hang is not null)
        {
            keyMetrics.Add(KM("Queued work items", hang.QueuedWorkItems.ToString("N0"),                                          hang.QueuedWorkItems));
            keyMetrics.Add(KM("Total tasks",       hang.TotalTasks.ToString("N0"),                                               hang.TotalTasks));
            keyMetrics.Add(KM("Pending tasks",     hang.PendingTasks.ToString("N0"),                                             hang.PendingTasks));
            keyMetrics.Add(KM("Faulted tasks",     hang.FaultedTasks.ToString("N0"),                                             hang.FaultedTasks));
            keyMetrics.Add(KM("Canceled tasks",    hang.CanceledTasks.ToString("N0"),                                            hang.CanceledTasks));
            keyMetrics.Add(KM("Runtime TP data",   hang.RuntimeThreadPoolDataAvailable ? "Available" : "Unavailable",            hang.RuntimeThreadPoolDataAvailable ? 1.0 : 0.0));
            keyMetrics.Add(KM("Health score",      hang.HealthScore.ToString("N0"),                                              hang.HealthScore));

            if (hang.RuntimeThreadPoolDataAvailable)
            {
                var tpRows = new List<TableRow>
                {
                    Row(Cell("Min worker threads"),   Cell(hang.RuntimeMinThreads.ToString("N0"),          hang.RuntimeMinThreads)),
                    Row(Cell("Max worker threads"),   Cell(hang.RuntimeMaxThreads.ToString("N0"),          hang.RuntimeMaxThreads)),
                    Row(Cell("Active worker threads"),Cell(hang.RuntimeActiveWorkerThreads.ToString("N0"),hang.RuntimeActiveWorkerThreads)),
                    Row(Cell("Idle worker threads"),  Cell(hang.RuntimeIdleWorkerThreads.ToString("N0"),  hang.RuntimeIdleWorkerThreads)),
                    Row(Cell("Retired worker threads"),Cell(hang.RuntimeRetiredWorkerThreads.ToString("N0"),hang.RuntimeRetiredWorkerThreads)),
                    Row(Cell("Runtime queue length"), Cell(hang.RuntimeQueueLength.HasValue ? hang.RuntimeQueueLength.Value.ToString("N0") : "Unavailable", hang.RuntimeQueueLength)),
                    Row(Cell("CPU utilization"),      Cell($"{hang.RuntimeCpuUtilization:N0}%",            hang.RuntimeCpuUtilization)),
                    Row(Cell("Starvation flag"),      Cell(hang.IsStarved ? "Yes" : "No",                  hang.IsStarved ? 1L : 0L)),
                    Row(Cell("Queue length proxy"),   Cell($"{hang.QueuedWorkItems:N0} queued work items", hang.QueuedWorkItems)),
                };
                tables.Add(ST("Runtime thread-pool metrics", ["Signal", "Value"], tpRows));
                blocks.Add(T(hang.RuntimeQueueLength.HasValue
                    ? "Runtime queue length is shown directly when the ClrMD thread-pool surface exposes it; queued work items remain a dump-derived proxy."
                    : "Queued work items remain the fallback queue-length proxy because this ClrMD thread-pool surface did not expose a runtime queue length value."));
            }
            else
            {
                blocks.Add(T("Runtime thread-pool metadata was unavailable; this summary is approximate."));
            }

            if (hang.TopContinuationTypes is { Count: > 0 })
            {
                var rows = new List<TableRow>(hang.TopContinuationTypes.Count);
                for (int i = 0; i < hang.TopContinuationTypes.Count; i++)
                    rows.Add(Row(Cell(hang.TopContinuationTypes[i].Name), Cell(hang.TopContinuationTypes[i].Count.ToString("N0"), hang.TopContinuationTypes[i].Count)));
                tables.Add(ST("Continuation types", ["Type", "Count"], rows));
            }
        }

        if (lockGraph is not null)
        {
            keyMetrics.Add(KM("Deadlock candidates",        lockGraph.DeadlockCandidateCount.ToString("N0"),  lockGraph.DeadlockCandidateCount));
            keyMetrics.Add(KM("Contested locks",            lockGraph.ContestedLockCount.ToString("N0"),       lockGraph.ContestedLockCount));
            keyMetrics.Add(KM("Max waiters on single lock", lockGraph.MaxWaitersOnSingleLock.ToString("N0"),   lockGraph.MaxWaitersOnSingleLock));

            if (lockGraph.DeadlockCandidateDetails is { Count: > 0 })
            {
                var rows = new List<TableRow>(lockGraph.DeadlockCandidateDetails.Count);
                for (int i = 0; i < lockGraph.DeadlockCandidateDetails.Count; i++)
                {
                    DeadlockCandidateSnapshot snapshot = lockGraph.DeadlockCandidateDetails[i];
                    rows.Add(Row(
                        Cell(snapshot.ManagedThreadId.ToString("N0"), snapshot.ManagedThreadId),
                        Cell(snapshot.OsThreadId.ToString("N0"), snapshot.OsThreadId),
                        Cell(string.Join(", ", snapshot.LockObjectTypes)),
                        Cell(snapshot.CycleSummary)));
                }
                tables.Add(ST("Deadlock candidates", ["Managed Thread", "OS Thread", "Locks Held", "Summary"], rows));
            }

            if (lockGraph.ContestedLockDetails is { Count: > 0 })
            {
                var rows = new List<TableRow>(lockGraph.ContestedLockDetails.Count);
                for (int i = 0; i < lockGraph.ContestedLockDetails.Count; i++)
                {
                    ContestedLockSnapshot snapshot = lockGraph.ContestedLockDetails[i];
                    rows.Add(Row(
                        Cell($"0x{snapshot.ObjectAddress:X}"),
                        Cell(snapshot.ObjectTypeName),
                        Cell(snapshot.WaitingThreadCount.ToString("N0"), snapshot.WaitingThreadCount),
                        Cell(snapshot.OwnerManagedThreadId?.ToString() ?? "—"),
                        Cell(snapshot.RecursionCount.ToString("N0"), snapshot.RecursionCount)));
                }
                tables.Add(ST("Contested locks", ["Address", "Type", "Waiters", "Owner Thread", "Recursion"], rows));
            }
        }

        return new AnalyzerDetailSection(
            "Thread & Concurrency", DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics.Count > 0 ? keyMetrics : null,
            Tables: tables.Count > 0 ? tables : null);
    }
}
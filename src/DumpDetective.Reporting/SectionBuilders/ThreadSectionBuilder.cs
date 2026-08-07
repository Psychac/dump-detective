using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Thread Analysis";
    public string DisplayTitle => "Thread Overview";
    public int SortOrder => 100;

    public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ThreadDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_threads"] = new NumericMetricValue(d.TotalThreadCount, MetricUnit.Count),
            ["alive_threads"] = new NumericMetricValue(d.AliveThreadCount, MetricUnit.Count),
            ["inactive_threads"] = new NumericMetricValue(d.InactiveThreadCount, MetricUnit.Count),
            ["background_threads"] = new NumericMetricValue(d.BackgroundThreadCount, MetricUnit.Count),
            ["gc_threads"] = new NumericMetricValue(d.GcThreadCount, MetricUnit.Count),
            ["thread_pool_workers"] = new NumericMetricValue(d.ThreadPoolWorkerCount, MetricUnit.Count),
            ["blocked_threads"] = new NumericMetricValue(d.BlockedThreadCount, MetricUnit.Count),
            ["lock_holding_threads"] = new NumericMetricValue(d.LockHoldingThreadCount, MetricUnit.Count),
            ["threads_with_exceptions"] = new NumericMetricValue(d.ThreadsWithActiveExceptionsCount, MetricUnit.Count),
            ["threadpool_queue_depth"] = new NumericMetricValue(d.ThreadPoolQueueDepth, MetricUnit.Count),
            ["threadpool_active_workers"] = new NumericMetricValue(d.ThreadPoolActiveWorkers, MetricUnit.Count),
            ["threadpool_idle_workers"] = new NumericMetricValue(d.ThreadPoolIdleWorkers, MetricUnit.Count),
            ["threadpool_min_workers"] = new NumericMetricValue(d.ThreadPoolMinWorkers, MetricUnit.Count),
            ["threadpool_max_workers"] = new NumericMetricValue(d.ThreadPoolMaxWorkers, MetricUnit.Count),
            ["finalizer_blocked"] = new TextMetricValue(d.FinalizerThreadBlocked ? "Yes" : "No"),
            ["finalizer_lock_count"] = new NumericMetricValue(d.FinalizerLockCount, MetricUnit.Count),
            ["async_chain_threads"] = new NumericMetricValue(d.AsyncChainThreadCount, MetricUnit.Count),
            ["max_async_chain_depth"] = new NumericMetricValue(d.MaxAsyncChainDepth, MetricUnit.Count),
        };
        if (d.FinalizerManagedThreadId.HasValue)
        {
            keyMetrics["finalizer_thread_id"] = new NumericMetricValue(d.FinalizerManagedThreadId.Value, MetricUnit.Count);
            if (d.FinalizerOsThreadId.HasValue)
                keyMetrics["finalizer_os_thread"] = new NumericMetricValue(d.FinalizerOsThreadId.Value, MetricUnit.Count);
        }
        if (d.SamplingCapacity > 0 || d.SampledSnapshotCount > 0)
        {
            keyMetrics["sampled_snapshots"] = new NumericMetricValue(d.SampledSnapshotCount, MetricUnit.Count);
            keyMetrics["sampling_capacity"] = new NumericMetricValue(d.SamplingCapacity, MetricUnit.Count);
            keyMetrics["sampling_seed"] = new TextMetricValue($"0x{d.SamplingSeed:X8}");
        }

        // Finalizer frames — emitted as a NamedStackTrace typed slot
        var finFrames = d.FinalizerFrames ?? [];
        var stackTraces = new List<NamedStackTrace>();
        if (finFrames.Count > 0)
        {
            string blockedSuffix = d.FinalizerThreadBlocked ? " — BLOCKED" : string.Empty;
            var finMeta = new System.Collections.Generic.Dictionary<string, string>
            {
                ["State"] = d.FinalizerThreadBlocked ? "Blocked" : "Running",
            };
            if (d.FinalizerManagedThreadId.HasValue)
                finMeta["Thread ID"] = d.FinalizerManagedThreadId.Value.ToString("N0");
            if (d.FinalizerOsThreadId.HasValue)
                finMeta["OS Thread"] = d.FinalizerOsThreadId.Value.ToString("N0");
            finMeta["Lock Count"] = d.FinalizerLockCount.ToString("N0");
            var finEntries = new List<StackFrameEntry>(finFrames.Count);
            for (int i = 0; i < finFrames.Count; i++)
                finEntries.Add(new StackFrameEntry(i, finFrames[i], IsFrameworkFrame(finFrames[i])));
            stackTraces.Add(new NamedStackTrace($"Finalizer Thread{blockedSuffix}", "Finalizer", finMeta, finEntries, false));
        }

        if (d.WaitPatternBreakdown.Count > 0)
        {
            var wcRows = new List<TableRow>(d.WaitPatternBreakdown.Count);
            foreach (var kvp in d.WaitPatternBreakdown)
                wcRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            compactTables.Add(STCompact("Wait category distribution", new[] { CH("Category"), CH("Count","number") }, wcRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopBlockedThreads is { Count: > 0 })
        {
            var bRows = new List<TableRow>(d.TopBlockedThreads.Count);
            for (int i = 0; i < d.TopBlockedThreads.Count; i++)
            {
                ThreadStateSnapshot s = d.TopBlockedThreads[i];
                bRows.Add(new TableRow([
                    Cell(s.ThreadId.ToString("N0"),    s.ThreadId),
                    Cell(s.OSThreadId.ToString("N0"),  s.OSThreadId),
                    Cell(s.LockCount.ToString("N0"),   s.LockCount),
                    Cell(s.ThreadState),
                    Cell(s.GcMode),
                    Cell(s.WaitCategory ?? "—"),
                    Cell(s.WaitReason   ?? "—"),
                    Cell(s.StackSizeBytes > 0 ? FormatHelper.FormatBytes(s.StackSizeBytes) : "—"),
                    Cell(s.TopFrames.Count > 0 ? s.TopFrames[0] : "—")]));
            }
            compactTables.Add(STCompact("Top blocked threads", new[] { CH("Thread ID","number"), CH("OS Thread","number"), CH("Lock Count","number"), CH("State"), CH("GC Mode"), CH("Wait Category"), CH("Wait Reason"), CH("Stack Size","bytes"), CH("Top Frame") }, bRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopLockedThreads is { Count: > 0 })
        {
            var lRows = new List<TableRow>(d.TopLockedThreads.Count);
            for (int i = 0; i < d.TopLockedThreads.Count; i++)
            {
                ThreadStateSnapshot s = d.TopLockedThreads[i];
                lRows.Add(new TableRow([
                    Cell(s.ThreadId.ToString("N0"),    s.ThreadId),
                    Cell(s.OSThreadId.ToString("N0"),  s.OSThreadId),
                    Cell(s.LockCount.ToString("N0"),   s.LockCount),
                    Cell(s.ThreadState),
                    Cell(s.GcMode),
                    Cell(s.WaitCategory ?? "—"),
                    Cell(s.WaitReason   ?? "—"),
                    Cell(s.StackSizeBytes > 0 ? FormatHelper.FormatBytes(s.StackSizeBytes) : "—"),
                    Cell(s.TopFrames.Count > 0 ? s.TopFrames[0] : "—")]));
            }
            compactTables.Add(STCompact("Top lock-holding threads", new[] { CH("Thread ID","number"), CH("OS Thread","number"), CH("Lock Count","number"), CH("State"), CH("GC Mode"), CH("Wait Category"), CH("Wait Reason"), CH("Stack Size","bytes"), CH("Top Frame") }, lRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ThreadStateDistribution is { Count: > 0 })
        {
            var stateRows = new List<TableRow>(d.ThreadStateDistribution.Count);
            foreach (var kvp in d.ThreadStateDistribution.OrderByDescending(kvp => kvp.Value))
                stateRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            compactTables.Add(STCompact("Thread state distribution", new[] { CH("Thread State"), CH("Count","number") }, stateRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.GcModeDistribution is { Count: > 0 })
        {
            var gcModeRows = new List<TableRow>(d.GcModeDistribution.Count);
            foreach (var kvp in d.GcModeDistribution.OrderByDescending(kvp => kvp.Value))
                gcModeRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            compactTables.Add(STCompact("GC mode distribution", new[] { CH("GC Mode"), CH("Count","number") }, gcModeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ExceptionTypeDistribution is { Count: > 0 })
        {
            var exTypeRows = new List<TableRow>(d.ExceptionTypeDistribution.Count);
            foreach (var kvp in d.ExceptionTypeDistribution.OrderByDescending(kvp => kvp.Value))
                exTypeRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            compactTables.Add(STCompact("Exception type distribution", new[] { CH("Exception Type"), CH("Count","number") }, exTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ThreadsWithActiveExceptions is { Count: > 0 })
        {
            var exRows = new List<TableRow>(d.ThreadsWithActiveExceptions.Count);
            for (int i = 0; i < d.ThreadsWithActiveExceptions.Count; i++)
            {
                ThreadExceptionSnapshot snapshot = d.ThreadsWithActiveExceptions[i];
                exRows.Add(new TableRow([
                    Cell(snapshot.ThreadId.ToString("N0"), snapshot.ThreadId),
                    Cell(snapshot.OSThreadId.ToString("N0"), snapshot.OSThreadId),
                    Cell(snapshot.ExceptionType),
                    Cell(snapshot.ExceptionMessage ?? "—"),
                    Cell(snapshot.LockCount.ToString("N0"), snapshot.LockCount),
                    Cell(snapshot.GcMode),
                    Cell(snapshot.TopFrames.Count > 0 ? snapshot.TopFrames[0] : "—")]));
            }
            compactTables.Add(STCompact("Threads with active exceptions", new[] { CH("Thread ID","number"), CH("OS Thread","number"), CH("Exception Type"), CH("Message"), CH("Lock Count","number"), CH("GC Mode"), CH("Top Frame") }, exRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var hotspots = d.TopStackHotspots ?? [];
        if (hotspots.Count > 0)
        {
            var hsRows = new List<TableRow>(hotspots.Count);
            for (int i = 0; i < hotspots.Count; i++)
                hsRows.Add(new TableRow([Cell(hotspots[i].Name), Cell($"{hotspots[i].Count:N0}", hotspots[i].Count)]));
            compactTables.Add(STCompact("Top frame hotspots", new[] { CH("Frame"), CH("Count","number") }, hsRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var sampled = d.SampledThreads ?? [];
        if (sampled.Count > 0)
        {
            var lockedSet  = d.TopLockedThreads     ?? (IReadOnlyList<ThreadStateSnapshot>)[];
            var blockedSet = d.TopBlockedThreads    ?? (IReadOnlyList<ThreadStateSnapshot>)[];
            var exSet      = d.ThreadsWithActiveExceptions ?? (IReadOnlyList<ThreadExceptionSnapshot>)[];

            for (int i = 0; i < sampled.Count; i++)
            {
                var s = sampled[i];
                bool isCaptured = false;
                for (int j = 0; j < lockedSet.Count;  j++) if (lockedSet[j].ThreadId  == s.ThreadId && lockedSet[j].OSThreadId  == s.OSThreadId)  { isCaptured = true; break; }
                for (int j = 0; j < blockedSet.Count; j++) if (blockedSet[j].ThreadId == s.ThreadId && blockedSet[j].OSThreadId == s.OSThreadId) { isCaptured = true; break; }
                for (int j = 0; j < exSet.Count;      j++) if (exSet[j].ThreadId      == s.ThreadId && exSet[j].OSThreadId      == s.OSThreadId)      { isCaptured = true; break; }

                string snapshotTag = isCaptured ? "Captured" : "Sampled";
                string waitTag = !string.IsNullOrEmpty(s.WaitCategory) ? $" | {s.WaitCategory}" : string.Empty;
                string lockTag = s.LockCount > 0 ? $" | {s.LockCount} lock{(s.LockCount == 1 ? "" : "s")}" : string.Empty;
                string label = $"Thread {s.ThreadId} (OS {s.OSThreadId}) — {snapshotTag}{waitTag}{lockTag}";
                string category = isCaptured ? "Captured" : "Sampled";

                var meta = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["State"]       = s.ThreadState,
                    ["GC Mode"]     = s.GcMode,
                    ["Lock Count"]  = s.LockCount.ToString("N0"),
                    ["Stack Roots"] = s.StackRootCount.ToString("N0"),
                };
                if (s.StackSizeBytes > 0)
                    meta["Stack Size"] = FormatBytes(s.StackSizeBytes);
                if (!string.IsNullOrEmpty(s.WaitCategory))
                    meta["Wait Category"] = s.WaitCategory!;
                if (!string.IsNullOrEmpty(s.WaitReason))
                    meta["Wait Reason"] = s.WaitReason!;

                var frames = new List<StackFrameEntry>(s.TopFrames.Count);
                for (int f = 0; f < s.TopFrames.Count; f++)
                    frames.Add(new StackFrameEntry(f, s.TopFrames[f], IsFrameworkFrame(s.TopFrames[f])));

                stackTraces.Add(new NamedStackTrace(label, category, meta, frames, false));
            }
        }

        if (d.AppDomainDistribution is { Count: > 0 })
        {
            var appRows = new List<TableRow>(d.AppDomainDistribution.Count);
            foreach (var kvp in d.AppDomainDistribution)
                appRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            compactTables.Add(STCompact("AppDomain thread distribution", new[] { CH("AppDomain"), CH("Thread Count","number") }, appRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            StackTraces: stackTraces.Count > 0 ? stackTraces : null);
    }

    private static bool IsFrameworkFrame(string frame) =>
        frame.StartsWith("System.",    StringComparison.Ordinal) ||
        frame.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        frame.StartsWith("mscorlib",   StringComparison.Ordinal);
}

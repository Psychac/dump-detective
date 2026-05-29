using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Threads",        $"{d.TotalThreadCount:N0}",      d.TotalThreadCount),
            KM("Alive Threads",        $"{d.AliveThreadCount:N0}",      d.AliveThreadCount),
            KM("Inactive Threads",     $"{d.InactiveThreadCount:N0}",   d.InactiveThreadCount),
            KM("Background Threads",   $"{d.BackgroundThreadCount:N0}", d.BackgroundThreadCount),
            KM("GC Threads",           $"{d.GcThreadCount:N0}",         d.GcThreadCount),
            KM("Thread Pool Workers",  $"{d.ThreadPoolWorkerCount:N0}", d.ThreadPoolWorkerCount),
            KM("Blocked Threads",      $"{d.BlockedThreadCount:N0}",    d.BlockedThreadCount),
            KM("Lock-Holding Threads", $"{d.LockHoldingThreadCount:N0}",d.LockHoldingThreadCount),
            KM("Threads w/ Exceptions",$"{d.ThreadsWithActiveExceptionsCount:N0}", d.ThreadsWithActiveExceptionsCount),
            KM("Finalizer Blocked",    d.FinalizerThreadBlocked ? "Yes" : "No"),
            KM("Finalizer Lock Count", $"{d.FinalizerLockCount:N0}",    d.FinalizerLockCount),
            KM("Async Chain Threads",  $"{d.AsyncChainThreadCount:N0}", d.AsyncChainThreadCount),
            KM("Max Async Chain Depth",$"{d.MaxAsyncChainDepth:N0}",    d.MaxAsyncChainDepth),
        };
        if (d.FinalizerManagedThreadId.HasValue)
        {
            keyMetrics.Add(KM("Finalizer Thread ID",    $"{d.FinalizerManagedThreadId.Value:N0}", d.FinalizerManagedThreadId.Value));
            if (d.FinalizerOsThreadId.HasValue)
                keyMetrics.Add(KM("Finalizer OS Thread", $"{d.FinalizerOsThreadId.Value:N0}",     d.FinalizerOsThreadId.Value));
        }
        if (d.SamplingCapacity > 0 || d.SampledSnapshotCount > 0)
        {
            keyMetrics.Add(KM("Sampled Snapshots", $"{d.SampledSnapshotCount:N0} sampled (capacity {d.SamplingCapacity:N0})", d.SampledSnapshotCount));
            keyMetrics.Add(KM("Sampling Seed", $"0x{d.SamplingSeed:X8}"));
        }

        // Finalizer frames — meaningful narrative
        var finFrames = d.FinalizerFrames ?? [];
        if (finFrames.Count > 0)
        {
            blocks.Add(H("FINALIZER FRAMES"));
            for (int i = 0; i < finFrames.Count; i++)
                blocks.Add(T(finFrames[i], 1));
        }

        if (d.WaitPatternBreakdown.Count > 0)
        {
            var wcRows = new List<TableRow>(d.WaitPatternBreakdown.Count);
            foreach (var kvp in d.WaitPatternBreakdown)
                wcRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            tables.Add(ST("Wait category distribution", ["Category", "Count"], wcRows));
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
            tables.Add(ST("Top blocked threads",
                ["Thread ID", "OS Thread", "Lock Count", "State", "GC Mode", "Wait Category", "Wait Reason", "Stack Size", "Top Frame"],
                bRows));
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
            tables.Add(ST("Top lock-holding threads",
                ["Thread ID", "OS Thread", "Lock Count", "State", "GC Mode", "Wait Category", "Wait Reason", "Stack Size", "Top Frame"],
                lRows));
        }

        if (d.ThreadStateDistribution is { Count: > 0 })
        {
            var stateRows = new List<TableRow>(d.ThreadStateDistribution.Count);
            foreach (var kvp in d.ThreadStateDistribution.OrderByDescending(kvp => kvp.Value))
                stateRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            tables.Add(ST("Thread state distribution", ["Thread State", "Count"], stateRows));
        }

        if (d.GcModeDistribution is { Count: > 0 })
        {
            var gcModeRows = new List<TableRow>(d.GcModeDistribution.Count);
            foreach (var kvp in d.GcModeDistribution.OrderByDescending(kvp => kvp.Value))
                gcModeRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            tables.Add(ST("GC mode distribution", ["GC Mode", "Count"], gcModeRows));
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
            tables.Add(ST("Threads with active exceptions",
                ["Thread ID", "OS Thread", "Exception Type", "Message", "Lock Count", "GC Mode", "Top Frame"],
                exRows));
        }

        var hotspots = d.TopStackHotspots ?? [];
        if (hotspots.Count > 0)
        {
            var hsRows = new List<TableRow>(hotspots.Count);
            for (int i = 0; i < hotspots.Count; i++)
                hsRows.Add(new TableRow([Cell(hotspots[i].Name), Cell($"{hotspots[i].Count:N0}", hotspots[i].Count)]));
            tables.Add(ST("Top frame hotspots", ["Frame", "Count"], hsRows));
        }

        var sampled = d.SampledThreads ?? [];
        if (sampled.Count > 0)
        {
            blocks.Add(H("SAMPLED THREAD SNAPSHOTS"));
            for (int i = 0; i < sampled.Count; i++)
            {
                var s = sampled[i];
                bool isCaptured = false;
                var locked = d.TopLockedThreads ?? new List<ThreadStateSnapshot>();
                var blocked = d.TopBlockedThreads ?? new List<ThreadStateSnapshot>();
                var exceptions = d.ThreadsWithActiveExceptions ?? new List<ThreadExceptionSnapshot>();
                if (locked.Any(t => t.ThreadId == s.ThreadId && t.OSThreadId == s.OSThreadId)) isCaptured = true;
                if (blocked.Any(t => t.ThreadId == s.ThreadId && t.OSThreadId == s.OSThreadId)) isCaptured = true;
                if (exceptions.Any(t => t.ThreadId == s.ThreadId && t.OSThreadId == s.OSThreadId)) isCaptured = true;
                blocks.Add(H($"Thread {s.ThreadId} (OS {s.OSThreadId})", 2));
                blocks.Add(M("Snapshot Type", isCaptured ? "Captured" : "Sampled"));
                blocks.Add(M("State", s.ThreadState));
                if (!string.IsNullOrEmpty(s.WaitCategory))
                    blocks.Add(M("Wait", s.WaitCategory ?? ""));
                if (!string.IsNullOrEmpty(s.WaitReason))
                    blocks.Add(M("Wait Reason", s.WaitReason ?? ""));
                blocks.Add(M("Stack Root Count", $"{s.StackRootCount:N0}", s.StackRootCount));
                blocks.Add(M("Stack Size", s.StackSizeBytes > 0 ? $"{s.StackSizeBytes:N0}" : "—", s.StackSizeBytes));
                for (int f = 0; f < s.TopFrames.Count; f++)
                    blocks.Add(T(s.TopFrames[f], 2));
            }
        }

        if (d.AppDomainDistribution is { Count: > 0 })
        {
            var appRows = new List<TableRow>(d.AppDomainDistribution.Count);
            foreach (var kvp in d.AppDomainDistribution)
                appRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            tables.Add(ST("AppDomain thread distribution", ["AppDomain", "Thread Count"], appRows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}

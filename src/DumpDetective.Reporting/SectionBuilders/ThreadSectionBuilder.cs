using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Thread Analysis";
    public int SortOrder => 12;

    public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ThreadDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("THREAD SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Threads", $"{d.TotalThreadCount:N0}", d.TotalThreadCount));
        blocks.Add(M("Alive Threads", $"{d.AliveThreadCount:N0}", d.AliveThreadCount));
        blocks.Add(M("Background Threads", $"{d.BackgroundThreadCount:N0}", d.BackgroundThreadCount));
        blocks.Add(M("Thread Pool Workers", $"{d.ThreadPoolWorkerCount:N0}", d.ThreadPoolWorkerCount));
        blocks.Add(M("Blocked Threads", $"{d.BlockedThreadCount:N0}", d.BlockedThreadCount));
        blocks.Add(M("Lock-Holding Threads", $"{d.LockHoldingThreadCount:N0}", d.LockHoldingThreadCount));

        // Finalizer
        blocks.Add(Blank());
        blocks.Add(H("FINALIZER THREAD"));
        blocks.Add(Divider());
        if (d.FinalizerManagedThreadId.HasValue)
            blocks.Add(M("Finalizer Thread ID", $"{d.FinalizerManagedThreadId.Value:N0}", d.FinalizerManagedThreadId.Value));
        blocks.Add(M("Finalizer Blocked", d.FinalizerThreadBlocked ? "Yes" : "No"));
        blocks.Add(M("Finalizer Lock Count", $"{d.FinalizerLockCount:N0}", d.FinalizerLockCount));

        var finFrames = d.FinalizerFrames ?? [];
        if (finFrames.Count > 0)
        {
            blocks.Add(H("Finalizer Frames:", 1));
            for (int i = 0; i < finFrames.Count; i++)
                blocks.Add(T(finFrames[i], 2));
        }

        // Async
        blocks.Add(Blank());
        blocks.Add(H("ASYNC CHAIN ANALYSIS"));
        blocks.Add(Divider());
        blocks.Add(M("Async Chain Threads", $"{d.AsyncChainThreadCount:N0}", d.AsyncChainThreadCount));
        blocks.Add(M("Max Async Chain Depth", $"{d.MaxAsyncChainDepth:N0}", d.MaxAsyncChainDepth));

        // Wait category distribution
        if (d.WaitPatternBreakdown.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("WAIT CATEGORY DISTRIBUTION"));
            blocks.Add(Divider());

            var wcRows = new List<TableRow>(d.WaitPatternBreakdown.Count);
            foreach (var kvp in d.WaitPatternBreakdown)
                wcRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            blocks.Add(new TableBlock("Wait category distribution", ["Category", "Count"], wcRows));
        }

        // Top frame hotspots
        var hotspots = d.TopStackHotspots ?? [];
        if (hotspots.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP FRAME HOTSPOTS"));
            blocks.Add(Divider());

            var hsRows = new List<TableRow>(hotspots.Count);
            for (int i = 0; i < hotspots.Count; i++)
                hsRows.Add(new TableRow([Cell(hotspots[i].Name), Cell($"{hotspots[i].Count:N0}", hotspots[i].Count)]));
            blocks.Add(new TableBlock("Top frame hotspots", ["Frame", "Count"], hsRows));
        }

        // Sampled thread snapshots
        // show sampling metadata if available
        if (d.SamplingCapacity > 0 || d.SampledSnapshotCount > 0)
        {
            blocks.Add(Blank());
            blocks.Add(M("Sampled Snapshots", $"{d.SampledSnapshotCount:N0} sampled (capacity {d.SamplingCapacity:N0})", d.SampledSnapshotCount));
            blocks.Add(M("Sampling Seed", $"0x{d.SamplingSeed:X8}"));
        }

        var sampled = d.SampledThreads ?? [];
        if (sampled.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("SAMPLED THREAD SNAPSHOTS"));
            blocks.Add(Divider());

            for (int i = 0; i < sampled.Count; i++)
            {
                var s = sampled[i];
                // Determine whether this snapshot was captured (top-N) or sampled.
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

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}

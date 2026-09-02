using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ThreadFindingGenerator : IFindingGenerator
{
    private const double HighBlockedRatioThreshold = 0.70;
    private const int DeepAsyncChainThreshold = 10;

    public string AnalyzerName => "Thread Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ThreadDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ThreadDomainResult r) return [];

        var findings = new List<InsightFinding>(5);

        FindingSeverity summarySeverity = r.ThreadsWithActiveExceptionsCount > 0 || r.BlockedThreadCount >= 10
            ? FindingSeverity.Warning : FindingSeverity.Info;

        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Threading",
            Severity: summarySeverity,
            Title: "Thread-state triage summary",
            Evidence: $"Alive threads: {r.AliveThreadCount:N0}; blocked-pattern threads: {r.BlockedThreadCount:N0}; lock-holding threads: {r.LockHoldingThreadCount:N0}; active thread exceptions: {r.ThreadsWithActiveExceptionsCount:N0}.",
            Recommendation: "Correlate blocked groups with lock owners hotspot frames to isolate contention/deadlock candidates.",
            Tags: ["threads", "locks", "blocked", "exceptions"],
            MetricValue: r.BlockedThreadCount,
            MetricUnit: "blocked-threads"));

        if (r.FinalizerThreadBlocked)
        {
            string threadId = r.FinalizerManagedThreadId.HasValue ? $"managed thread {r.FinalizerManagedThreadId.Value}" : "the finalizer thread";
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: FindingSeverity.Critical,
                Title: "Finalizer thread is blocked",
                Evidence: $"The finalizer thread ({threadId}) is currently blocked with {r.FinalizerLockCount:N0} held lock(s). " +
                    "A blocked finalizer halts finalization for the entire process, which can cause unbounded growth of finalizable objects awaiting cleanup.",
                Recommendation: "Inspect the finalizer thread's stack for the object/lock it is waiting on and resolve the contention or long-running finalizer method blocking it.",
                Tags: ["threads", "finalizer", "blocked"],
                MetricValue: r.FinalizerLockCount,
                MetricUnit: "held-locks"));
        }

        if (r.BlockedThreadRatio > HighBlockedRatioThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: FindingSeverity.Critical,
                Title: $"High blocked-thread ratio: {r.BlockedThreadRatio:P1} of alive threads",
                Evidence: $"{r.BlockedThreadCount:N0} of {r.AliveThreadCount:N0} alive threads ({r.BlockedThreadRatio:P1}) are blocked on a wait/lock pattern. " +
                    "A ratio this high is a strong starvation or deadlock signal rather than isolated contention.",
                Recommendation: "Review the wait-pattern breakdown and lock-holding threads to identify the primary contended resource causing widespread blocking.",
                Tags: ["threads", "blocked", "starvation", "deadlock"],
                MetricValue: r.BlockedThreadRatio,
                MetricUnit: "ratio"));
        }

        int activeProcessingThreadCount = 0;
        if (r.TopActiveThreadHotspots != null)
        {
            for (int i = 0; i < r.TopActiveThreadHotspots.Count; i++)
                activeProcessingThreadCount += r.TopActiveThreadHotspots[i].Count;
        }

        if (r.AliveThreadCount > 0 && activeProcessingThreadCount == 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: FindingSeverity.Critical,
                Title: "Zero active threads — every alive thread is blocked, GC, or finalizer",
                Evidence: $"None of the {r.AliveThreadCount:N0} alive threads are performing active (non-blocked, non-GC, non-finalizer) work. " +
                    "This indicates the process has stopped making forward progress entirely.",
                Recommendation: "Treat this as a full hang: inspect the blocked-thread and lock-holding tables for a deadlock cycle or an external resource (I/O, remote call) all threads are waiting on.",
                Tags: ["threads", "hang", "deadlock"],
                MetricValue: r.AliveThreadCount,
                MetricUnit: "alive-threads"));
        }

        if (r.MaxAsyncChainDepth > DeepAsyncChainThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: FindingSeverity.Warning,
                Title: $"Deep async continuation chain: {r.MaxAsyncChainDepth:N0} MoveNext frames",
                Evidence: $"The deepest observed async continuation chain is {r.MaxAsyncChainDepth:N0} MoveNext frames deep, across {r.AsyncChainThreadCount:N0} thread(s) carrying async chains. " +
                    "Chains this deep are uncommon in healthy async code and may indicate recursive continuations or an async deadlock/livelock.",
                Recommendation: "Trace the deepest chain's frames to confirm whether it reflects legitimate nested awaits or a continuation loop that is not completing.",
                Tags: ["threads", "async", "continuation-chain"],
                MetricValue: r.MaxAsyncChainDepth,
                MetricUnit: "moveNext-frames"));
        }

        return findings;
    }
}

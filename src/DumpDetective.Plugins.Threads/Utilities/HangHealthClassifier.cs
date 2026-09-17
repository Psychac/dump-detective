using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Sole owner of the Hang analyzer's judgment calls (health score, starvation flag). Stage 1
/// (<c>HangAnalyzer</c>) emits only raw measures on <see cref="HangDomainResult"/>; every consumer
/// (finding generator, section builder, trend comparer, executive summary) calls here instead of
/// re-deriving its own threshold logic, so there is exactly one place these numbers can disagree
/// with themselves. See docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md, Smell A.
/// </summary>
internal static class HangHealthClassifier
{
    /// <summary>
    /// Produces a 0-100 composite score. Each penalty maps to a named, observable signal so the
    /// score degrades predictably — not arbitrarily.
    /// </summary>
    public static int ComputeHealthScore(HangDomainResult r)
    {
        int score = 100;

        // Waiting-thread pressure (up to -40)
        if (r.WaitingPercent >= 80) score -= 40;
        else if (r.WaitingPercent >= 50) score -= 25;
        else if (r.WaitingPercent >= 30) score -= 10;

        // Probable deadlock candidates: monitor-wait + holding a lock (up to -30)
        int circularCandidates = 0;
        if (r.TopWaitingThreads is not null)
        {
            foreach (WaitingThreadSnapshot t in r.TopWaitingThreads)
            {
                if (t.WaitType == "MonitorWait" && t.LockCount > 0)
                    circularCandidates++;
            }
        }
        score -= Math.Min(circularCandidates * 15, 30);

        // Thread pool health (up to -15)
        bool saturated = r.RuntimeMaxThreads > 0 && r.RuntimeActiveWorkerThreads >= r.RuntimeMaxThreads;
        bool starvation = saturated && r.RuntimeCpuUtilization < 20;
        if (starvation) score -= 15;
        else if (saturated) score -= 10;

        // Task backpressure (-5)
        if (r.PendingTasks > r.HighThreadPoolThreshold) score -= 5;

        // Unobserved task faults (-5)
        if (r.FaultedTasks > 0) score -= 5;

        // Async continuation backlog (-5)
        if (r.TotalTaskContinuations > 1000) score -= 5;

        return Math.Max(0, Math.Min(100, score));
    }

    public static bool IsStarved(HangDomainResult r) =>
        r.RuntimeThreadPoolDataAvailable &&
        r.RuntimeMaxThreads > 0 &&
        r.RuntimeQueueLength.GetValueOrDefault() > 0 &&
        r.RuntimeActiveWorkerThreads >= r.RuntimeMaxThreads;
}

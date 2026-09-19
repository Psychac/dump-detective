using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>ThreadFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>BlockedThreadCount</c>/<c>LockHoldingThreadCount</c>/<c>ActiveProcessingThreadCount</c> all
/// come from <c>runtime.thread-triage</c>'s own measures rather than counting matched
/// <c>thread.state</c> observations — unlike <c>LockGraphSynthesisRule</c>'s
/// <c>lock.contested</c>/<c>thread.blocked</c> counting, <c>ThreadsWithActiveExceptionsCount</c>
/// spans dead threads too (no <c>thread.state</c> stand-in exists for those), so all the whole-run
/// scalars were kept together in one observation rather than splitting derivation strategies
/// per-field.
/// </remarks>
internal sealed class ThreadSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Thread Analysis";
    private const double HighBlockedRatioThreshold = 0.70;
    private const int DeepAsyncChainThreshold = 10;

    public string RuleId => "thread.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.thread-triage", "runtime.finalizer-thread"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? triage = null;
        Observation? finalizerThread = null;

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.thread-triage":
                    triage = observation;
                    break;
                case "runtime.finalizer-thread":
                    finalizerThread = observation;
                    break;
            }
        }

        if (triage is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var findings = new List<Finding>(5);

        int aliveThreadCount = (int)triage.Measures["alive-thread.count"].Value;
        int blockedThreadCount = (int)triage.Measures["blocked-thread.count"].Value;
        int lockHoldingThreadCount = (int)triage.Measures["lock-holding-thread.count"].Value;
        int threadsWithActiveExceptionsCount = (int)triage.Measures["active-exception-thread.count"].Value;
        int activeProcessingThreadCount = (int)triage.Measures["active-processing-thread.count"].Value;
        int asyncChainThreadCount = (int)triage.Measures["async-chain-thread.count"].Value;
        int maxAsyncChainDepth = (int)triage.Measures["max-async-chain.depth"].Value;
        double blockedThreadRatio = aliveThreadCount > 0 ? (double)blockedThreadCount / aliveThreadCount : 0.0;

        Severity summarySeverity = threadsWithActiveExceptionsCount > 0 || blockedThreadCount >= 10
            ? Severity.Warning : Severity.Info;

        findings.Add(new Finding
        {
            Fingerprint = Fingerprint("triage-summary"),
            Analyzer = AnalyzerName,
            Category = "Threading",
            Severity = summarySeverity,
            Title = "Thread-state triage summary",
            Narrative = $"Alive threads: {aliveThreadCount:N0}; blocked-pattern threads: {blockedThreadCount:N0}; lock-holding threads: {lockHoldingThreadCount:N0}; active thread exceptions: {threadsWithActiveExceptionsCount:N0}.",
            Recommendation = "Correlate blocked groups with lock owners hotspot frames to isolate contention/deadlock candidates.",
            Tags = ["threads", "locks", "blocked", "exceptions"],
            MetricValue = blockedThreadCount,
            MetricUnit = "blocked-threads",
            DerivedFrom = [triage.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        if (finalizerThread is not null && finalizerThread.Measures["is-blocked"].Value >= 1.0)
        {
            var finalizerRef = (ThreadRef)finalizerThread.Subjects[0];
            int finalizerLockCount = (int)finalizerThread.Measures["locks-held.count"].Value;
            string threadId = finalizerRef.ManagedThreadId.HasValue ? $"managed thread {finalizerRef.ManagedThreadId.Value}" : "the finalizer thread";

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("finalizer-blocked"),
                Analyzer = AnalyzerName,
                Category = "Threading",
                Severity = Severity.Critical,
                Title = "Finalizer thread is blocked",
                Narrative = $"The finalizer thread ({threadId}) is currently blocked with {finalizerLockCount:N0} held lock(s). " +
                    "A blocked finalizer halts finalization for the entire process, which can cause unbounded growth of finalizable objects awaiting cleanup.",
                Recommendation = "Inspect the finalizer thread's stack for the object/lock it is waiting on and resolve the contention or long-running finalizer method blocking it.",
                Tags = ["threads", "finalizer", "blocked"],
                MetricValue = finalizerLockCount,
                MetricUnit = "held-locks",
                DerivedFrom = [triage.Id, finalizerThread.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (blockedThreadRatio > HighBlockedRatioThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("high-blocked-ratio"),
                Analyzer = AnalyzerName,
                Category = "Threading",
                Severity = Severity.Critical,
                Title = $"High blocked-thread ratio: {blockedThreadRatio:P1} of alive threads",
                Narrative = $"{blockedThreadCount:N0} of {aliveThreadCount:N0} alive threads ({blockedThreadRatio:P1}) are blocked on a wait/lock pattern. " +
                    "A ratio this high is a strong starvation or deadlock signal rather than isolated contention.",
                Recommendation = "Review the wait-pattern breakdown and lock-holding threads to identify the primary contended resource causing widespread blocking.",
                Tags = ["threads", "blocked", "starvation", "deadlock"],
                MetricValue = blockedThreadRatio,
                MetricUnit = "ratio",
                DerivedFrom = [triage.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (aliveThreadCount > 0 && activeProcessingThreadCount == 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("zero-active-threads"),
                Analyzer = AnalyzerName,
                Category = "Threading",
                Severity = Severity.Critical,
                Title = "Zero active threads — every alive thread is blocked, GC, or finalizer",
                Narrative = $"None of the {aliveThreadCount:N0} alive threads are performing active (non-blocked, non-GC, non-finalizer) work. " +
                    "This indicates the process has stopped making forward progress entirely.",
                Recommendation = "Treat this as a full hang: inspect the blocked-thread and lock-holding tables for a deadlock cycle or an external resource (I/O, remote call) all threads are waiting on.",
                Tags = ["threads", "hang", "deadlock"],
                MetricValue = aliveThreadCount,
                MetricUnit = "alive-threads",
                DerivedFrom = [triage.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (maxAsyncChainDepth > DeepAsyncChainThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("deep-async-chain"),
                Analyzer = AnalyzerName,
                Category = "Threading",
                Severity = Severity.Warning,
                Title = $"Deep async continuation chain: {maxAsyncChainDepth:N0} MoveNext frames",
                Narrative = $"The deepest observed async continuation chain is {maxAsyncChainDepth:N0} MoveNext frames deep, across {asyncChainThreadCount:N0} thread(s) carrying async chains. " +
                    "Chains this deep are uncommon in healthy async code and may indicate recursive continuations or an async deadlock/livelock.",
                Recommendation = "Trace the deepest chain's frames to confirm whether it reflects legitimate nested awaits or a continuation loop that is not completing.",
                Tags = ["threads", "async", "continuation-chain"],
                MetricValue = maxAsyncChainDepth,
                MetricUnit = "moveNext-frames",
                DerivedFrom = [triage.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

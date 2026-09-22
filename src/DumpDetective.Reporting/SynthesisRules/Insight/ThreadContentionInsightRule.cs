using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectThreadContention</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// The original's Hang-branch evidence read <c>hang.ThreadsHoldingLocks</c> — a field
/// <c>HangDomainResult</c> carries but <c>runtime.hang-pressure</c> never observed. Rather than add
/// a new measure duplicating a value <c>ThreadAnalyzer</c> already observes independently
/// (<c>runtime.thread-triage</c>'s <c>lock-holding-thread.count</c> — the same underlying
/// lock-holding-thread count both analyzers derive from), this rule reads that instead in both the
/// Hang-branch and the ThreadDomainResult-fallback branch, which already used it under a different
/// evidence label. Preserves the original's control flow exactly, including the early
/// <c>return</c> after the Hang-branch fires (the separate "blocked finalizer thread" finding below
/// is unreachable whenever the Hang-branch already fired, in the original too — not a fix, a
/// faithful port).
/// </remarks>
internal sealed class ThreadContentionInsightRule : ISynthesisRule
{
    private const double WarningPct = 50.0;
    private const double CriticalPct = 75.0;

    public string RuleId => "insight.thread-contention";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.hang-pressure", "runtime.thread-triage", "runtime.finalizer-thread"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? hang = null, threadTriage = null, finalizerThread = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.hang-pressure": hang = observation; break;
                case "runtime.thread-triage": threadTriage = observation; break;
                case "runtime.finalizer-thread": finalizerThread = observation; break;
            }
        }

        long lockHoldingThreadCount = (long)(threadTriage?.Measures["lock-holding-thread.count"].Value ?? 0);

        if (hang is not null && hang.Measures["waiting.percent"].Value >= WarningPct)
        {
            double waitingPercent = hang.Measures["waiting.percent"].Value;
            long waitingThreadCount = (long)hang.Measures["waiting-thread.count"].Value;

            Severity severity = waitingPercent >= CriticalPct ? Severity.Critical : Severity.Warning;
            var derivedFrom = new List<ObservationId> { hang.Id };
            if (threadTriage is not null) derivedFrom.Add(threadTriage.Id);

            return ValueTask.FromResult<IReadOnlyList<Finding>>(
            [
                new Finding
                {
                    Fingerprint = "insightengine::thread-contention-hang",
                    Analyzer = "InsightEngine",
                    Category = "Threads",
                    Severity = severity,
                    Title = "High proportion of threads are blocked or waiting",
                    Narrative = $"{waitingPercent:F1}% of threads waiting ({waitingThreadCount} / total threads). " +
                                $"Threads holding locks: {lockHoldingThreadCount}.",
                    Recommendation = "Inspect blocked threads for lock contention, deadlocks, or async-over-sync " +
                                     "anti-patterns. Review the Hang and ThreadAnalyzer findings for specific call stacks.",
                    Tags = ["threads", "contention", "hang", "blocking"],
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                },
            ]);
        }

        if (threadTriage is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long alive = (long)threadTriage.Measures["alive-thread.count"].Value;
        if (alive == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long blocked = (long)threadTriage.Measures["blocked-thread.count"].Value;
        bool finalizerThreadBlocked = finalizerThread is not null && finalizerThread.Measures["is-blocked"].Value != 0;

        double blockedPct = blocked * 100.0 / alive;
        var findings = new List<Finding>();

        if (blockedPct >= WarningPct)
        {
            Severity severity = blockedPct >= CriticalPct ? Severity.Critical : Severity.Warning;

            findings.Add(new Finding
            {
                Fingerprint = "insightengine::thread-contention-blocked",
                Analyzer = "InsightEngine",
                Category = "Threads",
                Severity = severity,
                Title = "High proportion of threads are blocked",
                Narrative = $"{blockedPct:F1}% of alive threads blocked ({blocked}/{alive}). " +
                            $"Lock-holding threads: {lockHoldingThreadCount}. " +
                            (finalizerThreadBlocked ? "Finalizer thread is blocked. " : string.Empty),
                Recommendation = "Review lock usage, async-over-sync patterns, and finalizer overhead. " +
                                 "A blocked finalizer thread can cause memory to accumulate.",
                Tags = ["threads", "blocking", "contention"],
                DerivedFrom = finalizerThread is not null ? [threadTriage.Id, finalizerThread.Id] : [threadTriage.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Separately flag a blocked finalizer thread regardless of overall blocked-%.
        if (finalizerThreadBlocked)
        {
            uint osThreadId = ((ThreadRef)finalizerThread!.Subjects[0]).OsThreadId;
            long lockCount = (long)finalizerThread.Measures["locks-held.count"].Value;

            findings.Add(new Finding
            {
                Fingerprint = "insightengine::thread-contention-finalizer-blocked",
                Analyzer = "InsightEngine",
                Category = "Threads",
                Severity = Severity.Critical,
                Title = "Finalizer thread is blocked",
                Narrative = $"Finalizer thread (OS tid: {osThreadId}) is blocked " +
                            $"with {lockCount} lock(s) held.",
                Recommendation = "A blocked finalizer thread prevents finalization of all objects in the queue. " +
                                 "Investigate the finalizer thread's call stack and remove blocking operations from finalizers.",
                Tags = ["finalizer", "threads", "blocking", "gc"],
                DerivedFrom = [finalizerThread.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }
}

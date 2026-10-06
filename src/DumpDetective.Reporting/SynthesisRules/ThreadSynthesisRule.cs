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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        var findings = new List<Claim>(5);

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

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("triage-summary"),
            Assertion = BuildSummaryAssertion(summarySeverity, aliveThreadCount, blockedThreadCount, threadsWithActiveExceptionsCount),
            Support = [new EvidenceStatement([triage.Id], $"{aliveThreadCount:N0} alive threads: {blockedThreadCount:N0} blocked, {lockHoldingThreadCount:N0} holding locks, {threadsWithActiveExceptionsCount:N0} with an active exception.")],
            Counter = BuildSummaryCounter(triage, summarySeverity, activeProcessingThreadCount, threadsWithActiveExceptionsCount),
            FalsifiedBy = BuildSummaryFalsifiedBy(aliveThreadCount, blockedThreadCount, threadsWithActiveExceptionsCount),
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("finalizer-blocked"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Blocks,
                    Subject = finalizerRef,
                    Measure = ClaimParts.Count(finalizerLockCount),
                    Qualifiers = new AssertionQualifiers { Location = "the finalizer thread", Mechanism = "a held lock" },
                },
                Support =
                [
                    new EvidenceStatement([finalizerThread.Id], $"The finalizer thread is recorded as blocked while holding {finalizerLockCount:N0} lock(s)."),
                    new EvidenceStatement([triage.Id], "Supplies the process-wide thread counts the finalizer thread is read against."),
                ],
                Counter = Counter.None("A thread seen blocked at capture time may be about to resume; one snapshot cannot show the wait is permanent."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!threads",
                        Test = "If the finalizer thread's stack in !clrstack is not parked on a wait or lock, it is not blocked.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump a few minutes later; if the finalizer thread has moved on and the finalizer queue is draining, the block was transient.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("high-blocked-ratio"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Stalls,
                    Measure = ClaimParts.Percent(blockedThreadRatio * 100.0),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(HighBlockedRatioThreshold * 100.0), "high blocked-ratio threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "alive threads in a blocked pattern" },
                },
                Support = [new EvidenceStatement([triage.Id], $"{blockedThreadCount:N0} of {aliveThreadCount:N0} alive threads match a blocked wait or lock pattern.")],
                Counter = activeProcessingThreadCount > 0
                    ? Counter.Of(new EvidenceStatement([triage.Id], $"{activeProcessingThreadCount:N0} threads are actively processing, so the process is still making progress."))
                    : Counter.None("Idle thread-pool workers parked on a wait count as blocked here; nothing observed separates idle waits from stalled ones."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!threadpool",
                        Test = $"If most of the {blockedThreadCount:N0} blocked threads are idle thread-pool workers, the ratio reflects spare capacity, not starvation.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump a few minutes later; if the blocked threads are different ones at different frames, the waits are short-lived churn.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("zero-active-threads"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Stalls,
                    Measure = ClaimParts.Count(activeProcessingThreadCount),
                    Qualifiers = new AssertionQualifiers { Location = "alive threads doing active work" },
                },
                Support = [new EvidenceStatement([triage.Id], $"None of the {aliveThreadCount:N0} alive threads is doing active (non-blocked, non-GC, non-finalizer) work.")],
                Counter = Counter.None("A process captured while idle, with no requests in flight, looks the same as one that has stopped making progress."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump a few minutes later; if any thread is executing application code, the process was idle, not hung.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!syncblk",
                        Test = "If !syncblk shows no held monitors and the blocked threads wait on I/O, the stall is an external dependency, not a deadlock.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("deep-async-chain"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(maxAsyncChainDepth),
                    Reference = ClaimParts.Budget(ClaimParts.Count(DeepAsyncChainThreshold), "deep async chain threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the deepest async continuation chain" },
                },
                Support = [new EvidenceStatement([triage.Id], $"The deepest chain is {maxAsyncChainDepth:N0} MoveNext frames, across {asyncChainThreadCount:N0} thread(s) carrying async chains.")],
                Counter = Counter.None("Depth alone does not separate legitimately nested awaits from a continuation loop that is not completing."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!clrstack",
                        Test = "On the thread with the deepest chain: if the MoveNext frames belong to different methods advancing in order, they are nested awaits, not a loop.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the awaiting code recurses by design, with a bounded depth, the chain is expected.",
                    },
                ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static Assertion BuildSummaryAssertion(Severity severity, int aliveThreadCount, int blockedThreadCount, int threadsWithActiveExceptionsCount)
    {
        if (threadsWithActiveExceptionsCount > 0)
            return new Assertion
            {
                Predicate = AssertionPredicate.Exhibits,
                Measure = ClaimParts.Count(threadsWithActiveExceptionsCount),
                Qualifiers = new AssertionQualifiers { Location = "threads with an active exception" },
            };

        if (severity == Severity.Warning)
            return new Assertion
            {
                Predicate = AssertionPredicate.Blocks,
                Measure = ClaimParts.Count(blockedThreadCount),
                Reference = ClaimParts.Budget(ClaimParts.Count(BlockedThreadWarningCount), "blocked-thread threshold"),
                Qualifiers = new AssertionQualifiers { Location = "alive threads in a blocked pattern" },
            };

        return new Assertion
        {
            Predicate = AssertionPredicate.Composes,
            Measure = ClaimParts.Count(aliveThreadCount),
            Qualifiers = new AssertionQualifiers { Location = "alive threads by state" },
        };
    }

    private const int BlockedThreadWarningCount = 10;

    private static Counter BuildSummaryCounter(
        Observation triage, Severity severity, int activeProcessingThreadCount, int threadsWithActiveExceptionsCount)
    {
        if (severity != Severity.Warning)
            return Counter.None("This is a description of thread states and asserts no problem, so there is nothing to weaken.");

        if (threadsWithActiveExceptionsCount > 0)
            return Counter.None("A thread with an active exception has not necessarily failed; the exception may be caught and handled further up the stack.");

        return activeProcessingThreadCount > 0
            ? Counter.Of(new EvidenceStatement([triage.Id], $"{activeProcessingThreadCount:N0} threads are actively processing, so the process is not stalled."))
            : Counter.None("Idle thread-pool workers parked on a wait count as blocked; nothing observed separates idle waits from stalled ones.");
    }

    private static IReadOnlyList<FalsificationTest> BuildSummaryFalsifiedBy(
        int aliveThreadCount, int blockedThreadCount, int threadsWithActiveExceptionsCount) =>
        threadsWithActiveExceptionsCount > 0
        ?
        [
            new FalsificationTest
            {
                Kind = FalsificationKind.Windbg,
                Command = "!threads",
                Test = $"If !threads shows an exception on a number of threads other than {threadsWithActiveExceptionsCount:N0}, the count is wrong.",
            },
            new FalsificationTest
            {
                Kind = FalsificationKind.Windbg,
                Command = "!pe",
                Test = "On each such thread: if !pe shows no exception object, or one already handled, there is no active failure.",
            },
        ]
        :
        [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!threads",
            Test = $"If !threads lists a number of live threads other than {aliveThreadCount:N0}, or {blockedThreadCount:N0} of them are not parked in a wait, the triage counts are wrong.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

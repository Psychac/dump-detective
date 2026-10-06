using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectFinalizerQueueBacklog</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// The original also took a <c>DominatorDomainResult? leak</c> parameter that its body never reads
/// — dropped here, not ported, since carrying it forward would mean querying
/// <c>type.retention-population</c> for no reason.
/// </remarks>
internal sealed class FinalizerQueueBacklogInsightRule : ISynthesisRule
{
    private const int WarningCount = 1_000;
    private const int CriticalCount = 10_000;

    public string RuleId => "insight.finalizer-queue-backlog";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["finalizer.queue-population", "runtime.finalizer-thread"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? queue = null, finalizerThread = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "finalizer.queue-population": queue = observation; break;
                case "runtime.finalizer-thread": finalizerThread = observation; break;
            }
        }

        long queueCount = (long)(queue?.Measures["queue.count"].Value ?? 0);
        if (queueCount < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Severity severity = queueCount >= CriticalCount ? Severity.Critical : Severity.Warning;

        bool finalizerBlocked = finalizerThread is not null && finalizerThread.Measures["is-blocked"].Value != 0;
        ulong retainedBytes = (ulong)(queue?.Measures["retained.bytes"].Value ?? 0);

        // Starvation risk: large retained sub-graphs + blocked finalizer thread
        if (finalizerBlocked && retainedBytes > 0)
            severity = Severity.Critical;

        string retainedPart = retainedBytes > 0
            ? $" Estimated retained memory in queue sub-graphs: {InsightByteFormat.FormatBytes(retainedBytes)}."
            : string.Empty;

        var derivedFrom = new List<ObservationId> { queue!.Id };
        if (finalizerThread is not null) derivedFrom.Add(finalizerThread.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::finalizer-queue-backlog",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(queueCount),
                    Reference = queueCount >= CriticalCount
                        ? ClaimParts.Budget(ClaimParts.Count(CriticalCount), "critical threshold")
                        : ClaimParts.Budget(ClaimParts.Count(WarningCount), "warning threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = "objects in the finalizer queue",
                        Mechanism = finalizerBlocked ? "the finalizer thread is blocked" : null,
                    },
                },
                Support =
                [
                    new EvidenceStatement([queue!.Id], $"The finalizer queue holds {queueCount:N0} objects, retaining about {InsightByteFormat.FormatBytes(retainedBytes)}."),
                    .. finalizerThread is not null
                        ? [new EvidenceStatement([finalizerThread.Id], finalizerBlocked ? "The finalizer thread is blocked, so the queue cannot drain." : "The finalizer thread is not blocked.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = finalizerThread is not null && !finalizerBlocked
                    ? Counter.Of(new EvidenceStatement([finalizerThread.Id], "The finalizer thread is running and not blocked, so the queue is draining and may be a transient burst."))
                    : Counter.None("The queue fills after a burst of finalizable allocations and drains once the finalizer thread runs; one snapshot does not show it is stuck."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!finalizequeue",
                        Test = $"If !finalizequeue reports far fewer than {queueCount:N0} objects ready for finalization, the backlog is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!threads",
                        Test = "If the finalizer thread is running and not blocked, the queue is draining.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "Large finalizer queue backlog detected",
                Narrative = $"{queueCount:N0} objects are waiting in the finalizer queue.{retainedPart} " +
                            (finalizerBlocked ? "Finalizer thread is currently blocked — starvation risk." : string.Empty),
                Recommendation = "Objects with finalizers hold memory for at least two GC cycles. " +
                                 "Prefer IDisposable + using/Dispose over finalizers. " +
                                 "Implement IDisposable in finalizable types and call GC.SuppressFinalize.",
                Tags = ["finalizer", "gc", "memory-leak", "dispose"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}

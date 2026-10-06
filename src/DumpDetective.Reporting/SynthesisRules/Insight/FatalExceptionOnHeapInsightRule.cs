using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectFatalExceptionOnHeap</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>CrashDomainResult.ExceptionTypeCounts</c> (every exception type on heap, not just active
/// ones) had no observation of its own before this — only <c>runtime.active-exception</c>
/// (per-instance, active only) existed. <c>CrashAnalyzer</c> now also emits a bounded
/// <c>runtime.exception-type</c> observation per distinct exception type
/// (<c>CrashAnalyzer.EmitObservationsAsync</c>), unblocking this rule and several others in the
/// same group.
/// </remarks>
internal sealed class FatalExceptionOnHeapInsightRule : ISynthesisRule
{
    private static readonly HashSet<string> FatalExceptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.OutOfMemoryException",
        "System.StackOverflowException",
        "System.ExecutionEngineException",
        "System.AccessViolationException",
    };

    public string RuleId => "insight.fatal-exception-on-heap";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-population", "runtime.exception-type"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var exceptionTypes = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "runtime.crash-population")
                population = observation;
            else if (observation.ObservationType == "runtime.exception-type")
                exceptionTypes.Add(observation);
        }

        long totalExceptions = (long)(population?.Measures["total.count"].Value ?? 0);
        if (population is null || totalExceptions == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        var fatalFound = new List<string>();
        var derivedFrom = new List<ObservationId> { population.Id };
        foreach (Observation exceptionType in exceptionTypes)
        {
            string typeName = ((TypeRef)exceptionType.Subjects[0]).CanonicalName;
            if (!FatalExceptionTypes.Contains(typeName))
                continue;

            long count = (long)exceptionType.Measures["instance.count"].Value;
            fatalFound.Add($"{typeName} ×{count}");
            derivedFrom.Add(exceptionType.Id);
        }

        if (fatalFound.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::fatal-exception-on-heap",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(fatalFound.Count),
                    Qualifiers = new AssertionQualifiers { Location = "fatal exception types on the managed heap", Mechanism = string.Join(", ", fatalFound) },
                },
                Support = [new EvidenceStatement(derivedFrom, $"Fatal exception objects are present: {string.Join(", ", fatalFound)}.")],
                Counter = Counter.None("A fatal exception object on the heap may be a leftover that was caught and handled; the dump does not show the process was ever terminated."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <exception address>",
                        Test = "On a sampled fatal exception: if !gcroot finds no root, it is garbage from a handled event, not a current condition.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the application catches OutOfMemoryException deliberately to degrade gracefully, the object is an expected result.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Crash",
                Severity = Severity.Critical,
                Title = "Fatal exception type(s) found on managed heap",
                Narrative = $"Fatal exception object(s) present: {string.Join(", ", fatalFound)}. " +
                            "These exception types indicate a previous near-fatal process event.",
                Recommendation = "OutOfMemoryException: reduce allocations, enable large address space, or scale out. " +
                                 "StackOverflowException: review recursive call depth and unbounded recursion. " +
                                 "ExecutionEngineException: indicates CLR corruption — check native interop and upgrade runtime. " +
                                 "AccessViolationException: unsafe code or native interop writing beyond allocated buffers.",
                Tags = ["crash", "fatal-exception", "oom", "soe", "critical"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}

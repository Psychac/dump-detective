using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>StaticRootFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <b>Deliberately preserves <c>StaticRootAnalyzer</c>'s own known, documented bug</b> (its class
/// remarks: <c>HeapRootKind.StaticVar</c> never fires on ClrMD 4, so this analyzer produces zero
/// findings on every real dump) — same "mechanically port consumption logic, don't fix the
/// upstream bug" choice the analyzer's own retyping made, and the same choice
/// <c>StaticRootLeakDetector</c>'s retyping made per user call. This rule is correct for when the
/// upstream fix lands, not exercised on real dumps today.
/// <para>
/// The severity check's "largest root's memory impact" needs no dedicated observation: it's the
/// max <c>heap.static-root</c> <c>retained.bytes</c> across every analyzed root (significant or
/// not) by construction — a root large enough to be the overall max is always
/// >= <c>SignificantMemoryThresholdBytes</c>, hence always significant, so the global max and the
/// old generator's <c>topRoots[0].TotalMemoryImpact</c> (computed over the same unfiltered,
/// size-descending-sorted population) are always the same value.
/// </para>
/// <para>
/// The retention finding's confidence comes from the largest root's sampled GC root path
/// (<see cref="RootPathEvidenceStrength"/>), carried as raw facts on
/// <c>static-root.population</c> — the same root the old generator's
/// <c>EvidenceConfidence.Compute(topEvidence)</c> read.
/// </para>
/// </remarks>
internal sealed class StaticRootSynthesisRule : ISynthesisRule
{
    private const int RootCountCriticalThreshold = 10;
    private const ulong CriticalByteThreshold = 100 * 1_024 * 1_024;

    private const string AnalyzerName = "Static Root Leak Detection";

    public string RuleId => "static-root.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.static-root", "static-root.population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        ulong maxRetainedBytes = 0;
        var rootIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "static-root.population":
                    population = observation;
                    break;
                case "heap.static-root":
                    rootIds.Add(observation.Id);
                    ulong retainedBytes = (ulong)observation.Measures["retained.bytes"].Value;
                    if (retainedBytes > maxRetainedBytes) maxRetainedBytes = retainedBytes;
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long rootCount = (long)population.Measures["root.count"].Value;

        if (rootCount == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("no-candidates"),
                Analyzer = AnalyzerName,
                Category = "Leak",
                Severity = Severity.Info,
                Title = "No static-root retention candidates detected",
                Narrative = "No static roots with significant retained object graphs were found.",
                Recommendation = "No static-root action required for this snapshot.",
                Tags = ["static-root", "retention"],
                MetricValue = 0,
                MetricUnit = "retained-bytes",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Normal,
                    Measure = ClaimParts.Count(0),
                    Qualifiers = new AssertionQualifiers { Location = "static roots retaining significant object graphs" },
                },
                Support = [new EvidenceStatement([population.Id], "Counted no static roots above the significance cutoff.")],
                Counter = Counter.Of(new EvidenceStatement(
                    [population.Id],
                    "Only static roots retaining significant object graphs are reported; smaller retention is outside this check.")),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <address of the largest object>",
                        Test = "If the largest objects in !dumpheap -stat are rooted from a static field, a retaining static root was missed.",
                    },
                ],
            }]);
        }

        ulong totalRetainedBytes = (ulong)population.Measures["retained.bytes"].Value;

        Severity severity = rootCount >= RootCountCriticalThreshold || maxRetainedBytes > CriticalByteThreshold
            ? Severity.Critical
            : Severity.Warning;

        return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
        {
            Fingerprint = Fingerprint("retention-candidates"),
            Analyzer = AnalyzerName,
            Category = "Leak",
            Severity = severity,
            Title = "Static-root retention candidates detected",
            Narrative = $"{rootCount:N0} root(s) retain ~{FormatHelper.FormatBytes(totalRetainedBytes)} cumulative memory.",
            Recommendation = "Audit static ownership and clear or weaken references for expired object graphs.",
            Tags = ["static-root", "retention", "memory-leak"],
            MetricValue = totalRetainedBytes,
            MetricUnit = "retained-bytes",
            DerivedFrom = rootIds.Count > 0 ? [population.Id, .. rootIds] : [population.Id],
            Confidence = RootPathEvidenceStrength.FromMeasures(population.Measures, "top-root."),
            Assertion = BuildAssertion(severity, rootCount, maxRetainedBytes, totalRetainedBytes),
            Support = BuildSupport(population, rootIds, rootCount, totalRetainedBytes),
            Counter = Counter.None("Nothing observed separates a static cache kept on purpose from a static reference that was never cleared; these are candidates, not confirmed leaks."),
            FalsifiedBy = BuildFalsifiedBy(totalRetainedBytes),
        }]);
    }

    private static Assertion BuildAssertion(Severity severity, long rootCount, ulong maxRetainedBytes, ulong totalRetainedBytes)
    {
        bool tooManyRoots = rootCount >= RootCountCriticalThreshold;
        bool largestRootTooLarge = maxRetainedBytes > CriticalByteThreshold;

        if (severity == Severity.Critical && tooManyRoots)
            return new Assertion
            {
                Predicate = AssertionPredicate.Retains,
                Measure = ClaimParts.Count(rootCount),
                Reference = ClaimParts.Budget(ClaimParts.Count(RootCountCriticalThreshold), "critical root-count threshold"),
                Qualifiers = new AssertionQualifiers { Location = "static roots retaining significant object graphs" },
            };

        if (severity == Severity.Critical && largestRootTooLarge)
            return new Assertion
            {
                Predicate = AssertionPredicate.Retains,
                Measure = ClaimParts.Bytes(maxRetainedBytes),
                Reference = ClaimParts.Budget(ClaimParts.Bytes(CriticalByteThreshold), "critical per-root threshold"),
                Qualifiers = new AssertionQualifiers { Location = "the largest static root" },
            };

        return new Assertion
        {
            Predicate = AssertionPredicate.Retains,
            Measure = ClaimParts.Bytes(totalRetainedBytes),
            Qualifiers = new AssertionQualifiers { Location = "static roots retaining significant object graphs" },
        };
    }

    private static IReadOnlyList<EvidenceStatement> BuildSupport(
        Observation population, List<ObservationId> rootIds, long rootCount, ulong totalRetainedBytes)
    {
        var support = new List<EvidenceStatement>(2)
        {
            new([population.Id], $"Totals {rootCount:N0} root(s) retaining about {FormatHelper.FormatBytes(totalRetainedBytes)}."),
        };

        if (rootIds.Count > 0)
            support.Add(new(rootIds, "Each is a static root retaining a significant object graph."));

        return support;
    }

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(ulong totalRetainedBytes) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!gcroot <object address>",
            Test = "On an instance of the largest retained type: if the path starts at a thread stack or the finalizer queue, not a static handle, the static-root attribution is wrong.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.Capture,
            Test = $"Capture a second dump later under comparable load; if the roots still retain about {FormatHelper.FormatBytes(totalRetainedBytes)} and no more, this is a bounded cache, not growth.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = "If the owning static field is cleared or replaced on a schedule in the application code, the retention is bounded by design.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

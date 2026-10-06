using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>ThreadStackClusterFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// The dominant-cluster finding trusts emission order — <c>thread.stack-cluster</c> is emitted one
/// per <c>TopClusters</c> entry, already sorted descending by count, the same
/// already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies on — so the first
/// matched observation of that type is the dominant cluster. <c>FrameworkPattern != null</c> is
/// reconstructed from the presence of a second <c>Evidence</c> entry rather than reimplementing
/// <c>ThreadStackClusterAnalyzer.ClassifyFrameworkPattern</c>'s lookup table (internal, inaccessible
/// from Reporting) — the boolean fact crosses the boundary, not the classifier itself.
/// </remarks>
internal sealed class ThreadStackClusterSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Thread Stack Signature Clustering";

    public string RuleId => "thread-stack-cluster.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.thread-cluster-diversity", "thread.stack-cluster"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? diversity = null;
        Observation? dominantCluster = null;

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.thread-cluster-diversity":
                    diversity = observation;
                    break;
                case "thread.stack-cluster":
                    dominantCluster ??= observation;
                    break;
            }
        }

        if (diversity is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        var findings = new List<Claim>(2);

        int aliveThreadCount = (int)diversity.Measures["alive-thread.count"].Value;
        int uniqueClusters = (int)diversity.Measures["unique-cluster.count"].Value;
        double diversityPercent = diversity.Measures["diversity.percent"].Value;

        Severity severity = diversityPercent <= 25 ? Severity.Warning : Severity.Info;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("diversity"),
            Analyzer = AnalyzerName,
            Category = "Threading",
            Severity = severity,
            Title = "Thread stack-signature diversity",
            Narrative = $"{uniqueClusters:N0} unique signatures across {aliveThreadCount:N0} alive threads ({diversityPercent:F1}% diversity).",
            Recommendation = severity == Severity.Warning
                ? "Low diversity suggests hotspot wait/execution patterns; inspect top clusters for bottlenecks."
                : "Thread stack diversity appears healthy for this snapshot.",
            Tags = ["thread-cluster", "hotspot", "contention"],
            MetricValue = diversityPercent,
            MetricUnit = "% signature-diversity",
            DerivedFrom = [diversity.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Assertion = BuildDiversityAssertion(severity, diversityPercent),
            Support = [new EvidenceStatement([diversity.Id], $"{uniqueClusters:N0} unique stack signatures across {aliveThreadCount:N0} alive threads.")],
            Counter = severity == Severity.Warning
                ? Counter.None("Idle framework threads also produce low diversity; whether the largest cluster is such a pattern is judged in the dominant-cluster finding.")
                : Counter.None("Diversity is above the low-diversity threshold; no observation points at concentration."),
            FalsifiedBy = BuildDiversityFalsifiedBy(severity, uniqueClusters, aliveThreadCount),
        });

        if (dominantCluster is not null)
        {
            int dominantCount = (int)dominantCluster.Measures["cluster.count"].Value;
            double dominantPercent = aliveThreadCount > 0 ? dominantCount * 100.0 / aliveThreadCount : 0;
            string signature = dominantCluster.Evidence[0].Description ?? string.Empty;
            string? frameworkPattern = dominantCluster.Evidence.Count > 1 ? dominantCluster.Evidence[1].Description : null;
            bool isExpectedPattern = frameworkPattern is not null;
            Severity dominantSeverity = dominantPercent >= 50 && !isExpectedPattern ? Severity.Warning : Severity.Info;

            string recommendation;
            if (isExpectedPattern)
                recommendation = $"The largest cluster matches a known framework idle pattern ({frameworkPattern}); this is expected and not indicative of application-level contention.";
            else if (dominantSeverity == Severity.Warning)
                recommendation = "More than 50% of threads share the same call stack; this typically indicates a hotspot, resource contention, or coordinated wait. Investigate the signature frames for blocking points.";
            else
                recommendation = "The largest cluster is below 50% of threads; thread activity appears distributed.";

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("dominant-cluster"),
                Analyzer = AnalyzerName,
                Category = "Threading",
                Severity = dominantSeverity,
                Title = "Dominant thread cluster",
                Narrative = $"{dominantCount} of {aliveThreadCount} threads ({dominantPercent:F1}%) blocked in: {signature}",
                Recommendation = recommendation,
                Tags = isExpectedPattern
                    ? ["thread-cluster", "dominant-cluster", "framework-pattern"]
                    : ["thread-cluster", "dominant-cluster", "hotspot"],
                MetricValue = dominantPercent,
                MetricUnit = "% dominant-cluster",
                DerivedFrom = [diversity.Id, dominantCluster.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
                Assertion = BuildDominantAssertion(dominantSeverity, isExpectedPattern, dominantPercent),
                Support =
                [
                    new EvidenceStatement([dominantCluster.Id], $"{dominantCount:N0} threads share the same stack signature."),
                    new EvidenceStatement([diversity.Id], $"Supplies the alive thread count ({aliveThreadCount:N0}) the share is measured against."),
                ],
                Counter = BuildDominantCounter(dominantSeverity, isExpectedPattern, frameworkPattern, dominantPercent, dominantCluster),
                FalsifiedBy = BuildDominantFalsifiedBy(signature, dominantCount),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private const double LowDiversityPercent = 25;
    private const double DominantClusterPercent = 50;

    private static Assertion BuildDiversityAssertion(Severity severity, double diversityPercent) => new()
    {
        Predicate = severity == Severity.Warning ? AssertionPredicate.Dominates : AssertionPredicate.Normal,
        Measure = ClaimParts.Percent(diversityPercent),
        Reference = ClaimParts.Budget(ClaimParts.Percent(LowDiversityPercent), "low-diversity threshold"),
        Qualifiers = new AssertionQualifiers { Location = "thread stack-signature diversity" },
    };

    private static IReadOnlyList<FalsificationTest> BuildDiversityFalsifiedBy(Severity severity, int uniqueClusters, int aliveThreadCount)
    {
        var tests = new List<FalsificationTest>(2)
        {
            new()
            {
                Kind = FalsificationKind.Windbg,
                Command = "!eestack",
                Test = $"If the managed stacks of the {aliveThreadCount:N0} alive threads fall into a number of distinct shapes other than {uniqueClusters:N0}, the clustering is wrong.",
            },
        };

        if (severity == Severity.Warning)
            tests.Add(new()
            {
                Kind = FalsificationKind.Capture,
                Test = "Capture a second dump a few minutes later; if the shared signatures are different ones, the low diversity is a transient pile-up, not a standing pattern.",
            });

        return tests;
    }

    private static Assertion BuildDominantAssertion(Severity severity, bool isExpectedPattern, double dominantPercent) => new()
    {
        Predicate = severity == Severity.Warning ? AssertionPredicate.Dominates : AssertionPredicate.Normal,
        Measure = ClaimParts.Percent(dominantPercent),
        Reference = ClaimParts.Budget(ClaimParts.Percent(DominantClusterPercent), "dominant-cluster threshold"),
        Qualifiers = new AssertionQualifiers
        {
            Location = "alive threads sharing one stack signature",
            Mechanism = isExpectedPattern ? "a known framework idle pattern" : null,
        },
    };

    private static Counter BuildDominantCounter(
        Severity severity, bool isExpectedPattern, string? frameworkPattern, double dominantPercent, Observation dominantCluster)
    {
        if (isExpectedPattern)
            return dominantPercent >= DominantClusterPercent
                ? Counter.Of(new EvidenceStatement(
                    [dominantCluster.Id],
                    $"{dominantPercent:F0}% of alive threads share this stack, so even an idle pattern ({frameworkPattern}) leaves little capacity doing application work."))
                : Counter.None("The signature matches a known idle pattern and covers under half of the threads; nothing observed suggests application-level contention.");

        return severity == Severity.Warning
            ? Counter.None("Idle threads can look like a hotspot; no known framework idle pattern matched this signature.")
            : Counter.None("The largest cluster covers under half of the threads; no observation points at a shared bottleneck.");
    }

    private static IReadOnlyList<FalsificationTest> BuildDominantFalsifiedBy(string signature, int dominantCount) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!eestack",
            Test = $"If sampled threads from this cluster show a different top frame than \"{signature}\", the {dominantCount:N0}-thread grouping is wrong.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = "If the shared frame is a deliberate wait (a queue consumer parked on a signal), the cluster is by design, not a bottleneck.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

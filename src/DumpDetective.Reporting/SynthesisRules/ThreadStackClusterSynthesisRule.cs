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
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class ReferenceChainFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Reference Chain Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ReferenceChainDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ReferenceChainDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 2);

        if (r.AnalyzedSamples == 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: FindingSeverity.Info,
                Title: "No sample instances available for reference-chain tracing",
                Evidence: "Reference-chain analyzer could not obtain valid sample objects for configured top types.",
                Recommendation: "Review type statistics and dump integrity; re-run with broader type coverage if needed.",
                Tags: ["reference-chain", "roots", "retention"],
                MetricValue: 0,
                MetricUnit: "% retained-samples"));
            return findings;
        }

        FindingSeverity severity = r.RetainedPercent >= 70 ? FindingSeverity.Warning : FindingSeverity.Info;
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Retention",
            Severity: severity,
            Title: "Reference-chain retention coverage",
            Evidence: $"{r.RetainedSamples:N0}/{r.AnalyzedSamples:N0} sampled top types had at least one GC-root path ({r.RetainedPercent:F1}%).",
            Recommendation: "Focus on root paths for retained top types to identify ownership leaks.",
            Tags: ["reference-chain", "gc-roots", "retention"],
            MetricValue: r.RetainedPercent,
            MetricUnit: "% retained-samples"));

        // Traversal-limit finding derived from the typed result property.
        if (r.TraversalLimitedSamples > 0)
        {
            long limitedSamples = r.TraversalLimitedSamples;
            double limitedPct = r.AnalyzedSamples == 0 ? 0 : limitedSamples * 100.0 / r.AnalyzedSamples;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: limitedPct >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Reference-chain traversal limit reached",
                Evidence: $"{limitedSamples:N0}/{r.AnalyzedSamples:N0} sampled type(s) hit traversal limits before a conclusive root-path result ({limitedPct:F1}%).",
                Recommendation: "Increase sampling depth/path budget for inconclusive types and validate with targeted object tracing.",
                Tags: ["reference-chain", "traversal-limit", "retention"],
                MetricValue: limitedPct,
                MetricUnit: "% traversal-limited-samples"));
        }

        return findings;
    }
}

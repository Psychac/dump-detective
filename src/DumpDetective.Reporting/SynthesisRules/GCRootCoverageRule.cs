using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States where the root-owned subgraph walk stopped at its node or depth budget. Replaces the <c>Missing</c> claim
/// <see cref="GCRootSynthesisRule"/> used to emit: the cap describes the analysis, not the dump
/// (docs/ReportStructure/m3-coverage-plan.md D2). The analyzer does not count root-owned subgraphs, so the total is unknown.
/// </summary>
internal sealed class GCRootCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "GC Root Analysis";

    public string RuleId => "gc-root.coverage";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["root.population"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        foreach (Observation population in matched.Matched)
        {
            long capped = (long)population.Measures["subgraph-walk-capped.count"].Value;
            if (population.Measures["subgraph-walk-capped"].Value < 1.0 || capped <= 0)
                continue;

            return
            [
                new CoverageRecord(
                    new Capability(CapabilityVocabulary.HeapReferences),
                    population.Provenance.Artifact,
                    CoverageStatus.Partial,
                    [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
                    new CoverageBoundary(capped, null, "root-owned subgraphs", "were truncated at the node or depth budget")),
            ];
        }

        return [];
    }
}

using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States how many incoming-reference counts are approximate because the tracking limit was reached. Replaces the
/// <c>Missing</c> claim <see cref="DominatorSynthesisRule"/> used to emit (docs/ReportStructure/m3-coverage-plan.md D2).
/// The analyzer does not report how many addresses it tracked exactly, so the total is unknown.
/// </summary>
internal sealed class DominatorCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "Dominator Analysis";

    public string RuleId => "dominator.coverage";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["type.retention-population"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        foreach (Observation population in matched.Matched)
        {
            long approximated = (long)population.Measures["approximated-reference-addresses.count"].Value;
            if (approximated <= 0)
                continue;

            return
            [
                new CoverageRecord(
                    new Capability(CapabilityVocabulary.HeapReverseReferences),
                    population.Provenance.Artifact,
                    CoverageStatus.Partial,
                    [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
                    new CoverageBoundary(approximated, null, "addresses", "have approximated incoming-reference counts past the tracking limit")),
            ];
        }

        return [];
    }
}

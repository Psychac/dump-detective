using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States how far incoming-reference counting got. Replaces the <c>Missing</c> claim <see cref="DominatorSynthesisRule"/> used to
/// emit for approximated counts, and carries the two other limits the report's "bounded-scan signals" table stated from the domain
/// result: counting skipped, and the object-scan cap reached (docs/ReportStructure/m3-coverage-plan.md D2). The analyzer does not
/// report how many addresses it tracked exactly, so the total is unknown.
/// </summary>
internal sealed class DominatorCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "Dominator Analysis";
    private const string CountingScan = "incoming-reference counting";

    public string RuleId => "dominator.coverage";
    public int Version => 2;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["type.retention-population"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        foreach (Observation population in matched.Matched)
        {
            CoverageBoundary? boundary = BoundaryOf(population);
            if (boundary is null)
                continue;

            return
            [
                new CoverageRecord(
                    new Capability(CapabilityVocabulary.HeapReverseReferences),
                    population.Provenance.Artifact,
                    CoverageStatus.Partial,
                    [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
                    boundary),
            ];
        }

        return [];
    }

    // One boundary per observation, most severe limit first: skipping counting makes the other two moot.
    private static CoverageBoundary? BoundaryOf(Observation population)
    {
        if (population.Measures["is-reference-counting-skipped"].Value > 0)
            return new CoverageBoundary(null, null, CountingScan, "was skipped because the disk-backed index would make full reverse counting too expensive");

        if (population.Measures["is-object-scan-capped"].Value > 0)
            return new CoverageBoundary(null, null, CountingScan, "hit the object-scan cap, so highly-referenced-object counts may be partial");

        long approximated = (long)population.Measures["approximated-reference-addresses.count"].Value;
        return approximated > 0
            ? new CoverageBoundary(approximated, null, "addresses", "have approximated incoming-reference counts past the tracking limit")
            : null;
    }
}

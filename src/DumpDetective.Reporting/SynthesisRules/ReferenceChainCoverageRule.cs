using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States how far the reference-chain root-path searches got. Replaces the two <c>Missing</c> claims
/// <see cref="ReferenceChainSynthesisRule"/> used to emit for "no sample available" and "traversal limit reached":
/// both describe the analysis, not the dump (docs/ReportStructure/m3-coverage-plan.md D2).
/// </summary>
internal sealed class ReferenceChainCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "Reference Chain Analysis";
    private const string SampleUnit = "sampled types";

    public string RuleId => "reference-chain.coverage";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["reference-chain.population"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        Observation? population = null;
        foreach (Observation observation in matched.Matched)
            population = observation;

        if (population is null)
            return [];

        long analyzed = (long)population.Measures["analyzed.count"].Value;
        long traversalLimited = (long)population.Measures["traversal-limited.count"].Value;

        if (analyzed == 0)
            return [PartialRecord(population, reached: 0, total: 0, limit: "no valid sample instance for the configured top types")];

        if (traversalLimited == 0)
            return [];

        return [PartialRecord(population, reached: analyzed - traversalLimited, total: analyzed, limit: "path or depth budget")];
    }

    private static CoverageRecord PartialRecord(Observation population, long reached, long total, string limit) => new(
        new Capability(CapabilityVocabulary.HeapRootPaths),
        population.Provenance.Artifact,
        CoverageStatus.Partial,
        [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
        new CoverageBoundary(reached, total, SampleUnit, limit));
}

using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States how many strings the deduplication analysis did not see. Replaces the <c>Missing</c> claim
/// <see cref="StringSynthesisRule"/> used to emit when under 5% of strings were sampled
/// (docs/ReportStructure/m3-coverage-plan.md D2). The unseen strings are those longer than the maximum duplicate-string length.
/// </summary>
internal sealed class StringCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "String Analysis";
    private const double LowSamplingCoverage = 0.05;

    public string RuleId => "string.coverage";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["type.string-composition"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        foreach (Observation composition in matched.Matched)
        {
            double coverage = composition.Measures["sampling-coverage.ratio"].Value;
            if (coverage <= 0 || coverage >= LowSamplingCoverage)
                continue;

            long total = (long)composition.Measures["total-string.count"].Value;
            long sampled = (long)composition.Measures["strings-sampled.count"].Value;

            return
            [
                new CoverageRecord(
                    new Capability(CapabilityVocabulary.HeapStrings),
                    composition.Provenance.Artifact,
                    CoverageStatus.Partial,
                    [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
                    new CoverageBoundary(total - sampled, total, "strings", "fell outside the maximum duplicate-string length")),
            ];
        }

        return [];
    }
}

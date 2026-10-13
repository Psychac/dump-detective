using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States that static roots are never enumerated, in place of the "No static-root retention candidates detected" claim
/// <see cref="StaticRootSynthesisRule"/> used to publish when it found none. The detector filters on root kinds ClrMD does not
/// report (<c>StaticVar</c>, <c>ThreadStaticVar</c>), so a zero count is not an absence: the check cannot find anything. The fix
/// exists on branch <c>fix/static-root-detection</c> (4d7e267b) and is held because it costs about 454 s on a 3.3 GB dump, so this rule
/// stays correct until that fix is merged; when it is, this rule must go with it
/// (docs/ReportStructure/m3-coverage-plan.md step 7).
/// </summary>
internal sealed class StaticRootCoverageRule : ICoverageRule
{
    private const string AnalyzerName = "Static Root Leak Detection";

    public string RuleId => "static-root.coverage";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["static-root.population"] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        foreach (Observation population in matched.Matched)
        {
            if ((long)population.Measures["root.count"].Value != 0)
                continue;

            return
            [
                new CoverageRecord(
                    new Capability(CapabilityVocabulary.HeapStatics),
                    population.Provenance.Artifact,
                    CoverageStatus.Partial,
                    [new CoverageImpact(AnalyzerName, CoverageImpactKind.Limited)],
                    new CoverageBoundary(null, null, "static roots", "are never enumerated: the detector filters on root kinds ClrMD does not report, so finding none says nothing")),
            ];
        }

        return [];
    }
}

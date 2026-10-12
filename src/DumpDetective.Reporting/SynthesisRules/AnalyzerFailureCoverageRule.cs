using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// States which analyzers failed. Replaces <c>AnalyzerFailuresInsightRule</c>, whose <c>Missing</c> claim fired only from three
/// failures and carried a hand-picked threshold: a failed analyzer is a fact about the run at any count
/// (docs/ReportStructure/m3-coverage-plan.md D2). Keyed by analyzer, not by capability: a failure says nothing about the
/// capabilities the analyzer declared, which other analyzers may still cover.
/// </summary>
internal sealed class AnalyzerFailureCoverageRule : ICoverageRule
{
    public string RuleId => "analyzer-failure.coverage";
    public int Version => 1;

    // No observation is read; an empty query keeps the engine from matching anything (it still calls every rule).
    public ObservationQuery Query { get; } = new() { ObservationTypes = [] };

    public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context)
    {
        var records = new List<CoverageRecord>(context.FailedAnalyzers.Count);
        foreach (string analyzer in context.FailedAnalyzers)
        {
            records.Add(new CoverageRecord(
                Capability: null,
                Artifact: null,
                CoverageStatus.Failed,
                [new CoverageImpact(analyzer, CoverageImpactKind.Failed)]));
        }

        return records;
    }
}

using DumpDetective.Core.Models;
using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Runs every registered <see cref="ISynthesisRule"/> over a run's collected observations and
/// substitutes their <see cref="FindingSynthesisAdapter"/>-translated output for whatever
/// <c>IFindingGenerator</c>-based <c>FindingGenerationPipeline</c> already produced for the same
/// analyzer. Deliberately overrides rather than only filling in nulls: it's what lets
/// <c>ArrayFindingGenerator</c> stay registered (unchanged, since <c>AnalyzerFeatureModule.FindingGeneratorType</c>
/// is a required field with no per-analyzer opt-out — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook, step 6 note)
/// while still proving the migrated rule's output is what actually reaches the real report.
/// </summary>
/// <remarks>
/// Groups synthesized findings by <see cref="Finding.Analyzer"/>, not by re-querying per run — a
/// single <see cref="SynthesisEngine.SynthesizeAsync"/> pass over the whole observation set, same
/// as a real report run would do once more than one analyzer has a rule.
/// </remarks>
internal sealed class SynthesisFindingEnricher
{
    private readonly SynthesisEngine _engine;

    public SynthesisFindingEnricher(ISynthesisRuleFactory ruleFactory)
    {
        _engine = new SynthesisEngine(ruleFactory.CreateRules());
    }

    public async ValueTask<IReadOnlyList<AnalyzerRunResult>> EnrichAsync(
        IReadOnlyList<AnalyzerRunResult> runs, IReadOnlyList<Observation> observations, CancellationToken cancellationToken)
    {
        // AnalyzerFailuresInsightRule needs no observations at all (only the run-count summary
        // below), so in principle this guard could skip it on a zero-observation pass — but real
        // dumps always have tens of thousands of observations, so this is a synthetic-test-only
        // edge case, not a production concern. Left as-is rather than widening it: widening let
        // SynthesisFindingEnricherTests' StubRule (which unconditionally returns a finding,
        // ignoring its match set) fire on a zero-observation pass too, which broke that test's own
        // "nothing to synthesize, return runs unchanged" contract for every rule, not just this one.
        if (observations.Count == 0)
            return runs;

        int failedCount = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Status == AnalyzerExecutionStatus.Failed)
                failedCount++;
        }
        var context = new SynthesisContext { AnalyzerRunSummary = (runs.Count, failedCount) };

        IReadOnlyList<Finding> findings = await _engine.SynthesizeAsync(observations, context, cancellationToken);
        if (findings.Count == 0)
            return runs;

        var byAnalyzer = new Dictionary<string, List<InsightFinding>>(StringComparer.Ordinal);
        foreach (Finding finding in findings)
        {
            if (!byAnalyzer.TryGetValue(finding.Analyzer, out List<InsightFinding>? bucket))
            {
                bucket = [];
                byAnalyzer[finding.Analyzer] = bucket;
            }

            bucket.Add(FindingSynthesisAdapter.ToInsightFinding(finding));
        }

        var updated = new List<AnalyzerRunResult>(runs.Count);
        foreach (AnalyzerRunResult run in runs)
        {
            if (!byAnalyzer.TryGetValue(run.AnalyzerName, out List<InsightFinding>? replacementFindings))
            {
                updated.Add(run);
                continue;
            }

            updated.Add(run with { Findings = replacementFindings, FindingCount = replacementFindings.Count });
        }

        return updated;
    }
}

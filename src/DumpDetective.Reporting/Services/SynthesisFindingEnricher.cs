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
        if (observations.Count == 0)
            return runs;

        IReadOnlyList<Finding> findings = await _engine.SynthesizeAsync(observations, new SynthesisContext(), cancellationToken);
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

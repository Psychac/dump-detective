using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Runs every registered <see cref="ISynthesisRule"/> over a run's collected observations and
/// attaches the <see cref="FindingSynthesisAdapter"/>-translated output to each analyzer's run —
/// synthesis rules are the only source of analyzer findings.
/// </summary>
/// <remarks>
/// Groups synthesized findings by <see cref="Finding.Analyzer"/>, not by re-querying per run — a
/// single <see cref="SynthesisEngine.SynthesizeAsync"/> pass over the whole observation set, same
/// as a real report run would do once more than one analyzer has a rule. Findings whose
/// <see cref="Finding.Analyzer"/> matches no run (the cross-analyzer rules, all named
/// <c>"InsightEngine"</c>) are returned separately as <see cref="SynthesisEnrichment.UnattachedFindings"/>
/// rather than dropped.
/// </remarks>
internal sealed class SynthesisFindingEnricher
{
    private static readonly IReadOnlyDictionary<string, FindingLineage> EmptyLineage = new Dictionary<string, FindingLineage>();

    private readonly SynthesisEngine _engine;

    public SynthesisFindingEnricher(ISynthesisRuleFactory ruleFactory)
    {
        _engine = new SynthesisEngine(ruleFactory.CreateRules());
    }

    public async ValueTask<SynthesisEnrichment> EnrichAsync(
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
            return new SynthesisEnrichment(runs, [], EmptyLineage);

        int failedCount = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Status == AnalyzerExecutionStatus.Failed)
                failedCount++;
        }
        var context = new SynthesisContext { AnalyzerRunSummary = (runs.Count, failedCount) };

        IReadOnlyList<Finding> findings = await _engine.SynthesizeAsync(observations, context, cancellationToken);
        if (findings.Count == 0)
            return new SynthesisEnrichment(runs, [], EmptyLineage);

        var lineageByFingerprint = new Dictionary<string, FindingLineage>(StringComparer.Ordinal);
        var byAnalyzer = new Dictionary<string, List<InsightFinding>>(StringComparer.Ordinal);
        foreach (Finding finding in findings)
        {
            if (!byAnalyzer.TryGetValue(finding.Analyzer, out List<InsightFinding>? bucket))
            {
                bucket = [];
                byAnalyzer[finding.Analyzer] = bucket;
            }

            InsightFinding insightFinding = FindingSynthesisAdapter.ToInsightFinding(finding);
            bucket.Add(insightFinding);

            var lineage = new FindingLineage(finding.DerivedFrom, finding.Confidence);
            string fingerprint = insightFinding.EffectiveFingerprint;
            lineageByFingerprint[fingerprint] = lineageByFingerprint.TryGetValue(fingerprint, out FindingLineage? existing)
                ? existing.MergeWith(lineage)
                : lineage;
        }

        var updated = new List<AnalyzerRunResult>(runs.Count);
        foreach (AnalyzerRunResult run in runs)
        {
            if (!byAnalyzer.Remove(run.AnalyzerName, out List<InsightFinding>? replacementFindings))
            {
                updated.Add(run);
                continue;
            }

            updated.Add(run with { Findings = replacementFindings, FindingCount = replacementFindings.Count });
        }

        var unattached = new List<InsightFinding>();
        foreach (List<InsightFinding> bucket in byAnalyzer.Values)
            unattached.AddRange(bucket);

        return new SynthesisEnrichment(updated, unattached, lineageByFingerprint);
    }
}

internal readonly record struct SynthesisEnrichment(
    IReadOnlyList<AnalyzerRunResult> Runs,
    IReadOnlyList<InsightFinding> UnattachedFindings,
    IReadOnlyDictionary<string, FindingLineage> LineageByFingerprint);

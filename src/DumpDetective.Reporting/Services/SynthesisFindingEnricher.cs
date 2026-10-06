using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Runs every registered <see cref="ISynthesisRule"/> over a run's collected observations and
/// attaches the resulting claims to each analyzer's run —
/// synthesis rules are the only source of analyzer findings.
/// </summary>
/// <remarks>
/// Groups synthesized findings by <see cref="Claim.Analyzer"/>, not by re-querying per run — a
/// single <see cref="SynthesisEngine.SynthesizeAsync"/> pass over the whole observation set, same
/// as a real report run would do once more than one analyzer has a rule. Findings whose
/// <see cref="Claim.Analyzer"/> matches no run (the cross-analyzer rules, all named
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

    private static Dictionary<ObservationId, string> IndexAnalyzers(IReadOnlyList<Observation> observations)
    {
        var analyzerByObservation = new Dictionary<ObservationId, string>(observations.Count);
        foreach (Observation observation in observations)
            analyzerByObservation[observation.Id] = observation.Provenance.AnalyzerKey;
        return analyzerByObservation;
    }

    private static EvidenceSources? ResolveSources(Claim claim, Dictionary<ObservationId, string> analyzerByObservation)
    {
        if (claim.Support.Count == 0 && claim.Counter is not { Statements.Count: > 0 })
            return null;

        var support = new List<IReadOnlyList<string>>(claim.Support.Count);
        foreach (EvidenceStatement statement in claim.Support)
            support.Add(AnalyzersOf(statement, analyzerByObservation));

        var counter = new List<IReadOnlyList<string>>(claim.Counter?.Statements.Count ?? 0);
        if (claim.Counter is not null)
        {
            foreach (EvidenceStatement statement in claim.Counter.Statements)
                counter.Add(AnalyzersOf(statement, analyzerByObservation));
        }

        return new EvidenceSources(support, counter);
    }

    private static List<string> AnalyzersOf(EvidenceStatement statement, Dictionary<ObservationId, string> analyzerByObservation)
    {
        var analyzers = new List<string>(1);
        foreach (ObservationId id in statement.Observations)
        {
            if (analyzerByObservation.TryGetValue(id, out string? analyzer) && !analyzers.Contains(analyzer))
                analyzers.Add(analyzer);
        }

        return analyzers;
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

        IReadOnlyList<Claim> findings = await _engine.SynthesizeAsync(observations, context, cancellationToken);
        if (findings.Count == 0)
            return new SynthesisEnrichment(runs, [], EmptyLineage);

        var lineageByFingerprint = new Dictionary<string, FindingLineage>(StringComparer.Ordinal);
        Dictionary<ObservationId, string>? analyzerByObservation = null;
        var byAnalyzer = new Dictionary<string, List<Claim>>(StringComparer.Ordinal);
        foreach (Claim finding in findings)
        {
            if (!byAnalyzer.TryGetValue(finding.Analyzer, out List<Claim>? bucket))
            {
                bucket = [];
                byAnalyzer[finding.Analyzer] = bucket;
            }

            bucket.Add(finding);

            analyzerByObservation ??= IndexAnalyzers(observations);
            FindingLineage lineage = FindingLineage.From(finding) with { EvidenceSources = ResolveSources(finding, analyzerByObservation) };
            string fingerprint = finding.Fingerprint;
            lineageByFingerprint[fingerprint] = lineageByFingerprint.TryGetValue(fingerprint, out FindingLineage? existing)
                ? existing.MergeWith(lineage)
                : lineage;
        }

        var updated = new List<AnalyzerRunResult>(runs.Count);
        foreach (AnalyzerRunResult run in runs)
        {
            if (!byAnalyzer.Remove(run.AnalyzerName, out List<Claim>? replacementFindings))
            {
                updated.Add(run);
                continue;
            }

            updated.Add(run with { Findings = replacementFindings, FindingCount = replacementFindings.Count });
        }

        var unattached = new List<Claim>();
        foreach (List<Claim> bucket in byAnalyzer.Values)
            unattached.AddRange(bucket);

        return new SynthesisEnrichment(updated, unattached, lineageByFingerprint);
    }
}

internal readonly record struct SynthesisEnrichment(
    IReadOnlyList<AnalyzerRunResult> Runs,
    IReadOnlyList<Claim> UnattachedFindings,
    IReadOnlyDictionary<string, FindingLineage> LineageByFingerprint);

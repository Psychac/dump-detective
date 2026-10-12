using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Artifacts;
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
    private static readonly IReadOnlyDictionary<string, EvidenceSources> EmptyEvidenceSources = new Dictionary<string, EvidenceSources>();

    private readonly SynthesisEngine _engine;

    public SynthesisFindingEnricher(ISynthesisRuleFactory ruleFactory)
    {
        _engine = new SynthesisEngine(ruleFactory.CreateRules(), coverageRules: ruleFactory.CreateCoverageRules());
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
            return new SynthesisEnrichment(runs, [], EmptyEvidenceSources, []);

        var failedAnalyzers = new List<string>();
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Status == AnalyzerExecutionStatus.Failed)
                failedAnalyzers.Add(run.AnalyzerName);
        }
        var context = new SynthesisContext { FailedAnalyzers = failedAnalyzers };

        IReadOnlyList<Claim> findings = await _engine.SynthesizeAsync(observations, context, cancellationToken);
        IReadOnlyList<CoverageRecord> coverage = _engine.EvaluateCoverage(observations, context);
        if (coverage.Count > 0)
            (findings, coverage) = LinkClaimsToCoverage(findings, coverage);

        if (findings.Count == 0)
            return new SynthesisEnrichment(runs, [], EmptyEvidenceSources, coverage);

        var evidenceSourcesByFingerprint = new Dictionary<string, EvidenceSources>(StringComparer.Ordinal);
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
            EvidenceSources? sources = ResolveSources(finding, analyzerByObservation);
            if (sources is not null)
                evidenceSourcesByFingerprint[finding.Fingerprint] = sources;
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

        return new SynthesisEnrichment(updated, unattached, evidenceSourcesByFingerprint, coverage);
    }

    // A claim is linked to a limited analyzer's record by analyzer name; the claim's confidence gains a limiting factor
    // naming the boundary but its scores do not move, because the rule already judged how well its evidence supports the claim.
    private static (IReadOnlyList<Claim> Claims, IReadOnlyList<CoverageRecord> Coverage) LinkClaimsToCoverage(
        IReadOnlyList<Claim> claims, IReadOnlyList<CoverageRecord> coverage)
    {
        var fingerprintsByRecord = new List<string>[coverage.Count];
        var linkedClaims = new List<Claim>(claims.Count);

        foreach (Claim claim in claims)
        {
            Claim linked = claim;
            for (int i = 0; i < coverage.Count; i++)
            {
                if (!IsLimiting(coverage[i], claim.Analyzer))
                    continue;

                (fingerprintsByRecord[i] ??= []).Add(claim.Fingerprint);
                linked = linked with { Confidence = linked.Confidence with { LimitingFactors = [.. linked.Confidence.LimitingFactors, LimitingFactorText(coverage[i])] } };
            }

            linkedClaims.Add(linked);
        }

        var linkedCoverage = new List<CoverageRecord>(coverage.Count);
        for (int i = 0; i < coverage.Count; i++)
            linkedCoverage.Add(fingerprintsByRecord[i] is { } fingerprints ? coverage[i] with { Claims = fingerprints } : coverage[i]);

        return (linkedClaims, linkedCoverage);
    }

    private static bool IsLimiting(CoverageRecord record, string analyzer)
    {
        foreach (CoverageImpact impact in record.Impacts)
        {
            if (impact.Kind == CoverageImpactKind.Limited && string.Equals(impact.Analyzer, analyzer, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string LimitingFactorText(CoverageRecord record) => record.Boundary is null
        ? $"{record.Capability} coverage is partial."
        : $"{record.Capability} coverage is partial: {CoverageProse.BoundaryText(record)}.";
}

internal readonly record struct SynthesisEnrichment(
    IReadOnlyList<AnalyzerRunResult> Runs,
    IReadOnlyList<Claim> UnattachedFindings,
    IReadOnlyDictionary<string, EvidenceSources> EvidenceSourcesByFingerprint,
    IReadOnlyList<CoverageRecord> Coverage);

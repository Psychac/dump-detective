using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DumpDetective.Reporting.Synthesis;

/// <summary>
/// Runs every registered <see cref="ISynthesisRule"/> over a collected observation set and returns
/// the combined findings. See docs/refactor/modularity/phase-5-observations-synthesis.md's
/// "Before step 2" prerequisites for why this shape — no query DSL, no forced per-entity grouping,
/// no rule-level parallelism — was chosen over the first-draft design.
/// </summary>
/// <remarks>
/// Indexes observations by <see cref="Observation.ObservationType"/> once per run — a plain
/// dictionary, not a query engine — because at measured scale (81,316 observations, phase-5's own
/// real-dump measurement) a full per-rule scan is already sub-millisecond; the index exists to
/// avoid re-scanning the same list once per rule, not because scanning it once per rule would be
/// too slow.
/// </remarks>
internal sealed class SynthesisEngine
{
    private readonly IReadOnlyList<ISynthesisRule> _rules;
    private readonly ILogger<SynthesisEngine> _logger;

    public SynthesisEngine(IReadOnlyList<ISynthesisRule> rules, ILogger<SynthesisEngine>? logger = null)
    {
        _rules = rules;
        _logger = logger ?? NullLogger<SynthesisEngine>.Instance;
    }

    public async ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        IReadOnlyList<Observation> observations,
        SynthesisContext context,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<Observation>> byType = new(StringComparer.Ordinal);
        Dictionary<ObservationId, Observation> byId = new(observations.Count);
        foreach (Observation observation in observations)
        {
            byId[observation.Id] = observation;

            if (!byType.TryGetValue(observation.ObservationType, out List<Observation>? bucket))
            {
                bucket = [];
                byType[observation.ObservationType] = bucket;
            }

            bucket.Add(observation);
        }

        var findings = new List<Finding>();

        // Sequential, not parallel: rule cost is trivial at measured scale, and sequential
        // execution is the safer default while the byte-identical golden-file bar is in force —
        // see the "Before step 2" prerequisites doc entry.
        foreach (ISynthesisRule rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Observation> matched = CollectMatches(rule.Query, byType);

            // No zero-match skip: whether "nothing matched" means "nothing to report" or "report an
            // explicit no-issue finding" is the rule's call, not the engine's — see
            // ReportSystemVision.md § 7.2 on negative results being first-class, deliberately left
            // for a rule to decide rather than short-circuited here.
            try
            {
                IReadOnlyList<Finding> ruleFindings = await rule.SynthesizeAsync(
                    new ObservationMatchSet(matched), context, cancellationToken);
                foreach (Finding finding in ruleFindings)
                    findings.Add(WithLineageConfidence(finding, byId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Synthesis rule '{RuleId}' threw and was skipped", rule.RuleId);
            }
        }

        findings.Sort((a, b) => string.CompareOrdinal(a.Fingerprint, b.Fingerprint));
        return findings;
    }

    // A rule judges how well its evidence supports its claim; the engine adds what the observations
    // underneath can vouch for, so no rule has to re-derive measurement caps from its own lineage.
    private static Finding WithLineageConfidence(Finding finding, Dictionary<ObservationId, Observation> byId)
    {
        var lineage = new List<Observation>(finding.DerivedFrom.Count);
        foreach (ObservationId id in finding.DerivedFrom)
        {
            if (byId.TryGetValue(id, out Observation? observation))
                lineage.Add(observation);
        }

        return finding with { Confidence = ConfidenceCalculator.Apply(finding.Confidence, lineage) };
    }

    private static List<Observation> CollectMatches(
        ObservationQuery query, Dictionary<string, List<Observation>> byType)
    {
        var candidates = new List<Observation>();
        foreach (string observationType in query.ObservationTypes)
        {
            if (byType.TryGetValue(observationType, out List<Observation>? bucket))
            {
                candidates.AddRange(bucket);
            }
        }

        if (query.AdditionalPredicate is null)
        {
            return candidates;
        }

        var filtered = new List<Observation>(candidates.Count);
        foreach (Observation candidate in candidates)
        {
            if (query.AdditionalPredicate(candidate))
            {
                filtered.Add(candidate);
            }
        }

        return filtered;
    }
}

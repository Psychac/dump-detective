using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectActiveCrash</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>CrashDomainResult.ActiveExceptionTypeCounts</c> (a pre-aggregated per-type dictionary) was
/// never observed directly — instead, <c>CrashAnalyzer</c> emits one <c>runtime.active-exception</c>
/// observation per active exception *instance*, each with the exception's <c>TypeRef</c> as its
/// subject. This rule re-derives the same dominant-type/count pair by grouping those observations,
/// rather than adding a redundant per-type observation the analyzer would have to keep in sync with
/// the per-instance one.
/// </remarks>
internal sealed class ActiveCrashInsightRule : ISynthesisRule
{
    public string RuleId => "insight.active-crash";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-population", "runtime.active-exception"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var activeExceptions = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "runtime.crash-population")
                population = observation;
            else if (observation.ObservationType == "runtime.active-exception")
                activeExceptions.Add(observation);
        }

        long activeCount = (long)(population?.Measures["active.count"].Value ?? 0);
        if (population is null || activeCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var countByType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Observation instance in activeExceptions)
        {
            string typeName = ((TypeRef)instance.Subjects[0]).CanonicalName;
            countByType[typeName] = countByType.TryGetValue(typeName, out int existing) ? existing + 1 : 1;
        }

        string? dominantType = null;
        int dominantCount = 0;
        foreach (KeyValuePair<string, int> kv in countByType)
        {
            if (kv.Value > dominantCount)
            {
                dominantCount = kv.Value;
                dominantType = kv.Key;
            }
        }

        string narrative = dominantType is not null
            ? $"Dominant type: {dominantType} ({dominantCount} active). Total active: {activeCount}."
            : $"Total active exceptions: {activeCount}.";

        var derivedFrom = new List<ObservationId> { population.Id };
        foreach (Observation instance in activeExceptions)
            derivedFrom.Add(instance.Id);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::active-crash",
                Analyzer = "InsightEngine",
                Category = "Crash",
                Severity = Severity.Critical,
                Title = "Active exceptions found — dump may represent a crash or unhandled exception state",
                Narrative = narrative,
                Recommendation = "Examine the Crash analyzer findings for full exception messages and stack traces. " +
                                 "Correlate with the Thread analyzer to identify the faulting thread.",
                Tags = ["crash", "exception", "active-exception"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}

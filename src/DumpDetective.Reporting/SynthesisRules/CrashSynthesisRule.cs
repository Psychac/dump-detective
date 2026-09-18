using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>CrashFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Trusts <c>runtime.crash-thread-candidate</c> emission order for the "primary exception type"
/// text — <c>CrashAnalyzer</c> sorts <c>CrashThreadCandidates</c> descending by
/// <c>ActiveExceptionCount</c> before either the domain result or the observation loop is built,
/// the same already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies on. Falls
/// back to counting <c>runtime.active-exception</c> Subject types when no candidate observations
/// exist, matching the old generator's own fallback to <c>ActiveExceptionTypeCounts</c>.
/// <para>
/// The old generator's active-exceptions finding also computes a weighted-average
/// <c>Finding.ConfidenceScore</c> (from each candidate's <c>OriginalStackTraceConfidence</c> tier)
/// plus a <c>Caveats</c> list summarizing tier distribution — left unreproduced, same choice every
/// prior rule in this playbook made for evidence-based confidence overrides, since neither is part
/// of this migration's parity surface (Title/Narrative/Recommendation/Severity/Category/Tags/
/// MetricValue/MetricUnit).
/// </para>
/// </remarks>
internal sealed class CrashSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Crash Analysis";

    public string RuleId => "crash.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-population", "runtime.active-exception", "runtime.crash-thread-candidate"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        Observation? topCandidate = null;
        var activeExceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeExceptionIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.crash-population":
                    population = observation;
                    break;
                case "runtime.crash-thread-candidate":
                    topCandidate ??= observation;
                    break;
                case "runtime.active-exception":
                    activeExceptionIds.Add(observation.Id);
                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    activeExceptionTypeCounts.TryGetValue(typeName, out int count);
                    activeExceptionTypeCounts[typeName] = count + 1;
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long totalExceptions = (long)population.Measures["total.count"].Value;

        if (totalExceptions == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Finding>>([new Finding
            {
                Fingerprint = Fingerprint("no-exceptions"),
                Analyzer = AnalyzerName,
                Category = "Stability",
                Severity = Severity.Info,
                Title = "No exception objects detected",
                Narrative = "Crash analysis found no exception objects in the heap snapshot.",
                Recommendation = "Validate dump type and capture settings if a crash was expected.",
                Tags = ["crash", "exception", "stability"],
                MetricValue = 0,
                MetricUnit = "active-exceptions",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        long activeExceptions = (long)population.Measures["active.count"].Value;
        long uniqueTypes = (long)population.Measures["unique-types.count"].Value;

        if (activeExceptions > 0)
        {
            string topType;
            var derivedFrom = new List<ObservationId> { population.Id };

            if (topCandidate is not null)
            {
                topType = ((TypeRef)topCandidate.Subjects[0]).CanonicalName;
                derivedFrom.Add(topCandidate.Id);
            }
            else if (activeExceptionTypeCounts.Count > 0)
            {
                string best = "";
                int bestCount = -1;
                foreach (KeyValuePair<string, int> kv in activeExceptionTypeCounts)
                {
                    if (kv.Value > bestCount)
                    {
                        bestCount = kv.Value;
                        best = kv.Key;
                    }
                }
                topType = best;
                derivedFrom.AddRange(activeExceptionIds);
            }
            else
            {
                topType = "Unknown";
            }

            return ValueTask.FromResult<IReadOnlyList<Finding>>([new Finding
            {
                Fingerprint = Fingerprint("active-exceptions"),
                Analyzer = AnalyzerName,
                Category = "Stability",
                Severity = Severity.Critical,
                Title = $"Active exceptions detected ({activeExceptions:N0} on thread stacks)",
                Narrative = $"{activeExceptions:N0} active exception(s) found on thread stacks. Primary type: {topType}. " +
                            $"Total exceptions: {totalExceptions:N0}; unique types: {uniqueTypes:N0}.",
                Recommendation = "Investigate the crash thread candidates; correlate with the thread section for full context.",
                Tags = ["crash", "exceptions", "threads"],
                MetricValue = activeExceptions,
                MetricUnit = "active-exceptions",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>([new Finding
        {
            Fingerprint = Fingerprint("exception-pressure"),
            Analyzer = AnalyzerName,
            Category = "Stability",
            Severity = Severity.Warning,
            Title = "Exception pressure in crash dump",
            Narrative = $"Total exceptions: {totalExceptions:N0}; active thread exceptions: 0; unique types: {uniqueTypes:N0}.",
            Recommendation = "Review top exception families for recurring fault paths.",
            Tags = ["crash", "exceptions", "threads"],
            MetricValue = 0,
            MetricUnit = "active-exceptions",
            DerivedFrom = [population.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        }]);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

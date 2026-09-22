using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectLeakSuspicion</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class LeakSuspicionInsightRule : ISynthesisRule
{
    private const ulong DuplicateWasteThresholdBytes = 10 * 1024 * 1024; // 10 MB

    public string RuleId => "insight.leak-suspicion";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.retention-population", "type.string-composition"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? retention = null, strings = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.retention-population": retention = observation; break;
                case "type.string-composition": strings = observation; break;
            }
        }

        var findings = new List<Finding>();

        if (retention is not null)
        {
            long highlyReferenced = (long)retention.Measures["highly-referenced.count"].Value;
            if (highlyReferenced > 0)
            {
                findings.Add(new Finding
                {
                    Fingerprint = "insightengine::leak-suspicion-highly-referenced",
                    Analyzer = "InsightEngine",
                    Category = "Memory",
                    Severity = Severity.Warning,
                    Title = "Objects with unusually high incoming reference counts detected",
                    Narrative = $"{highlyReferenced:N0} objects have an abnormally high number of " +
                                "incoming references, indicating potential retention through event handlers, " +
                                "static collections, or observer patterns.",
                    Recommendation = "Review the Retention analyzer findings for specific types. " +
                                     "Common causes: static event subscriptions, global caches, and long-lived delegates " +
                                     "capturing closures over short-lived objects.",
                    Tags = ["memory-leak", "retention", "references"],
                    DerivedFrom = [retention.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        if (strings is not null)
        {
            ulong duplicateWastedBytes = (ulong)strings.Measures["duplicate-wasted.bytes"].Value;
            if (duplicateWastedBytes > DuplicateWasteThresholdBytes)
            {
                long duplicatePatternCount = (long)strings.Measures["duplicate-pattern.count"].Value;
                long totalStrings = (long)strings.Measures["total-string.count"].Value;
                ulong totalStringMemoryBytes = (ulong)strings.Measures["total-string-memory.bytes"].Value;

                findings.Add(new Finding
                {
                    Fingerprint = "insightengine::leak-suspicion-duplicate-strings",
                    Analyzer = "InsightEngine",
                    Category = "Memory",
                    Severity = Severity.Info,
                    Title = "Significant memory wasted by duplicate string instances",
                    Narrative = $"{InsightByteFormat.FormatBytes(duplicateWastedBytes)} wasted across " +
                                $"{duplicatePatternCount:N0} duplicate string pattern(s). " +
                                $"Total strings: {totalStrings:N0} ({InsightByteFormat.FormatBytes(totalStringMemoryBytes)}).",
                    Recommendation = "Use string.Intern or a shared lookup table for repeated strings. " +
                                     "Consider replacing string keys with enum or int IDs in hot dictionaries.",
                    Tags = ["memory-leak", "strings", "interning"],
                    DerivedFrom = [strings.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }
}

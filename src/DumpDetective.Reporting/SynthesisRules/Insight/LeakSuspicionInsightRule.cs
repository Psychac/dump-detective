using DumpDetective.Reporting.SynthesisRules;
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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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

        var findings = new List<Claim>();

        if (retention is not null)
        {
            long highlyReferenced = (long)retention.Measures["highly-referenced.count"].Value;
            if (highlyReferenced > 0)
            {
                findings.Add(new Claim
                {
                    Fingerprint = "insightengine::leak-suspicion-highly-referenced",
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exhibits,
                        Measure = ClaimParts.Count(highlyReferenced),
                        Qualifiers = new AssertionQualifiers { Location = "objects with abnormally high incoming reference counts" },
                    },
                    Support = [new EvidenceStatement([retention.Id], $"{highlyReferenced:N0} objects have abnormally high incoming reference counts.")],
                    Counter = Counter.None("A shared lookup object is referenced widely by design; a high incoming count does not show it is retained longer than intended."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!gcroot <object address>",
                            Test = "On a sampled highly referenced object: if its referrers are short-lived, the high count is transient, not a retention hub.",
                        },
                    ],
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

                findings.Add(new Claim
                {
                    Fingerprint = "insightengine::leak-suspicion-duplicate-strings",
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Duplicates,
                        Measure = ClaimParts.Bytes(duplicateWastedBytes),
                        Reference = ClaimParts.Budget(ClaimParts.Bytes(DuplicateWasteThresholdBytes), "duplicate-waste threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "duplicate string instances" },
                    },
                    Support = [new EvidenceStatement([strings.Id], $"{InsightByteFormat.FormatBytes(duplicateWastedBytes)} is wasted across {duplicatePatternCount:N0} duplicate patterns among {totalStrings:N0} strings.")],
                    Counter = Counter.None("Duplicates inside short-lived structures are reclaimed with them; lifetime is not observed."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!dumpheap -strings",
                            Test = $"If the most repeated strings in !dumpheap -strings add up to far less than {InsightByteFormat.FormatBytes(duplicateWastedBytes)}, the waste is overstated.",
                        },
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.CodeInspection,
                            Test = "If the repeated strings are already interned or pooled at the source, the duplicates are transient copies, not retained waste.",
                        },
                    ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }
}

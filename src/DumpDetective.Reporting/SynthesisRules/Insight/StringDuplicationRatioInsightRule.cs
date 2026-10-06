using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectStringDuplicationRatio</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Complements <c>LeakSuspicionInsightRule</c>'s duplicate-string check the same way the original
/// complemented <c>DetectLeakSuspicion</c> — this fires on ratio regardless of absolute waste
/// bytes, but only when the absolute waste is under 10 MB (otherwise that other rule already
/// covers it). Two independent rules, same avoid-double-firing guard the original had inline.
/// </remarks>
internal sealed class StringDuplicationRatioInsightRule : ISynthesisRule
{
    private const double WarningRatio = 0.50;
    private const ulong DuplicateWasteThresholdBytes = 10 * 1024 * 1024; // 10 MB

    public string RuleId => "insight.string-duplication-ratio";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["type.string-composition"] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Observation strings = matched.Matched[0];
        long totalStrings = (long)strings.Measures["total-string.count"].Value;
        if (totalStrings == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double duplicationRatio = strings.Measures["duplication.ratio"].Value;
        if (duplicationRatio < WarningRatio)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong duplicateWastedBytes = (ulong)strings.Measures["duplicate-wasted.bytes"].Value;
        if (duplicateWastedBytes >= DuplicateWasteThresholdBytes)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long uniquePatterns = (long)strings.Measures["unique-pattern.count"].Value;
        double samplingCoverage = strings.Measures["sampling-coverage.ratio"].Value;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::string-duplication-ratio",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Duplicates,
                    Measure = ClaimParts.Percent(duplicationRatio * 100.0),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(WarningRatio * 100.0), "duplication-ratio threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "string instances", Scope = $"{samplingCoverage:P1} sampling coverage" },
                },
                Support = [new EvidenceStatement([strings.Id], $"{duplicationRatio:P0} of {totalStrings:N0} strings are duplicates, wasting {InsightByteFormat.FormatBytes(duplicateWastedBytes)}.")],
                Counter = Counter.Of(new EvidenceStatement(
                    [strings.Id],
                    $"The duplicated strings waste only {InsightByteFormat.FormatBytes(duplicateWastedBytes)}, under the {InsightByteFormat.FormatBytes(DuplicateWasteThresholdBytes)} that would make it costly, so the ratio overstates the cost.")),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -strings",
                        Test = "If the most repeated strings in !dumpheap -strings are a small number of short tokens, the ratio is high but the cost is small.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Info,
                Title = "High string duplication ratio detected",
                Narrative = $"{duplicationRatio:P0} of string instances are duplicates " +
                            $"({totalStrings - uniquePatterns:N0} duplicate out of {totalStrings:N0} total). " +
                            $"Wasted: {InsightByteFormat.FormatBytes(duplicateWastedBytes)}. " +
                            $"(Based on {samplingCoverage:P1} sampling coverage; interpret with caution at low coverage.)",
                Recommendation = "Consider string.Intern for frequently duplicated strings, " +
                                 "or use a string→int dictionary for repeated tokens.",
                Tags = ["strings", "duplication", "memory"],
                MetricValue = duplicationRatio,
                MetricUnit = "ratio",
                DerivedFrom = [strings.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}

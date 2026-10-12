using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>StringFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Every scalar here comes from the whole-run <c>type.string-composition</c> observation, not from
/// counting/summing <c>heap.long-string</c> — the latter is emitted uncapped during the heap scan,
/// while <c>very-long-string.count</c>/<c>.bytes</c> on <c>type.string-composition</c> are the exact
/// totals after <c>StringAnalyzer</c>'s own post-scan truncation to <c>maxVeryLongStringsToKeep</c>,
/// which is what the old generator's <c>VeryLongStrings.Count</c> actually reads. Counting matched
/// <c>heap.long-string</c> observations instead would silently overcount past that cap — the same
/// capped-vs-exact trap <c>ObjectShapeSynthesisRule</c>'s migration caught.
/// </remarks>
internal sealed class StringSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "String Analysis";

    public string RuleId => "string.finding-parity";
    public int Version => 2;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.string-composition"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = matched.Matched.FirstOrDefault(o => o.ObservationType == "type.string-composition");
        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double duplicationRatio = composition.Measures["duplication.ratio"].Value;
        int duplicatePatternCount = (int)composition.Measures["duplicate-pattern.count"].Value;
        ulong duplicateWastedBytes = (ulong)composition.Measures["duplicate-wasted.bytes"].Value;
        ulong lohStringBytes = (ulong)composition.Measures["loh-string.bytes"].Value;
        int veryLongStringCount = (int)composition.Measures["very-long-string.count"].Value;
        ulong veryLongStringBytes = (ulong)composition.Measures["very-long-string.bytes"].Value;
        double pctOfManagedHeap = composition.Measures["pct-of-managed-heap.percent"].Value;
        ulong totalStringMemoryBytes = (ulong)composition.Measures["total-string-memory.bytes"].Value;
        double samplingCoverage = composition.Measures["sampling-coverage.ratio"].Value;
        int stringsSampled = (int)composition.Measures["strings-sampled.count"].Value;

        var findings = new List<Claim>(capacity: 4);

        if (duplicationRatio > 0.5 && duplicatePatternCount > 0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("extreme-duplication"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Duplicates,
                    Measure = ClaimParts.Percent(duplicationRatio * 100.0),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(ExtremeDuplicationRatio * 100.0), "extreme-duplication threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "string objects" },
                },
                Support = [new EvidenceStatement([composition.Id], $"{duplicatePatternCount:N0} duplicated patterns waste about {FormatHelper.FormatBytes(duplicateWastedBytes)}.")],
                Counter = DuplicationCounter(composition, samplingCoverage),
                FalsifiedBy = DuplicationFalsifiedBy(duplicatePatternCount, duplicateWastedBytes),
                Analyzer = AnalyzerName,
                Category = "Optimization",
                Severity = Severity.Critical,
                Title = "Extreme string duplication detected",
                Narrative = $"{duplicationRatio:P0} of all string objects are duplicates — {duplicatePatternCount:N0} patterns wasting ~{FormatHelper.FormatBytes(duplicateWastedBytes)}.",
                Recommendation = "Use string interning (string.Intern), pooling, or de-duplication at ingestion points to eliminate redundant allocations.",
                Tags = ["string", "memory", "allocation"],
                MetricValue = duplicateWastedBytes,
                MetricUnit = "wasted-bytes",
                DerivedFrom = [composition.Id],
                Confidence = SampledDuplicationConfidence(samplingCoverage),
            });
        }
        else if (duplicatePatternCount > 0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("high-duplicate-pressure"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Duplicates,
                    Measure = ClaimParts.Count(duplicatePatternCount),
                    Qualifiers = new AssertionQualifiers { Location = "string objects" },
                },
                Support = [new EvidenceStatement([composition.Id], $"{duplicatePatternCount:N0} duplicated patterns waste about {FormatHelper.FormatBytes(duplicateWastedBytes)} ({duplicationRatio:P0} of strings are duplicates).")],
                Counter = DuplicationCounter(composition, samplingCoverage),
                FalsifiedBy = DuplicationFalsifiedBy(duplicatePatternCount, duplicateWastedBytes),
                Analyzer = AnalyzerName,
                Category = "Optimization",
                Severity = Severity.Warning,
                Title = "High duplicate string pressure detected",
                Narrative = $"{duplicatePatternCount:N0} duplicate string patterns with ~{FormatHelper.FormatBytes(duplicateWastedBytes)} estimated waste ({duplicationRatio:P0} duplication ratio).",
                Recommendation = "Consider string interning or pooling for frequently repeated payloads.",
                Tags = ["string", "memory", "allocation"],
                MetricValue = duplicateWastedBytes,
                MetricUnit = "wasted-bytes",
                DerivedFrom = [composition.Id],
                Confidence = SampledDuplicationConfidence(samplingCoverage),
            });
        }

        if (lohStringBytes > 0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("loh-strings"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Bytes(lohStringBytes),
                    Qualifiers = new AssertionQualifiers { Location = "the Large Object Heap", Mechanism = "individual strings over 85 KB" },
                },
                Support = [new EvidenceStatement([composition.Id], $"{FormatHelper.FormatBytes(lohStringBytes)} of string data sits on the Large Object Heap.")],
                Counter = Counter.None("A string on the LOH is not necessarily retained; short-lived ones are collected with Gen2 and cost fragmentation, not memory growth."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -type System.String -min 85000",
                        Test = $"If the strings of 85,000 bytes or more total much less than {FormatHelper.FormatBytes(lohStringBytes)}, the LOH string figure is overstated.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Large strings allocated on LOH",
                Narrative = $"{FormatHelper.FormatBytes(lohStringBytes)} of string data resides on the Large Object Heap (individual strings > 85 KB).",
                Recommendation = "Avoid creating very long strings; split or stream large text data to prevent LOH fragmentation.",
                Tags = ["string", "loh", "memory"],
                MetricValue = lohStringBytes,
                MetricUnit = "bytes",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (veryLongStringCount > 0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("very-long-strings"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(veryLongStringCount),
                    Qualifiers = new AssertionQualifiers { Location = "strings over 85 KB", Mechanism = "blocks GC compaction and fragments the LOH" },
                },
                Support = [new EvidenceStatement([composition.Id], $"{veryLongStringCount:N0} strings over 85 KB total {FormatHelper.FormatBytes(veryLongStringBytes)}.")],
                Counter = Counter.None("Long strings only cost fragmentation if they churn; nothing observed shows whether these are allocated repeatedly."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -type System.String -min 85000 -stat",
                        Test = $"If !dumpheap -stat finds a number of strings other than {veryLongStringCount:N0} at or above 85,000 bytes, the count is wrong.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Very long strings detected",
                Narrative = $"{veryLongStringCount:N0} very long string(s) totaling {FormatHelper.FormatBytes(veryLongStringBytes)} (individual threshold > 85 KB). These block GC compaction and fragment the Large Object Heap.",
                Recommendation = "Refactor to avoid allocating extremely long strings; use ReadOnlySpan<char>, StringBuilder with limited buffering, or streaming APIs.",
                Tags = ["string", "loh", "fragmentation", "memory"],
                MetricValue = veryLongStringBytes,
                MetricUnit = "bytes",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (pctOfManagedHeap > 20.0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("dominates-managed-heap"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Dominates,
                    Measure = ClaimParts.Percent(pctOfManagedHeap),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(StringDominancePercent), "string dominance threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the managed heap", Mechanism = "string objects" },
                },
                Support = [new EvidenceStatement([composition.Id], $"Strings total {FormatHelper.FormatBytes(totalStringMemoryBytes)}, {pctOfManagedHeap:F1}% of the managed heap.")],
                Counter = Counter.None("Nothing observed separates the volume of text a service legitimately holds from retained garbage."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -stat",
                        Test = $"If System.String in !dumpheap -stat totals well below {FormatHelper.FormatBytes(totalStringMemoryBytes)}, or the managed heap total is much larger than assumed, the share is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <string address>",
                        Test = "On a sampled large string: if its root is a request frame, the strings are short-lived working data, not retained.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Strings dominate managed heap",
                Narrative = $"Strings consume {pctOfManagedHeap:F1}% of total managed heap ({FormatHelper.FormatBytes(totalStringMemoryBytes)}).",
                Recommendation = "Review string retention roots; consider ReadOnlySpan<char>, StringPool, or lazy string resolution.",
                Tags = ["string", "memory"],
                MetricValue = totalStringMemoryBytes,
                MetricUnit = "bytes",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Low sampling coverage is coverage, not a claim about the dump: see StringCoverageRule.

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    // Duplication figures come from the sampled dedup scan, so they're only as representative as
    // the sample; the exact whole-heap figures (string memory, LOH, very long strings) aren't.
    private static ConfidenceBreakdown SampledDuplicationConfidence(double samplingCoverage) => DataQualityPenalties.Apply(
        ConfidenceBreakdown.MeasurementCertain,
        new DataQualityPenalty(samplingCoverage > 0 && samplingCoverage < 0.05, 0.35,
            $"Sampling coverage is below 5% ({samplingCoverage * 100.0:F1}%); duplication and pattern statistics reflect only the sampled subset, not the full heap."),
        new DataQualityPenalty(samplingCoverage >= 0.05 && samplingCoverage < 0.5, 0.15,
            $"Sampling coverage is {samplingCoverage * 100.0:F1}%; results may not fully represent heap-wide patterns."));

    private const double ExtremeDuplicationRatio = 0.5;
    private const double StringDominancePercent = 20.0;
    private const double PartialCoverageCeiling = 0.5;

    private static Counter DuplicationCounter(Observation composition, double samplingCoverage) =>
        samplingCoverage > 0 && samplingCoverage < PartialCoverageCeiling
            ? Counter.Of(new EvidenceStatement(
                [composition.Id],
                $"Duplication was measured on a sample covering {samplingCoverage * 100.0:F1}% of strings, so the ratio may not hold across the whole heap."))
            : Counter.None("Duplicates inside short-lived structures are reclaimed with them; lifetime is not observed.");

    private static IReadOnlyList<FalsificationTest> DuplicationFalsifiedBy(int duplicatePatternCount, ulong duplicateWastedBytes) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!dumpheap -strings",
            Test = $"If the most repeated strings in !dumpheap -strings add up to far less than {FormatHelper.FormatBytes(duplicateWastedBytes)} across {duplicatePatternCount:N0} patterns, the waste is overstated.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = "If the repeated strings are already interned or pooled at the source, the duplicates are transient copies, not retained waste.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

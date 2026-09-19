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
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.string-composition"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = matched.Matched.FirstOrDefault(o => o.ObservationType == "type.string-composition");
        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

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

        var findings = new List<Finding>(capacity: 4);

        if (duplicationRatio > 0.5 && duplicatePatternCount > 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("extreme-duplication"),
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
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }
        else if (duplicatePatternCount > 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("high-duplicate-pressure"),
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
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (lohStringBytes > 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("loh-strings"),
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
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("very-long-strings"),
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
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("dominates-managed-heap"),
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

        if (samplingCoverage < 0.05 && samplingCoverage > 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("low-sampling-coverage"),
                Analyzer = AnalyzerName,
                Category = "Analysis",
                Severity = Severity.Info,
                Title = "Low sampling coverage on deduplication analysis",
                Narrative = $"String deduplication was performed on a sample covering only {samplingCoverage * 100.0:F1}% of all strings ({stringsSampled:N0} sampled out of ~{(int)(stringsSampled / Math.Max(samplingCoverage, 0.01)):N0} total). Results may not be representative of heap-wide patterns.",
                Recommendation = "Low coverage here reflects how many strings fell within MaxDuplicateStringLength, not a configurable sampling limit. Current results should be treated as indicative, not definitive.",
                Tags = ["sampling", "coverage", "deduplication"],
                MetricValue = samplingCoverage * 100.0,
                MetricUnit = "percent",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

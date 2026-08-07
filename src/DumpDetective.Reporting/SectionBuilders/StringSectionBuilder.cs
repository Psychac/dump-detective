using System.Linq;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StringSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "String Analysis";
    public string DisplayTitle => "String Analysis";
    public int SortOrder => 700;

    public bool CanHandle(AnalyzerDomainResult result) => result is StringDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StringDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.85, ["String statistics are measured from analyzed heap data."]),
        };

        string dedupLine = d.DeduplicationSkipped
            ? "Skipped"
            : $"Performed ({d.StringsSampled:N0} sampled, {(d.SamplingCoverage * 100.0):F1}% coverage)";

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_strings"] = new NumericMetricValue(d.TotalStrings, MetricUnit.Count),
            ["total_string_memory_bytes"] = new NumericMetricValue((double)d.TotalStringMemoryBytes, MetricUnit.Bytes),
            ["pct_of_managed_heap"] = new NumericMetricValue(d.PctOfManagedHeap, MetricUnit.Percent),
            ["sampled_unique_patterns"] = new NumericMetricValue(d.SampledUniquePatterns, MetricUnit.Count),
            ["sampling_coverage"] = new NumericMetricValue(d.SamplingCoverage * 100.0, MetricUnit.Percent),
            ["sampling_mode"] = new TextMetricValue(d.SamplingMode ?? "(unknown)"),
            ["dedup_mode"] = new TextMetricValue(d.DeduplicationMode ?? "(unknown)"),
            ["dedup_threshold"] = new NumericMetricValue(d.DeduplicationThreshold, MetricUnit.Count),
            ["max_to_dedup"] = new NumericMetricValue(d.MaxStringsToDedup, MetricUnit.Count),
            ["deduplication"] = new TextMetricValue(dedupLine),
            ["dedup_source"] = new TextMetricValue(d.DedupSource ?? "(none)"),
            ["analysis_duration_ms"] = (d.AnalysisDurationMs > 0)
                ? new NumericMetricValue((double)d.AnalysisDurationMs, MetricUnit.Milliseconds)
                : new TextMetricValue("N/A"),
            ["duplication_ratio"] = new NumericMetricValue(d.DuplicationRatio, MetricUnit.Ratio),
            ["duplicate_waste_bytes"] = new NumericMetricValue((double)d.DuplicateWastedBytes, MetricUnit.Bytes),
            ["loh_string_bytes"] = new NumericMetricValue((double)d.LohStringBytes, MetricUnit.Bytes),
            ["gen2_string_count"] = new NumericMetricValue(d.Gen2StringCount, MetricUnit.Count),
            ["interned_strings_foh"] = new NumericMetricValue(d.InternedStringCount, MetricUnit.Count),
            ["interned_string_bytes"] = new NumericMetricValue((double)d.InternedStringBytes, MetricUnit.Bytes),
        };
        if (!string.IsNullOrEmpty(d.DedupSkipReason))
            keyMetrics["dedup_skip_reason"] = new TextMetricValue(d.DedupSkipReason);

        if (d.Distribution is not null && d.Distribution.SampleCount > 0)
        {
            keyMetrics["samples"] = new NumericMetricValue(d.Distribution.SampleCount, MetricUnit.Count);
            var p = d.Distribution.Percentiles ?? new System.Collections.Generic.Dictionary<string, double>();
            if (p.Count > 0)
            {
                keyMetrics["p50_median"] = new NumericMetricValue((double)p.GetValueOrDefault("p50", 0), MetricUnit.Count);
                keyMetrics["p75"] = new NumericMetricValue((double)p.GetValueOrDefault("p75", 0), MetricUnit.Count);
                keyMetrics["p90"] = new NumericMetricValue((double)p.GetValueOrDefault("p90", 0), MetricUnit.Count);
                keyMetrics["p95"] = new NumericMetricValue((double)p.GetValueOrDefault("p95", 0), MetricUnit.Count);
            }

            var lb = d.Distribution.LengthBuckets ?? new System.Collections.Generic.Dictionary<string, int>();
            if (lb.Count > 0)
            {
                var lbRows = new System.Collections.Generic.List<TableRow>(lb.Count);
                foreach (var kv in lb)
                {
                    double pct = d.Distribution.SampleCount > 0 ? kv.Value * 100.0 / d.Distribution.SampleCount : 0.0;
                    lbRows.Add(new TableRow([Cell(kv.Key), Cell($"{kv.Value:N0}", kv.Value), Cell($"{pct:F1}%", null)]));
                }
                compactTables.Add(STCompact("String length buckets", new[] { CH("Range"), CH("Count","number"), CH("% of samples", "number", "percent") }, lbRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            var fb = d.Distribution.FrequencyBuckets ?? new System.Collections.Generic.Dictionary<string, int>();
            if (fb.Count > 0)
            {
                var fbRows = new System.Collections.Generic.List<TableRow>(fb.Count);
                int totalPatterns = fb.Values.Sum();
                foreach (var kv in fb)
                {
                    double pct = totalPatterns > 0 ? kv.Value * 100.0 / totalPatterns : 0.0;
                    fbRows.Add(new TableRow([Cell(kv.Key), Cell($"{kv.Value:N0}", kv.Value), Cell($"{pct:F1}%", null)]));
                }
                compactTables.Add(STCompact("Duplicate frequency buckets", new[] { CH("Frequency","number"), CH("Pattern Count","number"), CH("% of patterns", "number", "percent") }, fbRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }

        if (d.TopDuplicateTypes is not null && d.TopDuplicateTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicateTypes.Count);
            foreach (var t in d.TopDuplicateTypes)
                rows.Add(Row(Cell(t.Name), Cell($"{t.Count:N0}", t.Count)));
            compactTables.Add(STCompact("Types by duplicate occurrence", new[] { CH("Type"), CH("Duplicate Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopDuplicates.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicates.Count);
            for (int i = 0; i < d.TopDuplicates.Count; i++)
            {
                var dup = d.TopDuplicates[i];
                double pct = d.TotalStringMemoryBytes > 0 ? dup.WastedBytes * 100.0 / d.TotalStringMemoryBytes : 0.0;
                string preview = FormatHelper.TruncateString(dup.Preview, Math.Max(32, d.PreviewMaxLength));
                string examples = dup.SampleAddresses is not null ? string.Join(", ", dup.SampleAddresses.Select(a => $"0x{a:X}")) : string.Empty;
                string fingerprint = dup.FingerprintHash is not null ? $"0x{dup.FingerprintHash.Value:X16}" : "—";
                string totalSize = dup.TotalSize > 0 ? FormatHelper.FormatBytes(dup.TotalSize) : "(n/a)";
                string avgSize = dup.AvgSize > 0 ? FormatHelper.FormatBytes((ulong)dup.AvgSize) : "(n/a)";
                string sampling = string.IsNullOrWhiteSpace(dup.SamplingSource) ? "—" : dup.SamplingSource;
                rows.Add(Row(
                    Cell(fingerprint),
                    Cell(preview),
                    Cell($"{dup.Count:N0}", dup.Count),
                    Cell(avgSize, dup.AvgSize),
                    Cell(totalSize, (long)dup.TotalSize),
                    Cell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes),
                    Cell($"{pct:F1}%", null),
                    Cell(dup.DominantType ?? (dup.DominantMethodTable != 0 ? $"0x{dup.DominantMethodTable:X}" : string.Empty)),
                    Cell(sampling),
                    Cell(examples)));
            }
            compactTables.Add(STCompact("Top duplicate strings", new[] { CH("Fingerprint"), CH("Preview"), CH("Count","number"), CH("Avg Size","bytes"), CH("Total Size","bytes"), CH("Wasted","bytes"), CH("% of strings", "number", "percent"), CH("Dominant Type"), CH("Sampling"), CH("Examples") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Very long strings → typed Tables slot
        if (d.VeryLongStrings.Count > 0)
        {
            var rows = new List<TableRow>(d.VeryLongStrings.Count);
            for (int i = 0; i < d.VeryLongStrings.Count; i++)
            {
                var s = d.VeryLongStrings[i];
                string previewDisplay = string.IsNullOrEmpty(s.Preview) ? "(no preview)" : s.Preview.Length > 50 ? s.Preview[..50] + "..." : s.Preview;
                rows.Add(Row(
                    Cell($"0x{s.Address:X}"),
                    Cell($"{s.CharLength:N0} chars", s.CharLength),
                    Cell(FormatHelper.FormatBytes(s.SizeBytes), (long)s.SizeBytes),
                    Cell(previewDisplay),
                    Cell(s.TypeName ?? "(unknown)")));
            }
            compactTables.Add(STCompact("Strings exceeding LOH threshold", new[] { CH("Address"), CH("Char Length","number"), CH("Size","bytes"), CH("Preview"), CH("Type") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

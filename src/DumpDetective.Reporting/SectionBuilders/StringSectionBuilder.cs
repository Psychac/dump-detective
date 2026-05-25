using System.Linq;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StringSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "String Analysis";
    public string DisplayTitle => "String Analysis";
    public int SortOrder => 26;

    public bool CanHandle(AnalyzerDomainResult result) => result is StringDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StringDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.85, ["String statistics are measured from analyzed heap data."]),
        };

        ulong estimatedInterningSaving = 0;
        int interningLimit = Math.Min(d.TopDuplicatesByWaste.Count, 20);
        for (int i = 0; i < interningLimit; i++)
            estimatedInterningSaving += d.TopDuplicatesByWaste[i].WastedBytes;

        string dedupLine = d.DeduplicationSkipped
            ? "Skipped"
            : $"Performed ({d.StringsSampled:N0} sampled, {(d.SamplingCoverage * 100.0):F1}% coverage)";

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Strings",              $"{d.TotalStrings:N0}",                                    d.TotalStrings),
            KM("Total String Memory",        FormatHelper.FormatBytes(d.TotalStringMemoryBytes),         (double)d.TotalStringMemoryBytes),
            KM("% of Managed Heap",          $"{d.PctOfManagedHeap:F2}%",                              d.PctOfManagedHeap),
            KM("Unique Strings",             $"{d.UniqueStrings:N0}",                                   d.UniqueStrings),
            KM("Sampling Mode",              d.SamplingMode ?? "(unknown)"),
            KM("Dedup Mode",                 d.DeduplicationMode ?? "(unknown)"),
            KM("Dedup Threshold",            $"{d.DeduplicationThreshold:N0}",                          d.DeduplicationThreshold),
            KM("Max To Dedup",               $"{d.MaxStringsToDedup:N0}",                              d.MaxStringsToDedup),
            KM("Deduplication",              dedupLine,                                                  d.DeduplicationSkipped ? 0 : d.StringsSampled),
            KM("Dedup Source",               d.DedupSource ?? "(none)"),
            KM("Analysis Duration",          d.AnalysisDurationMs > 0 ? $"{d.AnalysisDurationMs} ms" : "(n/a)", (double)d.AnalysisDurationMs),
            KM("Duplication Ratio",          $"{d.DuplicationRatio:P1}",                               d.DuplicationRatio),
            KM("Duplicate Waste",            FormatHelper.FormatBytes(d.DuplicateWastedBytes),           (double)d.DuplicateWastedBytes),
            KM("Estimated Interning Saving", FormatHelper.FormatBytes(estimatedInterningSaving),         (double)estimatedInterningSaving),
            KM("LOH String Bytes",           FormatHelper.FormatBytes(d.LohStringBytes),                (double)d.LohStringBytes),
            KM("Gen2 String Count",          $"{d.Gen2StringCount:N0}",                                  d.Gen2StringCount),
            KM("Interned Strings (FOH)",     $"{d.InternedStringCount:N0} ({FormatHelper.FormatBytes(d.InternedStringBytes)})", d.InternedStringCount),
        };
        if (!string.IsNullOrEmpty(d.DedupSkipReason))
            keyMetrics.Add(KM("Dedup Skip Reason", d.DedupSkipReason));

        if (d.Distribution is not null && d.Distribution.SampleCount > 0)
        {
            keyMetrics.Add(KM("Samples", $"{d.Distribution.SampleCount:N0}", d.Distribution.SampleCount));
            var p = d.Distribution.Percentiles ?? new System.Collections.Generic.Dictionary<string, double>();
            if (p.Count > 0)
            {
                keyMetrics.Add(KM("p50 (median)", $"{p.GetValueOrDefault("p50", 0):F0} chars", (double)p.GetValueOrDefault("p50", 0)));
                keyMetrics.Add(KM("p75",           $"{p.GetValueOrDefault("p75", 0):F0} chars", (double)p.GetValueOrDefault("p75", 0)));
                keyMetrics.Add(KM("p90",           $"{p.GetValueOrDefault("p90", 0):F0} chars", (double)p.GetValueOrDefault("p90", 0)));
                keyMetrics.Add(KM("p95",           $"{p.GetValueOrDefault("p95", 0):F0} chars", (double)p.GetValueOrDefault("p95", 0)));
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
                tables.Add(ST("String length buckets", ["Range", "Count", "% of samples"], lbRows));
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
                tables.Add(ST("Duplicate frequency buckets", ["Frequency", "Pattern Count", "% of patterns"], fbRows));
            }
        }

        if (d.TopDuplicateTypes is not null && d.TopDuplicateTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicateTypes.Count);
            foreach (var t in d.TopDuplicateTypes)
                rows.Add(Row(Cell(t.Name), Cell($"{t.Count:N0}", t.Count)));
            tables.Add(ST("Types by duplicate occurrence", ["Type", "Duplicate Count"], rows));
        }

        // Duplicates by waste → typed Tables slot (renderer collapses automatically)
        if (d.TopDuplicatesByWaste.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicatesByWaste.Count);
            for (int i = 0; i < d.TopDuplicatesByWaste.Count; i++)
            {
                var dup = d.TopDuplicatesByWaste[i];
                double pct = d.TotalStringMemoryBytes > 0 ? dup.WastedBytes * 100.0 / d.TotalStringMemoryBytes : 0.0;
                string preview = FormatHelper.TruncateString(dup.Preview, Math.Max(32, d.PreviewMaxLength));
                string examples = dup.SampleAddresses is not null ? string.Join(", ", dup.SampleAddresses.Select(a => $"0x{a:X}")) : string.Empty;
                string fingerprint = dup.FingerprintHash is not null ? $"0x{dup.FingerprintHash.Value:X16}" : string.Empty;
                string totalSize = dup.TotalSize > 0 ? FormatHelper.FormatBytes(dup.TotalSize) : "(n/a)";
                string avgSize = dup.AvgSize > 0 ? FormatHelper.FormatBytes((ulong)dup.AvgSize) : "(n/a)";
                rows.Add(Row(
                    Cell(fingerprint),
                    Cell(preview),
                    Cell($"{dup.Count:N0}", dup.Count),
                    Cell(avgSize, dup.AvgSize),
                    Cell(totalSize, (long)dup.TotalSize),
                    Cell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes),
                    Cell($"{pct:F1}%", null),
                    Cell(dup.DominantType ?? (dup.DominantMethodTable != 0 ? $"0x{dup.DominantMethodTable:X}" : string.Empty)),
                    Cell(dup.SamplingSource ?? string.Empty),
                    Cell(examples)));
            }
            tables.Add(ST("Duplicates ranked by wasted bytes",
                ["Fingerprint", "Preview", "Count", "Avg Size", "Total Size", "Wasted", "% of strings", "Dominant Type", "Sampling", "Examples"],
                rows));
        }

        // Duplicates by count → typed Tables slot
        if (d.TopDuplicatesByCount.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicatesByCount.Count);
            for (int i = 0; i < d.TopDuplicatesByCount.Count; i++)
            {
                var dup = d.TopDuplicatesByCount[i];
                double pct = d.TotalStringMemoryBytes > 0 ? dup.WastedBytes * 100.0 / d.TotalStringMemoryBytes : 0.0;
                string preview = FormatHelper.TruncateString(dup.Preview, Math.Max(32, d.PreviewMaxLength));
                string examples = dup.SampleAddresses is not null ? string.Join(", ", dup.SampleAddresses.Select(a => $"0x{a:X}")) : string.Empty;
                string fingerprint = dup.FingerprintHash is not null ? $"0x{dup.FingerprintHash.Value:X16}" : string.Empty;
                string totalSize = dup.TotalSize > 0 ? FormatHelper.FormatBytes(dup.TotalSize) : "(n/a)";
                string avgSize = dup.AvgSize > 0 ? FormatHelper.FormatBytes((ulong)dup.AvgSize) : "(n/a)";
                rows.Add(Row(
                    Cell(fingerprint),
                    Cell(preview),
                    Cell($"{dup.Count:N0}", dup.Count),
                    Cell(avgSize, dup.AvgSize),
                    Cell(totalSize, (long)dup.TotalSize),
                    Cell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes),
                    Cell($"{pct:F1}%", null),
                    Cell(dup.DominantType ?? (dup.DominantMethodTable != 0 ? $"0x{dup.DominantMethodTable:X}" : string.Empty)),
                    Cell(dup.SamplingSource ?? string.Empty),
                    Cell(examples)));
            }
            tables.Add(ST("Duplicates ranked by count",
                ["Fingerprint", "Preview", "Count", "Avg Size", "Total Size", "Wasted", "% of strings", "Dominant Type", "Sampling", "Examples"],
                rows));
        }

        // Very long strings → typed Tables slot
        if (d.VeryLongStrings.Count > 0)
        {
            var rows = new List<TableRow>(d.VeryLongStrings.Count);
            for (int i = 0; i < d.VeryLongStrings.Count; i++)
            {
                var s = d.VeryLongStrings[i];
                rows.Add(Row(
                    Cell($"0x{s.Address:X}"),
                    Cell($"{s.CharLength:N0} chars", s.CharLength),
                    Cell(FormatHelper.FormatBytes(s.SizeBytes), (long)s.SizeBytes)));
            }
            tables.Add(ST("Strings exceeding LOH threshold", ["Address", "Char Length", "Size"], rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}

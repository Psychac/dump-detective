using System.Linq;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StringSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName  => "String Analysis";
    public string DisplayTitle  => "String & Duplicate Analysis";
    public int    SortOrder     => 26;

    public bool CanHandle(AnalyzerDomainResult result) => result is StringDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StringDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Strings",           $"{d.TotalStrings:N0}",                                  d.TotalStrings));
        blocks.Add(M("Total String Memory",     FormatHelper.FormatBytes(d.TotalStringMemoryBytes),       (double)d.TotalStringMemoryBytes));
        blocks.Add(M("% of Managed Heap",       $"{d.PctOfManagedHeap:F2}%",                             d.PctOfManagedHeap));
        blocks.Add(M("Unique Strings",          $"{d.UniqueStrings:N0}",                                 d.UniqueStrings));
        // New sampling/dedup metadata
        blocks.Add(M("Sampling Mode",           d.SamplingMode ?? "(unknown)",                           null));
        blocks.Add(M("Dedup Mode",              d.DeduplicationMode ?? "(unknown)",                     null));
        blocks.Add(M("Dedup Threshold",         $"{d.DeduplicationThreshold:N0}",                        d.DeduplicationThreshold));
        blocks.Add(M("Max To Dedup",            $"{d.MaxStringsToDedup:N0}",                            d.MaxStringsToDedup));
        string dedupLine = d.DeduplicationSkipped
            ? "Skipped"
            : $"Performed ({d.StringsSampled:N0} sampled, {(d.SamplingCoverage * 100.0):F1}% coverage)";
        blocks.Add(M("Deduplication",           dedupLine, d.DeduplicationSkipped ? 0 : d.StringsSampled));
        blocks.Add(M("Dedup Source",            d.DedupSource ?? "(none)",                              null));
        blocks.Add(M("Analysis Duration",       d.AnalysisDurationMs > 0 ? $"{d.AnalysisDurationMs} ms" : "(n/a)",  (double)d.AnalysisDurationMs));
        if (!string.IsNullOrEmpty(d.DedupSkipReason)) blocks.Add(M("Dedup Skip Reason", d.DedupSkipReason, null));
        blocks.Add(M("Duplication Ratio",       $"{d.DuplicationRatio:P1}",                              d.DuplicationRatio));
        blocks.Add(M("Duplicate Waste",         FormatHelper.FormatBytes(d.DuplicateWastedBytes),         (double)d.DuplicateWastedBytes));
        blocks.Add(M("LOH String Bytes",        FormatHelper.FormatBytes(d.LohStringBytes),               (double)d.LohStringBytes));
        blocks.Add(M("Gen2 String Count",       $"{d.Gen2StringCount:N0}",                               d.Gen2StringCount));
        blocks.Add(M("Interned Strings (FOH)",  $"{d.InternedStringCount:N0} ({FormatHelper.FormatBytes(d.InternedStringBytes)})", d.InternedStringCount));

        // ── Distribution summary (percentiles + histogram) ─────────────────────────
        if (d.Distribution is not null && d.Distribution.SampleCount > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("STRING LENGTH DISTRIBUTION"));
            blocks.Add(Divider());
            blocks.Add(M("Samples", $"{d.Distribution.SampleCount:N0}", d.Distribution.SampleCount));

            var p = d.Distribution.Percentiles ?? new System.Collections.Generic.Dictionary<string,double>();
            if (p.Count > 0)
            {
                blocks.Add(M("p50 (median)", $"{p.GetValueOrDefault("p50", 0):F0} chars", (double)p.GetValueOrDefault("p50", 0)));
                blocks.Add(M("p75", $"{p.GetValueOrDefault("p75", 0):F0} chars", (double)p.GetValueOrDefault("p75", 0)));
                blocks.Add(M("p90", $"{p.GetValueOrDefault("p90", 0):F0} chars", (double)p.GetValueOrDefault("p90", 0)));
                blocks.Add(M("p95", $"{p.GetValueOrDefault("p95", 0):F0} chars", (double)p.GetValueOrDefault("p95", 0)));
            }

            // Length buckets table
            var lb = d.Distribution.LengthBuckets ?? new System.Collections.Generic.Dictionary<string,int>();
            if (lb.Count > 0)
            {
                blocks.Add(Blank());
                var lbRows = new System.Collections.Generic.List<TableRow>(lb.Count);
                foreach (var kv in lb)
                {
                    double pct = d.Distribution.SampleCount > 0 ? kv.Value * 100.0 / d.Distribution.SampleCount : 0.0;
                    lbRows.Add(new TableRow([Cell(kv.Key), Cell($"{kv.Value:N0}", kv.Value), Cell($"{pct:F1}%", null)]));
                }
                blocks.Add(new TableBlock("String length buckets", ["Range", "Count", "% of samples"], lbRows));
            }

            // Frequency buckets (how many patterns appear X times)
            var fb = d.Distribution.FrequencyBuckets ?? new System.Collections.Generic.Dictionary<string,int>();
            if (fb.Count > 0)
            {
                blocks.Add(Blank());
                var fbRows = new System.Collections.Generic.List<TableRow>(fb.Count);
                int totalPatterns = fb.Values.Sum();
                foreach (var kv in fb)
                {
                    double pct = totalPatterns > 0 ? kv.Value * 100.0 / totalPatterns : 0.0;
                    fbRows.Add(new TableRow([Cell(kv.Key), Cell($"{kv.Value:N0}", kv.Value), Cell($"{pct:F1}%", null)]));
                }
                blocks.Add(new TableBlock("Duplicate frequency buckets", ["Frequency", "Pattern Count", "% of patterns"], fbRows));
            }
        }

        // ── Top Duplicates by Waste ───────────────────────────────────────────
        if (d.TopDuplicatesByWaste.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP DUPLICATES BY WASTED BYTES"));
            blocks.Add(CollapseBegin("Top duplicate strings by memory waste"));
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
            blocks.Add(new TableBlock("Duplicates ranked by wasted bytes", ["Fingerprint","Preview", "Count", "Avg Size", "Total Size", "Wasted", "% of strings", "Dominant Type", "Sampling", "Examples"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── Top Duplicates by Count ───────────────────────────────────────────
        if (d.TopDuplicatesByCount.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP DUPLICATES BY COUNT"));
            blocks.Add(CollapseBegin("Top duplicate strings by occurrence count"));
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
            blocks.Add(new TableBlock("Duplicates ranked by count", ["Fingerprint","Preview", "Count", "Avg Size", "Total Size", "Wasted", "% of strings", "Dominant Type", "Sampling", "Examples"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── Top types contributing to duplicates ─────────────────────────────────
        if (d.TopDuplicateTypes is not null && d.TopDuplicateTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP TYPES CONTRIBUTING TO DUPLICATION"));
            var rows = new List<TableRow>(d.TopDuplicateTypes.Count);
            foreach (var t in d.TopDuplicateTypes)
                rows.Add(Row(Cell(t.Name), Cell($"{t.Count:N0}", t.Count)));
            blocks.Add(new TableBlock("Types by duplicate occurrence", ["Type", "Duplicate Count"], rows));
        }

        // ── Very Long Strings (LOH residents) ────────────────────────────────
        if (d.VeryLongStrings.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("VERY LONG STRINGS (> 85 KB)"));
            blocks.Add(CollapseBegin("Very long strings on LOH"));
            var rows = new List<TableRow>(d.VeryLongStrings.Count);
            for (int i = 0; i < d.VeryLongStrings.Count; i++)
            {
                var s = d.VeryLongStrings[i];
                rows.Add(Row(
                    Cell($"0x{s.Address:X}"),
                    Cell($"{s.CharLength:N0} chars", s.CharLength),
                    Cell(FormatHelper.FormatBytes(s.SizeBytes), (long)s.SizeBytes)));
            }
            blocks.Add(new TableBlock("Strings exceeding LOH threshold", ["Address", "Char Length", "Size"], rows));
            blocks.Add(CollapseEnd());
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}

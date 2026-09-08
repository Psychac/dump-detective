using System.Linq;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StringSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int PreviewDisplayLength = 80;

    public string AnalyzerName => "String Analysis";
    public string DisplayTitle => "String Analysis";
    public int SortOrder => 700;

    public bool CanHandle(AnalyzerDomainResult result) => result is StringDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StringDomainResult)result;
        var compactTables = new List<CompactTable>();

        // P3-3: confidence should fall as dedup SamplingCoverage falls — a report built from a
        // 1% sample of the strings on the heap isn't as trustworthy as one built from a full
        // scan. Thresholds match StringFindingGenerator's existing "low coverage" warning (< 5%)
        // so the confidence band and that finding agree on what counts as low coverage.
        var (confidenceScore, coverageCaveats) = ConfidenceScoring.Compute(0.85,
            ConfidenceScoring.F(d.SamplingCoverage > 0 && d.SamplingCoverage < 0.05, 0.35,
                $"Sampling coverage is below 5% ({d.SamplingCoverage * 100.0:F1}%); duplication and pattern statistics reflect only the sampled subset, not the full heap."),
            ConfidenceScoring.F(d.SamplingCoverage >= 0.05 && d.SamplingCoverage < 0.5, 0.15,
                $"Sampling coverage is {d.SamplingCoverage * 100.0:F1}%; results may not fully represent heap-wide patterns."));

        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(confidenceScore,
                new[] { "String statistics are measured from analyzed heap data." }
                    .Concat(coverageCaveats)
                    .ToArray()),
        };

        string dedupLine = $"Performed ({d.StringsSampled:N0} sampled, {(d.SamplingCoverage * 100.0):F1}% coverage)";

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_strings"] = new NumericMetricValue(d.TotalStrings, MetricUnit.Count),
            ["total_string_memory_bytes"] = new NumericMetricValue((double)d.TotalStringMemoryBytes, MetricUnit.Bytes),
            ["pct_of_managed_heap"] = new NumericMetricValue(d.PctOfManagedHeap, MetricUnit.Percent),
            ["sampled_unique_patterns"] = new NumericMetricValue(d.SampledUniquePatterns, MetricUnit.Count),
            ["sampling_coverage"] = new NumericMetricValue(d.SamplingCoverage * 100.0, MetricUnit.Percent),
            ["deduplication"] = new TextMetricValue(dedupLine),
            ["dedup_source"] = new TextMetricValue(d.DedupSource ?? "(none)"),
            ["analysis_duration_ms"] = (d.AnalysisDurationMs > 0)
                ? new NumericMetricValue((double)d.AnalysisDurationMs, MetricUnit.Milliseconds)
                : new TextMetricValue("N/A"),
            ["duplication_ratio"] = new NumericMetricValue(d.DuplicationRatio, MetricUnit.Ratio),
            ["duplicate_waste_bytes"] = new NumericMetricValue((double)d.DuplicateWastedBytes, MetricUnit.Bytes),
            ["loh_string_bytes"] = new NumericMetricValue((double)d.LohStringBytes, MetricUnit.Bytes),
            ["gen0_string_count"] = new NumericMetricValue(d.Gen0StringCount, MetricUnit.Count),
            ["gen1_string_count"] = new NumericMetricValue(d.Gen1StringCount, MetricUnit.Count),
            ["gen2_string_count"] = new NumericMetricValue(d.Gen2StringCount, MetricUnit.Count),
            ["interned_strings_foh"] = new NumericMetricValue(d.InternedStringCount, MetricUnit.Count),
            ["interned_string_bytes"] = new NumericMetricValue((double)d.InternedStringBytes, MetricUnit.Bytes),
        };

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

        // P1-3: Top types owning string fields
        if (d.TopStringOwnerTypes is not null && d.TopStringOwnerTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopStringOwnerTypes.Count);
            foreach (var (typeName, totalBytes) in d.TopStringOwnerTypes)
            {
                double pct = d.TotalStringMemoryBytes > 0 ? totalBytes * 100.0 / d.TotalStringMemoryBytes : 0.0;
                rows.Add(Row(Cell(typeName), Cell(FormatHelper.FormatBytes(totalBytes), (long)totalBytes), Cell($"{pct:F1}%", null)));
            }
            compactTables.Add(STCompact("Types by string field ownership", new[] { CH("Type"), CH("Total String Bytes","bytes"), CH("% of string memory", "number", "percent") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopDuplicates.Count > 0)
        {
            var rows = new List<TableRow>(d.TopDuplicates.Count);
            for (int i = 0; i < d.TopDuplicates.Count; i++)
            {
                var dup = d.TopDuplicates[i];
                double pct = d.TotalStringMemoryBytes > 0 ? dup.WastedBytes * 100.0 / d.TotalStringMemoryBytes : 0.0;
                string preview = FormatHelper.TruncateString(dup.Preview, PreviewDisplayLength);
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

        // P3-1: group duplicate patterns that share a common prefix (e.g. templated/formatted
        // strings differing only in a trailing id/timestamp) — surfaces duplication that exact
        // fingerprint matching alone misses.
        IReadOnlyList<StringPrefixCluster> prefixClusters = BuildPrefixClusters(d.TopDuplicates);
        if (prefixClusters.Count > 0)
        {
            var clusterRows = new List<CompactRow>(prefixClusters.Count);
            foreach (StringPrefixCluster cluster in prefixClusters)
            {
                clusterRows.Add(R(
                    FormatHelper.TruncateString(cluster.Prefix, PreviewDisplayLength),
                    cluster.PatternCount,
                    cluster.TotalOccurrences,
                    (long)cluster.TotalWastedBytes));
            }
            compactTables.Add(STCompact("String prefix clusters",
                [CH("Common Prefix"), CH("Distinct Patterns", "number"), CH("Total Occurrences", "number"), CH("Total Wasted", "bytes")],
                clusterRows));
        }

        // P3-2: GC root-path search results for the top duplicate patterns — "why is this
        // duplicated value still alive," bounded by StringAnalysisOptions.RetentionPathSampleCount.
        if (d.TopDuplicateRetentionPaths is { Count: > 0 })
        {
            var retentionRows = new List<CompactRow>(d.TopDuplicateRetentionPaths.Count);
            foreach (DuplicateStringRetentionPath rp in d.TopDuplicateRetentionPaths)
            {
                retentionRows.Add(R(
                    FormatHelper.TruncateString(rp.Preview, PreviewDisplayLength),
                    $"0x{rp.SampleAddress:X}",
                    rp.HasGcRoot ? (rp.RootPath ?? "(root found, path unavailable)") : "(no root found)",
                    rp.SearchTruncated ? "Yes" : "No"));
            }
            compactTables.Add(STCompact("Duplicate string retention paths",
                [CH("Preview"), CH("Sample Address"), CH("GC Root Path"), CH("Search Truncated")],
                retentionRows));
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

    private const int MinClusterPrefixLength = 8;
    private const int MinClusterSize = 2;

    /// <summary>
    /// Groups duplicate string patterns that share a long common prefix — e.g.
    /// <c>"OrderId=12345"</c> and <c>"OrderId=67890"</c> are distinct exact-match fingerprints
    /// (so <see cref="StringDomainResult.TopDuplicates"/> lists them separately) but together
    /// point at a single templated/formatted-string call site worth deduplicating.
    /// </summary>
    /// <remarks>
    /// Sorts previews ordinally, then does a single greedy left-to-right pass merging each item
    /// into the running cluster while the shared prefix stays &gt;= <see cref="MinClusterPrefixLength"/>,
    /// narrowing the cluster's prefix to the new common length as it grows. This is O(n log n)
    /// and simple, at the cost of being order-sensitive: an outlier sandwiched between two
    /// similar previews can split what a full pairwise comparison would treat as one cluster.
    /// Acceptable for a "does this class of duplication exist" signal; not a claim of optimal
    /// clustering.
    /// </remarks>
    private static IReadOnlyList<StringPrefixCluster> BuildPrefixClusters(IReadOnlyList<DuplicateStringSnapshot> topDuplicates)
    {
        if (topDuplicates.Count < MinClusterSize) return [];

        var sorted = new List<DuplicateStringSnapshot>(topDuplicates);
        sorted.Sort(static (a, b) => string.CompareOrdinal(a.Preview, b.Preview));

        var clusters = new List<StringPrefixCluster>();
        var members = new List<DuplicateStringSnapshot> { sorted[0] };
        string clusterPrefix = sorted[0].Preview;

        for (int i = 1; i < sorted.Count; i++)
        {
            DuplicateStringSnapshot next = sorted[i];
            int sharedLength = CommonPrefixLength(clusterPrefix, next.Preview);
            if (sharedLength >= MinClusterPrefixLength)
            {
                members.Add(next);
                clusterPrefix = clusterPrefix[..sharedLength];
            }
            else
            {
                FlushCluster(clusters, members, clusterPrefix);
                members = [next];
                clusterPrefix = next.Preview;
            }
        }
        FlushCluster(clusters, members, clusterPrefix);

        clusters.Sort(static (a, b) => b.TotalWastedBytes.CompareTo(a.TotalWastedBytes));
        return clusters;

        static void FlushCluster(List<StringPrefixCluster> clusters, List<DuplicateStringSnapshot> members, string prefix)
        {
            if (members.Count < MinClusterSize) return;

            int totalOccurrences = 0;
            ulong totalWastedBytes = 0;
            foreach (DuplicateStringSnapshot m in members)
            {
                totalOccurrences += m.Count;
                totalWastedBytes += m.WastedBytes;
            }
            clusters.Add(new StringPrefixCluster(prefix, members.Count, totalOccurrences, totalWastedBytes));
        }
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }

    private sealed record StringPrefixCluster(string Prefix, int PatternCount, int TotalOccurrences, ulong TotalWastedBytes);
}

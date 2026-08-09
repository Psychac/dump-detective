using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>A3 — Dominator Analysis and retention hotspots. Source: <see cref="DominatorDomainResult"/>.</summary>
internal sealed class DominatorSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Dominator Analysis";
    public string DisplayTitle => "Dominator Analysis";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is DominatorDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DominatorDomainResult)result;
        var (confidenceScore, caveats) = ConfidenceScoring.Compute(0.75,
            ConfidenceScoring.F(d.ObjectScanCapped, 0.15, "Object scan was capped; retention counts may be partial."),
            ConfidenceScoring.F(d.ReferenceCountingSkipped, 0.20, "Reference counting was skipped; results are estimated."),
            ConfidenceScoring.F(d.SkippedReferenceAddresses > 0, 0.10, $"{d.SkippedReferenceAddresses:N0} reference addresses were skipped."));

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(confidenceScore,
                new[] { "Retained bytes are bounded BFS estimates, not a true Lengauer-Tarjan dominator tree." }
                    .Concat(caveats)
                    .ToArray()),
        };

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["candidate_count"] = new NumericMetricValue(d.CandidateCount, MetricUnit.Count),
            ["analyzed_count"] = new NumericMetricValue(d.AnalyzedCount, MetricUnit.Count),
            ["total_retained_est"] = new NumericMetricValue((double)Math.Min(d.TotalEstimatedRetainedBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TotalEstimatedRetainedBytes)),
            ["max_bfs_breadth"] = new NumericMetricValue(d.MaxBreadth, MetricUnit.Count),
            ["max_bfs_depth"] = new NumericMetricValue(d.MaxDepth, MetricUnit.Count),
            ["highly_referenced_objects"] = new NumericMetricValue(d.HighlyReferencedObjectCount, MetricUnit.Count),
            ["top_retained_total"] = new NumericMetricValue((double)Math.Min(d.TopHighlyReferencedTotalBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TopHighlyReferencedTotalBytes)),
            ["skipped_ref_addresses"] = new NumericMetricValue(d.SkippedReferenceAddresses, MetricUnit.Count),
        };

        if (d.TopDominatorTypes.Count > 0)
        {
            blocks.Add(T($"Retained bytes are estimated with a bounded BFS over {d.AnalyzedCount:N0} suspects (breadth cap {d.MaxBreadth:N0}, depth cap {d.MaxDepth:N0})."));

            compactTables.Add(STCompact(
                "Top dominator suspects by retained bytes",
                new[] { CH("Type"), CH("Objects","number"), CH("Gen2","number"), CH("Shallow","bytes"), CH("LOH","bytes"), CH("Retained","bytes"), CH("Ratio", "number", "permille"), CH("Avg Size","bytes"), CH("Sample Addr") },
                d.TopDominatorTypes.Take(d.MaxTopDominatorTypesToShow).Select(type => R(
                    type.TypeName,
                    type.Count,
                    type.Gen2Count > 0 ? type.Gen2Count : null,
                    type.TotalBytes,
                    type.LohBytes > 0 ? type.LohBytes : null,
                    type.EstimatedRetainedBytes > 0 ? type.EstimatedRetainedBytes : null,
                    (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000),
                    type.AverageSize > 0 ? type.AverageSize : null,
                    $"0x{type.SampleAddress:X}")).ToArray()));
            if (d.TotalEstimatedRetainedBytes > 0)
            {
                compactTables.Add(STCompact(
                    "Dominator impact per-mille (of total estimated retained)",
                    new[] { CH("Type"), CH("Est. Retained","bytes"), CH("Per-mille", "number", "permille") },
                    d.TopDominatorTypes.Take(d.MaxTopDominatorTypesToShow).Select(type => R(
                        type.TypeName,
                        type.EstimatedRetainedBytes > 0 ? type.EstimatedRetainedBytes : null,
                        type.EstimatedRetainedBytes == 0 ? null : (double)type.EstimatedRetainedBytes * 1000 / d.TotalEstimatedRetainedBytes)).ToArray()));
            }
        }

        if (d.TopHighlyReferencedObjects is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Highly referenced objects",
                new[] { CH("Address"), CH("Type"), CH("Size","bytes"), CH("Incoming Refs","number"), CH("Est. Retained","bytes") },
                d.TopHighlyReferencedObjects.Take(d.MaxTopDominatorTypesToShow).Select(o => R(new object?[] { $"0x{o.Address:X}", o.TypeName, o.Size, o.IncomingReferences, o.EstimatedRetainedBytes > 0 ? o.EstimatedRetainedBytes : null })).ToArray()));
        }

        if (d.TopRetentionTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Top retention types",
                new[] { CH("Type"), CH("Objects","number"), CH("Footprint","bytes"), CH("Total Incoming Refs","number"), CH("Max Incoming Refs","number"), CH("Est. Retained","bytes"), CH("Ratio", "number", "ratio") },
                d.TopRetentionTypes.Take(d.MaxTopDominatorTypesToShow).Select(t => R(new object?[] { t.TypeName, t.ObjectCount, t.TotalBytes, t.TotalIncomingReferences, t.MaxIncomingReferences, t.EstimatedRetainedBytes > 0 ? t.EstimatedRetainedBytes : null, RatioValue(t.EstimatedRetainedBytes, t.TotalBytes) })).ToArray()));
        }

        if (caveats.Count > 0)
            blocks.Add(T("Caveats: " + string.Join(" ", caveats)));

        return new AnalyzerDetailSection(
            AnalyzerName: "Dominator Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static new string FormatRatio(ulong retained, ulong shallow)
        => shallow == 0 ? "—" : $"{(double)retained / shallow:F2}x";

    private static new double RatioValue(ulong retained, ulong shallow)
        => shallow == 0 ? 0.0 : (double)retained / shallow;
}

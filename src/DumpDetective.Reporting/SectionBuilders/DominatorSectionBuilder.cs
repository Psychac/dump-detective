using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>A3 — Dominator Analysis. Source: <see cref="DominatorDomainResult"/>.</summary>
internal sealed class DominatorSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Dominator Analysis";
    public string DisplayTitle => "Dominator Analysis";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is DominatorDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DominatorDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.55,
            [
                "Retained bytes are bounded BFS estimates, not true Lengauer-Tarjan dominator tree.",
                d.HeuristicOnly ? "HeuristicOnly flag is set — results may be further imprecise." : string.Empty,
            ]),
        };

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["candidate_count"] = new NumericMetricValue(d.CandidateCount, MetricUnit.Count),
            ["analyzed_count"] = new NumericMetricValue(d.AnalyzedCount, MetricUnit.Count),
            ["total_retained_est"] = new NumericMetricValue((double)Math.Min(d.TotalEstimatedRetainedBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TotalEstimatedRetainedBytes)),
            ["max_bfs_breadth"] = new NumericMetricValue(d.MaxBreadth, MetricUnit.Count),
            ["max_bfs_depth"] = new NumericMetricValue(d.MaxDepth, MetricUnit.Count),
        };

        if (d.TopDominatorTypes.Count > 0)
        {
            blocks.Add(T(d.HeuristicOnly
                ? $"Retained bytes are estimated with a bounded BFS over {d.AnalyzedCount:N0} suspects (breadth cap {d.MaxBreadth:N0}, depth cap {d.MaxDepth:N0})."
                : "Retained bytes are available for the listed suspects."));

            compactTables.Add(STCompact(
                "Top dominator suspects by retained bytes",
                new[] { CH("Type"), CH("Objects","number"), CH("Shallow","bytes"), CH("Retained","bytes"), CH("Ratio"), CH("Avg Size","bytes"), CH("Sample Addr") },
                d.TopDominatorTypes.Take(20).Select(type => R(
                    type.TypeName,
                    type.Count,
                    FormatBytes(type.TotalBytes),
                    type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—",
                    (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000),
                    type.AverageSize > 0 ? FormatBytes(type.AverageSize) : "—",
                    $"0x{type.SampleAddress:X}")).ToArray()));
            if (d.TotalEstimatedRetainedBytes > 0)
            {
                compactTables.Add(STCompact(
                    "Dominator impact per-mille (of total estimated retained)",
                    new[] { CH("Type"), CH("Est. Retained","bytes"), CH("Per-mille") },
                    d.TopDominatorTypes.Take(20).Select(type => R(
                        type.TypeName,
                        type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—",
                        type.EstimatedRetainedBytes == 0 ? "—" : $"{(double)type.EstimatedRetainedBytes * 1000 / d.TotalEstimatedRetainedBytes:F1}‰")).ToArray()));
            }
        }

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

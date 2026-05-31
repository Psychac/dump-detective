using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.55,
            [
                "Retained bytes are bounded BFS estimates, not true Lengauer-Tarjan dominator tree.",
                d.HeuristicOnly ? "HeuristicOnly flag is set — results may be further imprecise." : string.Empty,
            ]),
        };

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Candidate count",       d.CandidateCount.ToString("N0"),                        d.CandidateCount),
            KM("Analyzed count",        d.AnalyzedCount.ToString("N0"),                         d.AnalyzedCount),
            KM("Total retained (est.)", FormatBytes(d.TotalEstimatedRetainedBytes),             (double)Math.Min(d.TotalEstimatedRetainedBytes, long.MaxValue)),
            KM("Max BFS breadth",       d.MaxBreadth.ToString("N0"),                             d.MaxBreadth),
            KM("Max BFS depth",         d.MaxDepth.ToString("N0"),                               d.MaxDepth),
        };

        if (d.TopDominatorTypes.Count > 0)
        {
            blocks.Add(T(d.HeuristicOnly
                ? $"Retained bytes are estimated with a bounded BFS over {d.AnalyzedCount:N0} suspects (breadth cap {d.MaxBreadth:N0}, depth cap {d.MaxDepth:N0})."
                : "Retained bytes are available for the listed suspects."));

            tables.Add(ST(
                "Top dominator suspects by retained bytes",
                ["Type", "Objects", "Shallow", "Retained", "Ratio", "Avg Size", "Sample Addr"],
                d.TopDominatorTypes.Take(20).Select(type => Row(
                    Cell(type.TypeName),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—", (long)Math.Min(type.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(FormatRatio(type.EstimatedRetainedBytes, type.TotalBytes), (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000)),
                    Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : "—"),
                    Cell($"0x{type.SampleAddress:X}"))).ToArray()));

            if (d.TotalEstimatedRetainedBytes > 0)
            {
                tables.Add(ST(
                    "Dominator impact per-mille (of total estimated retained)",
                    ["Type", "Est. Retained", "Per-mille"],
                    d.TopDominatorTypes.Take(20).Select(type => Row(
                        Cell(type.TypeName),
                        Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—", (long)Math.Min(type.EstimatedRetainedBytes, long.MaxValue)),
                        Cell(type.EstimatedRetainedBytes == 0 ? "—" : $"{(double)type.EstimatedRetainedBytes * 1000 / d.TotalEstimatedRetainedBytes:F1}‰"))).ToArray()));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Dominator Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string FormatRatio(ulong retained, ulong shallow)
        => shallow == 0 ? "—" : $"{(double)retained / shallow:F2}x";

    private static double RatioValue(ulong retained, ulong shallow)
        => shallow == 0 ? 0.0 : (double)retained / shallow;
}

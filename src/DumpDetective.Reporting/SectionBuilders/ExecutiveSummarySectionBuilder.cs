using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ExecutiveSummarySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    private const int TopMemoryItems = 5;
    private const int TopRecommendationItems = 3;

    public string SectionId => "prof.executive-summary";
    public string DisplayTitle => "Executive Summary";
    public int SortOrder => 1000;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<MemoryDomainResult>() is not null
        || results.Get<AllocationPatternDomainResult>() is not null
        || results.Get<ThreadDomainResult>() is not null
        || results.AllFindingsSorted().Count > 0;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        MemoryDomainResult? memory = results.Get<MemoryDomainResult>();
        AllocationPatternDomainResult? allocation = results.Get<AllocationPatternDomainResult>();
        ThreadDomainResult? threads = results.Get<ThreadDomainResult>();
        IReadOnlyList<InsightFinding> findings = results.AllFindingsSorted();

        var blocks = new List<SectionBlock>
        {
            H("EXECUTIVE OVERVIEW"),
            T("Cross-analyzer summary of the highest-signal conditions in the dump."),
            M("Total managed memory", memory is null ? "N/A" : FormatBytes(memory.TotalBytes), memory is null ? null : (double)memory.TotalBytes),
            M("Process memory share", "N/A (dump-only)"),
            M("GC pressure", allocation?.GCPressure.ToString() ?? "N/A"),
            M("Blocked threads", threads?.BlockedThreadCount.ToString("N0") ?? "N/A", threads?.BlockedThreadCount),
            M("Thread contention", threads is null ? "N/A" : (threads.BlockedThreadCount > 0 ? "Yes" : "No"), threads is null ? null : (threads.BlockedThreadCount > 0 ? 1.0 : 0.0)),
        };

        blocks.Add(Blank());
        blocks.Add(H("TOP MEMORY CONSUMERS"));
        blocks.Add(T(memory is null
            ? "No memory analyzer result was available."
            : "Top memory consumers are shown by shallow size; retained size is surfaced elsewhere when available."));

        if (memory?.TopTypesBySize is { Count: > 0 } topTypes)
        {
            int limit = Math.Min(topTypes.Count, TopMemoryItems);
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                TypeSnapshot type = topTypes[i];
                rows.Add(Row(
                    Cell(type.TypeName),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.Count.ToString("N0"), type.Count)));
            }

            blocks.Add(new TableBlock(
                Caption: "Top object types by shallow size",
                Headers: ["Type", "Shallow Size", "Count"],
                Rows: rows));

            if (topTypes.Count > TopMemoryItems)
                blocks.Add(T($"Showing top {TopMemoryItems:N0} of {topTypes.Count:N0} object types by shallow size."));
        }
        else
        {
            blocks.Add(T("No top-type memory data was available."));
        }

        blocks.Add(Blank());
        blocks.Add(H("TOP ACTIONABLE RECOMMENDATIONS"));
        blocks.Add(T("Recommendations are taken from the highest-severity findings across all analyzers."));

        IReadOnlyList<InsightFinding> topRecommendations = BuildTopRecommendations(findings);
        if (topRecommendations.Count == 0)
        {
            blocks.Add(T("No Critical or Warning findings were available to summarize."));
        }
        else
        {
            for (int i = 0; i < topRecommendations.Count; i++)
            {
                InsightFinding finding = topRecommendations[i];
                blocks.Add(CollapseBegin($"[{i + 1}] {finding.Severity}: {finding.Title}"));
                blocks.Add(M("Analyzer", finding.Analyzer));
                blocks.Add(M("Confidence", finding.EffectiveConfidenceScore.ToString("F2"), (long)Math.Round(finding.EffectiveConfidenceScore * 100)));
                blocks.Add(T($"Evidence: {finding.Evidence}"));
                blocks.Add(T($"Recommendation: {finding.Recommendation}"));
                if (finding.EffectiveCaveats.Count > 0)
                    blocks.Add(T($"Caveats: {string.Join(" ", finding.EffectiveCaveats)}"));
                blocks.Add(CollapseEnd());

                if (i + 1 < topRecommendations.Count)
                    blocks.Add(Blank());
            }
        }

        blocks.Add(Blank());
        blocks.Add(H("LEAK LIKELIHOOD"));
        InsightFinding? leakSignal = FindTopLeakSignal(findings);
        blocks.Add(leakSignal is null
            ? T("No explicit leak signal was produced by the current analyzer set.")
            : T($"{leakSignal.Title} — {leakSignal.Evidence}"));

        return new AnalyzerDetailSection(
            AnalyzerName: "Executive Summary",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static IReadOnlyList<InsightFinding> BuildTopRecommendations(IReadOnlyList<InsightFinding> findings)
    {
        var recommendations = new List<InsightFinding>(TopRecommendationItems);

        for (int i = 0; i < findings.Count; i++)
        {
            InsightFinding finding = findings[i];
            if (finding.Severity is not (FindingSeverity.Critical or FindingSeverity.Warning))
                continue;

            recommendations.Add(finding);
            if (recommendations.Count == TopRecommendationItems)
                break;
        }

        return recommendations;
    }

    private static InsightFinding? FindTopLeakSignal(IReadOnlyList<InsightFinding> findings)
    {
        for (int i = 0; i < findings.Count; i++)
        {
            InsightFinding finding = findings[i];
            if (finding.Category.Contains("Leak", StringComparison.OrdinalIgnoreCase)
                || finding.Category.Contains("Retention", StringComparison.OrdinalIgnoreCase)
                || finding.Title.Contains("leak", StringComparison.OrdinalIgnoreCase))
            {
                return finding;
            }
        }

        return null;
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;

        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        GCGenerationDomainResult? gcGen = results.Get<GCGenerationDomainResult>();
        LeakCandidateDomainResult? leakCandidates = results.Get<LeakCandidateDomainResult>();
        LockGraphDomainResult? lockGraph = results.Get<LockGraphDomainResult>();
        CrashDomainResult? crash = results.Get<CrashDomainResult>();
        FinalizableObjectDomainResult? finalizable = results.Get<FinalizableObjectDomainResult>();
        HangDomainResult? hang = results.Get<HangDomainResult>();
        IReadOnlyList<InsightFinding> findings = results.AllFindingsSorted();
        IReadOnlyList<InsightFinding> criticalFindings = BuildFindingsBySeverity(findings, FindingSeverity.Critical, 5);
        IReadOnlyList<InsightFinding> warningFindings = BuildFindingsBySeverity(findings, FindingSeverity.Warning, 5);

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>();
        if (memory is not null)
        {
            keyMetrics["total_managed_memory"] = new NumericMetricValue((double)memory.TotalBytes, MetricUnit.Bytes, FormatBytes(memory.TotalBytes));
            keyMetrics["loh_pct"] = new NumericMetricValue(memory.LohPercent, MetricUnit.Percent, memory.LohPercent.ToString("F1") + "%");
        }
        if (gcGen is not null)
            keyMetrics["gen2_pct"] = new NumericMetricValue(gcGen.Gen2Pct, MetricUnit.Percent, gcGen.Gen2Pct.ToString("F1") + "%");
        if (allocation is not null)
            keyMetrics["gc_pressure"] = new TextMetricValue(allocation.GCPressure.ToString());
        if (leakCandidates is not null)
            keyMetrics["leak_candidates"] = new NumericMetricValue(leakCandidates.TotalCandidates, MetricUnit.Count, leakCandidates.TotalCandidates.ToString("N0"));
        if (hang is not null)
            keyMetrics["hang_score"] = new NumericMetricValue(hang.HealthScore, MetricUnit.Count, hang.HealthScore.ToString("N0"));
        if (threads is not null)
            keyMetrics["blocked_threads"] = new NumericMetricValue(threads.BlockedThreadCount, MetricUnit.Count, threads.BlockedThreadCount.ToString("N0"));
        if (lockGraph is not null)
            keyMetrics["deadlock_cycles"] = new NumericMetricValue(lockGraph.DeadlockCandidateCount, MetricUnit.Count, lockGraph.DeadlockCandidateCount.ToString("N0"));
        if (crash is not null)
            keyMetrics["active_exceptions"] = new NumericMetricValue(crash.ActiveExceptions, MetricUnit.Count, crash.ActiveExceptions.ToString("N0"));
        if (finalizable is not null)
            keyMetrics["finalizer_queue"] = new NumericMetricValue(finalizable.FinalizerQueueCount, MetricUnit.Count, finalizable.FinalizerQueueCount.ToString("N0"));

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            T("Cross-analyzer summary of the highest-signal conditions in the dump."),
        };

        blocks.Add(T(memory is null
            ? "No memory analyzer result was available."
            : "Top memory consumers are shown by shallow size; retained size is surfaced elsewhere when available."));

        if (memory?.TopTypes is { Count: > 0 } topTypes)
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

            compactTables.Add(STCompact("Top object types by shallow size", new[] { CH("Type"), CH("Shallow Size","bytes"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            if (topTypes.Count > TopMemoryItems)
                blocks.Add(T($"Showing top {TopMemoryItems:N0} of {topTypes.Count:N0} object types by shallow size."));
        }
        else
        {
            blocks.Add(T("No top-type memory data was available."));
        }

        blocks.Add(Blank());
        blocks.Add(H("CRITICAL FINDINGS"));
        if (criticalFindings.Count == 0)
        {
            blocks.Add(T("No Critical findings were available to summarize."));
        }
        else
        {
            for (int i = 0; i < criticalFindings.Count; i++)
            {
                InsightFinding finding = criticalFindings[i];
                blocks.Add(CollapseBegin($"[{i + 1}] {finding.Title}"));
                blocks.Add(M("Analyzer", finding.Analyzer));
                blocks.Add(M("Confidence", finding.EffectiveConfidenceScore.ToString("F2"), (long)Math.Round(finding.EffectiveConfidenceScore * 100)));
                blocks.Add(T($"Details: {finding.Evidence}"));
                blocks.Add(T($"Recommendation: {finding.Recommendation}"));
                blocks.Add(CollapseEnd());

                if (i + 1 < criticalFindings.Count)
                    blocks.Add(Blank());
            }
        }

        blocks.Add(Blank());
        blocks.Add(H("WARNING FINDINGS"));
        if (warningFindings.Count == 0)
        {
            blocks.Add(T("No Warning findings were available to summarize."));
        }
        else
        {
            for (int i = 0; i < warningFindings.Count; i++)
            {
                InsightFinding finding = warningFindings[i];
                blocks.Add(CollapseBegin($"[{i + 1}] {finding.Title}"));
                blocks.Add(M("Analyzer", finding.Analyzer));
                blocks.Add(M("Confidence", finding.EffectiveConfidenceScore.ToString("F2"), (long)Math.Round(finding.EffectiveConfidenceScore * 100)));
                blocks.Add(T($"Details: {finding.Evidence}"));
                blocks.Add(T($"Recommendation: {finding.Recommendation}"));
                blocks.Add(CollapseEnd());

                if (i + 1 < warningFindings.Count)
                    blocks.Add(Blank());
            }
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
                blocks.Add(T($"Details: {finding.Evidence}"));
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
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
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

    private static IReadOnlyList<InsightFinding> BuildFindingsBySeverity(IReadOnlyList<InsightFinding> findings, FindingSeverity severity, int limit)
    {
        var selected = new List<InsightFinding>(limit);
        for (int i = 0; i < findings.Count; i++)
        {
            InsightFinding finding = findings[i];
            if (finding.Severity != severity)
                continue;

            selected.Add(finding);
            if (selected.Count == limit)
                break;
        }

        return selected;
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
        string[] units = new[] { "B", "KB", "MB", "GB", "TB" };
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
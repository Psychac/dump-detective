using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ExecutiveSummarySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    private const int TopMemoryItems = 5;
    private const int TopRecommendationItems = 3;

    public IReadOnlyList<string> SourceAnalyzers => ["MemoryAnalyzer", "GCGenerationAnalyzer", "AllocationPatternAnalyzer", "LeakCandidateAnalyzer", "HangAnalyzer", "ThreadAnalyzer", "LockGraphAnalyzer", "CrashAnalyzer", "FinalizableObjectAnalyzer"];

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

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total managed memory", memory is null ? "N/A" : FormatBytes(memory.TotalBytes),         memory is null ? null : (double?)memory.TotalBytes),
            KM("LOH %",                memory is null ? "N/A" : memory.LohPercent.ToString("F1") + "%", memory is null ? null : (double?)memory.LohPercent),
            KM("Gen2 %",               gcGen is null  ? "N/A" : gcGen.Gen2Pct.ToString("F1") + "%",    gcGen is null  ? null : (double?)gcGen.Gen2Pct),
            KM("GC pressure",          allocation?.GCPressure.ToString() ?? "N/A",                      allocation is null ? null : (double?)allocation.GCPressure),
            KM("Leak candidates",      leakCandidates?.TotalCandidates.ToString("N0") ?? "N/A",         leakCandidates is null ? null : (double?)leakCandidates.TotalCandidates),
            KM("Hang score",           hang?.HealthScore.ToString("N0") ?? "N/A",                       hang is null ? null : (double?)hang.HealthScore),
            KM("Blocked threads",      threads?.BlockedThreadCount.ToString("N0") ?? "N/A",             threads is null ? null : (double?)threads.BlockedThreadCount),
            KM("Deadlock cycles",      lockGraph?.DeadlockCandidateCount.ToString("N0") ?? "N/A",       lockGraph is null ? null : (double?)lockGraph.DeadlockCandidateCount),
            KM("Active exceptions",    crash?.ActiveExceptions.ToString("N0") ?? "N/A",                 crash is null ? null : (double?)crash.ActiveExceptions),
            KM("Finalizer queue",      finalizable?.FinalizerQueueCount.ToString("N0") ?? "N/A",        finalizable is null ? null : (double?)finalizable.FinalizerQueueCount),
        };

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            T("Cross-analyzer summary of the highest-signal conditions in the dump."),
        };

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

            tables.Add(ST("Top object types by shallow size", ["Type", "Shallow Size", "Count"], rows));

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
                blocks.Add(T($"Evidence: {finding.Evidence}"));
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
                blocks.Add(T($"Evidence: {finding.Evidence}"));
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
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
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
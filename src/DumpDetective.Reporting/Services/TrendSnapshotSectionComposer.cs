using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal static class TrendSnapshotSectionComposer
{
    public static AnalyzerDetailSection Build(
        string dumpPath,
        DateTime generatedAtUtc,
        IReadOnlyList<FindingRecord> findings,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext,
        IReadOnlyList<AnalyzerDetailSection> sections,
        int dumpIndex,
        int totalDumps)
    {
        string title = $"Dump {dumpIndex + 1} of {totalDumps}: {Path.GetFileName(dumpPath)}";
        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("DUMP SUMMARY"));
        blocks.Add(new DividerBlock());
        blocks.Add(new PathBlock("Path", dumpPath));
        blocks.Add(new MetricBlock("Generated (UTC)", generatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")));
        blocks.Add(new MetricBlock("Findings", findings.Count.ToString()));

        if (incidentContext is { } ctx)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("INCIDENT CONTEXT"));
            blocks.Add(new DividerBlock());
            blocks.Add(new MetricBlock("Mode", ctx.Mode));
            blocks.Add(new MetricBlock("Report", $"{ctx.ReportFormat} / {ctx.ReportAudience}"));
            blocks.Add(new MetricBlock("Runtime", $"{ctx.RuntimeFlavor ?? "n/a"}{(string.IsNullOrWhiteSpace(ctx.RuntimeVersion) ? string.Empty : " " + ctx.RuntimeVersion)}"));
            blocks.Add(new MetricBlock("GC Mode", ctx.GcMode ?? "n/a"));
            blocks.Add(new MetricBlock("Heap Count", ctx.HeapCount.HasValue ? ctx.HeapCount.Value.ToString() : "n/a"));
            blocks.Add(new MetricBlock("Heap Walkable", ctx.HeapCanWalk ? "yes" : "no"));
            blocks.Add(new MetricBlock("Config", (ctx.UsedConfigFile ? "config file" : "command line") + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({ctx.ConfigPath})")));
            blocks.Add(new MetricBlock("Index Prebuild", ctx.IndexPrebuildMode));
            blocks.Add(new MetricBlock("Active Analyzers", ctx.ActiveAnalyzerCount.ToString()));
            blocks.Add(new MetricBlock("Elapsed", $"{ctx.AnalysisElapsedSeconds:F1}s"));
        }

        if (findings.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("FINDINGS"));
            blocks.Add(new DividerBlock());
            foreach (FindingRecord finding in findings)
            {
                blocks.Add(new HeadingBlock($"[{finding.Severity}] {finding.Title}", 1));
                blocks.Add(new TextBlock(finding.Evidence, 2));
            }
        }

        foreach (AnalyzerDetailSection section in sections)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new CollapsibleSectionBeginBlock(section.DisplayTitle));
            foreach (SectionBlock block in section.Blocks)
                blocks.Add(block);
            blocks.Add(new CollapsibleSectionEndBlock());
        }

        return new AnalyzerDetailSection(title, title, dumpIndex * 10 + 200, blocks);
    }
}
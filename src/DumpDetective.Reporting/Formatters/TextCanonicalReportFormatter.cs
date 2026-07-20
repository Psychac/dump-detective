using System.Text;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

internal sealed class TextCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Text;

    public string Render(AnalysisReportDocument doc)
    {
        bool isTrend = doc is TrendReportDocument;
        string title = isTrend ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = isTrend ? "Latest dump" : "Dump";
        string dumpPath = ReportFormatterHelpers.GetCanonicalDumpPath(doc);

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(StringConstants.Equals80);
        sb.AppendLine($"{dumpLabel}: {dumpPath}");
        sb.AppendLine($"Generated (UTC): {doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Elapsed: {doc.ElapsedSeconds:F1}s");
        sb.AppendLine($"Schema: {doc.SchemaVersion}");
        sb.AppendLine();
        // dedup diagnostics removed from report document

        if (doc is TrendReportDocument trendDoc)
        {
            sb.AppendLine($"Dumps analyzed: {trendDoc.TrendDumpCount}");
            if (trendDoc.TrendDumpPaths is { Count: > 0 })
            {
                sb.AppendLine("Analyzed dumps:");
                foreach (string path in trendDoc.TrendDumpPaths)
                    sb.AppendLine($"  - {path}");
            }
            // T2.2: Lifecycle summary
            sb.AppendLine($"Finding lifecycle:  New={trendDoc.TrendNewFindingCount}  Persistent={trendDoc.TrendPersistentFindingCount}  Resolved={trendDoc.TrendResolvedFindingCount}");
            sb.AppendLine();
        }
        if (doc.HealthScorecard is { } scorecard)
        {
            RenderHealthScorecard(scorecard, sb);
        }

        if (doc.ExecutiveSummary is { } executiveSummary)
        {
            RenderExecutiveSummaryText(executiveSummary, sb);
        }

        if (doc.IncidentContext is { } ctx)
        {
            sb.AppendLine("INCIDENT CONTEXT");
            sb.AppendLine(StringConstants.Equals80);
            sb.AppendLine($"- Mode: {ctx.Mode}");
            sb.AppendLine($"- Dump Path: {ctx.DumpPath}");
            if (!string.IsNullOrWhiteSpace(ctx.BaselineDumpPath)) sb.AppendLine($"- Baseline Dump: {ctx.BaselineDumpPath}");
            sb.AppendLine($"- Report: {ctx.ReportFormat}");
            sb.AppendLine($"- Config: {(ctx.UsedConfigFile ? "config file" : "command line")}" + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({ctx.ConfigPath})"));
            sb.AppendLine($"- Diagnostic Mode: {(ctx.DiagnosticMode ? "on" : "off")}");
            sb.AppendLine($"- Runtime: {ctx.RuntimeFlavor ?? "n/a"}" + (string.IsNullOrWhiteSpace(ctx.RuntimeVersion) ? string.Empty : $" {ctx.RuntimeVersion}"));
            sb.AppendLine($"- GC Mode: {ctx.GcMode ?? "n/a"}");
            sb.AppendLine($"- Heap Count: {(ctx.HeapCount.HasValue ? ctx.HeapCount.Value.ToString() : "n/a")}");
            sb.AppendLine($"- Heap Walkable: {(ctx.HeapCanWalk ? "yes" : "no")}");
            sb.AppendLine($"- Active Analyzers: {ctx.ActiveAnalyzerCount}");
            sb.AppendLine($"- Analysis Elapsed: {ctx.AnalysisElapsedSeconds:F1}s");
            if (ctx.TrendSnapshots is { Count: > 0 })
            {
                sb.AppendLine("- Snapshot Contexts:");
                foreach (var snap in ctx.TrendSnapshots)
                {
                    string role = snap.IsBaseline ? "baseline" : snap.IsCurrent ? "current" : $"snapshot {snap.Index + 1}";
                    sb.AppendLine($"  - {role}: {snap.DumpPath} | {snap.ElapsedSeconds:F1}s | analyzers {snap.AnalyzerCount} | findings {snap.FindingCount}");
                }
            }
            sb.AppendLine();
        }

        if (doc.Domains is null && doc.AnalyzerSections.Count > 0)
        {
            sb.AppendLine("DETAILED ANALYZER SECTIONS");
            sb.AppendLine(StringConstants.Equals80);
            foreach (AnalyzerDetailSection section in doc.AnalyzerSections)
            {
                sb.AppendLine($"[{section.DisplayTitle}]");
                sb.AppendLine();
                RenderBlocksText(section.Blocks, sb);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void RenderBlocksText(IReadOnlyList<SectionBlock> blocks, StringBuilder sb)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            SectionBlock block = blocks[i];
            switch (block)
            {
                case HeadingBlock h:
                    sb.AppendLine($"{Indent(h.IndentLevel)}{h.Text}");
                    break;
                case MetricBlock m:
                    sb.AppendLine($"{Indent(m.IndentLevel)}{m.Label}: {m.Value}");
                    break;
                case PathBlock p:
                    sb.AppendLine($"{Indent(p.IndentLevel)}{p.Label}: {p.Path}");
                    break;
                case StackFrameBlock sf:
                    sb.AppendLine($"{Indent(sf.IndentLevel)} at {sf.Frame}");
                    break;
                case TextBlock t:
                    sb.AppendLine($"{Indent(t.IndentLevel)}{t.Text}");
                    break;
                case ListItemBlock l:
                    sb.AppendLine($"{Indent(l.IndentLevel)}- {l.Text}");
                    break;
                case DividerBlock:
                    sb.AppendLine(StringConstants.Separator80);
                    break;
                case BlankBlock:
                    sb.AppendLine();
                    break;
                case TableBlock tbl:
                    RenderTableText(tbl, sb);
                    break;
                case ChartBlock chart:
                    sb.AppendLine($"{Indent(0)}[Chart] {chart.Title} ({chart.Kind})");
                    sb.AppendLine();
                    break;
                case ConfidenceBandBlock band:
                    sb.AppendLine($"{Indent(0)}> {band.Symbol} {band.Band} confidence{(band.Caveats.Length > 0 ? $" — {string.Join("; ", band.Caveats)}" : string.Empty)}");
                    sb.AppendLine();
                    break;
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"[{cs.Title}]");
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine();
                    break;
                case SparklineBlock spark:
                    {
                        double first = spark.Values.FirstOrDefault(v => !double.IsNaN(v));
                        double last  = spark.Values.LastOrDefault(v => !double.IsNaN(v));
                        int n = spark.Values.Count;
                        string label = n <= 2
                            ? $"{first} → {last}"
                            : $"{first} → [{n} pts] → {last}";
                        sb.AppendLine($"[Sparkline] {spark.MetricKey} ({spark.Unit}): {label}");
                        break;
                    }
            }
        }
    }

    private static void RenderTableText(TableBlock tbl, StringBuilder sb)
    {
        if (tbl.Caption is not null)
            sb.AppendLine($"  {tbl.Caption}");

        int cols = tbl.Headers.Count;
        int[] widths = new int[cols];
        for (int c = 0; c < cols; c++)
            widths[c] = tbl.Headers[c].Length;
        foreach (TableRow row in tbl.Rows)
            for (int c = 0; c < Math.Min(row.Cells.Count, cols); c++)
                widths[c] = Math.Max(widths[c], Math.Min(row.Cells[c].Display.Length, 60));

        var hdr = new StringBuilder("  ");
        for (int c = 0; c < cols; c++) { hdr.Append(tbl.Headers[c].PadRight(widths[c])); if (c < cols - 1) hdr.Append("  "); }
        sb.AppendLine(hdr.ToString());

        var sep = new StringBuilder("  ");
        for (int c = 0; c < cols; c++) { sep.Append(new string('-', widths[c])); if (c < cols - 1) sep.Append("  "); }
        sb.AppendLine(sep.ToString());

        foreach (TableRow row in tbl.Rows)
        {
            var rowSb = new StringBuilder("  ");
            for (int c = 0; c < cols; c++)
            {
                string cell = c < row.Cells.Count ? row.Cells[c].Display : string.Empty;
                if (cell.Length > 60) cell = cell[..57] + "...";
                rowSb.Append(cell.PadRight(widths[c]));
                if (c < cols - 1) rowSb.Append("  ");
            }
            sb.AppendLine(rowSb.ToString());
        }
        sb.AppendLine();
    }

    private static void RenderHealthScorecard(HealthScorecard scorecard, StringBuilder sb)
    {
        sb.AppendLine("HEALTH SUMMARY");
        sb.AppendLine(StringConstants.Equals80);
        bool hasTrend   = scorecard.Domains.Values.Any(d => d.Change.HasValue);
        bool hasHistory = hasTrend && scorecard.Domains.Values.Any(d => d.SeverityHistory is { Count: > 2 });
        if (hasTrend)
        {
            sb.AppendLine("Domain                Baseline    Current     Change       Critical  Warning");
            sb.AppendLine("--------------------   --------    -------     ------       --------  -------");
            foreach (var entry in scorecard.Domains.Values)
            {
                string bas = entry.BaselineSeverity?.ToString() ?? "—";
                string cur = entry.Severity.ToString();
                string chg = entry.Change switch
                {
                    DomainSeverityChange.Regressed => "Regressed",
                    DomainSeverityChange.Improved  => "Improved",
                    DomainSeverityChange.NewDomain => "New",
                    DomainSeverityChange.Removed   => "Removed",
                    _                              => "Stable"
                };
                sb.AppendLine($"{entry.Domain,-21} {bas,-10} {cur,-10} {chg,-12} {entry.CriticalCount,8} {entry.WarningCount,8}");
                if (hasHistory && entry.SeverityHistory is { Count: > 2 })
                {
                    string progression = string.Join(" → ", entry.SeverityHistory.Select((s, i) =>
                    {
                        string label = i == 0 ? "base" : i == entry.SeverityHistory.Count - 1 ? "cur" : $"#{i + 1}";
                        return $"{s}({label})";
                    }));
                    sb.AppendLine($"  Progression: {progression}");
                }
            }
        }
        else
        {
            sb.AppendLine("Domain                Severity    Critical  Warning");
            sb.AppendLine("--------------------   --------    --------  -------");
            foreach (var entry in scorecard.Domains.Values)
                sb.AppendLine($"{entry.Domain,-21} {entry.Severity,-10} {entry.CriticalCount,8} {entry.WarningCount,8}");
        }
        sb.AppendLine($"Overall severity: {scorecard.OverallSeverity}");
        sb.AppendLine();
    }

    private static void RenderExecutiveSummaryText(ExecutiveSummaryRecord summary, StringBuilder sb)
    {
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine(StringConstants.Equals80);
        sb.AppendLine($"- Total managed bytes: {summary.TotalManagedBytes:N0}");
        sb.AppendLine($"- Leak likelihood score: {summary.LeakLikelihoodScore}");
        sb.AppendLine($"- GC pressure score: {summary.GcPressureScore}");
        sb.AppendLine($"- Thread contention score: {summary.ThreadContentionScore}");

        if (summary.TopActions is { Count: > 0 })
        {
            sb.AppendLine("Action queue:");
            for (int i = 0; i < summary.TopActions.Count && i < 10; i++)
            {
                RankedActionRecord action = summary.TopActions[i];
                sb.AppendLine($"  {i + 1}. {action.Title}: {action.Action}");
            }
        }

        sb.AppendLine();
    }

    private static string Indent(int level) => level switch { 1 => "  ", 2 => "    ", >= 3 => "      ", _ => string.Empty };
}

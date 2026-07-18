using System.Text;
using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

namespace DumpDetective.Reporting.Formatters;

internal interface IReportFormatter
{
    ReportFormat Format { get; }
    string Render(AnalysisReportDocument doc);
}

internal static class ReportFormatterHelpers
{
    public static string GetCanonicalDumpPath(AnalysisReportDocument doc)
    {
        if (doc is SingleDumpReportDocument single)
            return single.DumpPath ?? string.Empty;

        if (doc is TrendReportDocument trend)
        {
            if (trend.TrendDumpPaths is { Count: > 0 })
                return trend.TrendDumpPaths[^1] ?? string.Empty;
            if (trend.PerDumpDocuments is { Count: > 0 })
            {
                var last = trend.PerDumpDocuments[^1];
                if (last is SingleDumpReportDocument sd)
                    return sd.DumpPath ?? string.Empty;
            }
            if (trend.IncidentContext is { } ctx && !string.IsNullOrEmpty(ctx.DumpPath))
                return ctx.DumpPath;
            return string.Empty;
        }

        return string.Empty;
    }
}

// ── Text ──────────────────────────────────────────────────────────────────────

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

    // ── P1.2: Explainable score rendering ─────────────────────────────────────

    private static void AppendScore(StringBuilder sb, string label, int score, int? delta, ScoreBreakdown? breakdown)
    {
        string deltaStr = delta.HasValue
            ? $" ({(delta.Value >= 0 ? "+" : string.Empty)}{delta.Value} vs baseline)"
            : string.Empty;
        string confidenceStr = breakdown is not null
            ? $" [confidence: {breakdown.Confidence:P0}]"
            : string.Empty;

        sb.AppendLine($"- {label}: {score}/100{deltaStr}{confidenceStr}");

        if (breakdown?.Contributors is { Count: > 0 } contributors)
        {
            for (int i = 0; i < contributors.Count; i++)
            {
                ScoreContributor c = contributors[i];
                string detail = string.IsNullOrEmpty(c.Detail) ? string.Empty : $" ({c.Detail})";
                sb.AppendLine($"    +{c.Points,2} pts  [{c.Source}] {c.Label}{detail}");
            }
        }
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
                    sb.AppendLine($"{Indent(sf.IndentLevel)}   at {sf.Frame}");
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

// ── Markdown ──────────────────────────────────────────────────────────────────

internal sealed class MarkdownCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Markdown;

    public string Render(AnalysisReportDocument doc)
    {
        bool isTrend = doc is TrendReportDocument;
        string title = isTrend ? "# DumpDetective Trend Analysis Report" : "# DumpDetective Analysis Report";
        string dumpLabel = isTrend ? "Latest dump" : "Dump";
        string dumpPath = ReportFormatterHelpers.GetCanonicalDumpPath(doc);

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine($"> {dumpLabel}: `{dumpPath}`  ");
        sb.AppendLine($"> Generated (UTC): `{doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}`  ");
        sb.AppendLine($"> Elapsed: `{doc.ElapsedSeconds:F1}s`");
        sb.AppendLine($"> Schema: `{doc.SchemaVersion}`");
        sb.AppendLine();
        // Dedup merged summary removed — no longer useful

        // Table Of Contents (Markdown)
        if (doc.Domains is null && doc.AnalyzerSections.Count > 0)
        {
            sb.AppendLine("## Table of Contents");
            sb.AppendLine();
            if (doc.AnalyzerSections.Count > 0)
            {
                sb.AppendLine("### Analyzer Sections");
                sb.AppendLine();
                for (int si = 0; si < doc.AnalyzerSections.Count; si++)
                {
                    var s = doc.AnalyzerSections[si];
                    sb.AppendLine($"- [{Esc(s.DisplayTitle)}](#detail-{si})");
                }
                sb.AppendLine();
            }
        }

        if (doc is TrendReportDocument trendDoc)
        {
            sb.AppendLine($"> Dumps analyzed: **{trendDoc.TrendDumpCount}**");
            if (trendDoc.TrendDumpPaths is { Count: > 0 })
            {
                sb.AppendLine("> Analyzed dumps:");
                foreach (string path in trendDoc.TrendDumpPaths)
                    sb.AppendLine($"> - `{path}`");
            }
            // T2.2: Lifecycle summary
            sb.AppendLine($"> Finding lifecycle: **New={trendDoc.TrendNewFindingCount}** / Persistent={trendDoc.TrendPersistentFindingCount} / Resolved={trendDoc.TrendResolvedFindingCount}");
            sb.AppendLine();
        }

        if (doc.HealthScorecard is { } scorecard)
        {
            RenderHealthScorecard(scorecard, sb);
        }

        if (doc.ExecutiveSummary is { } executiveSummary)
        {
            RenderExecutiveSummaryMarkdown(
                executiveSummary,
                doc.CorrelationEvents,
                sb);
        }

        if (doc.IncidentContext is { } ctx)
        {
            sb.AppendLine("## Incident Context");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| **Mode** | {Esc(ctx.Mode)} |");
            sb.AppendLine($"| **Dump Path** | `{ctx.DumpPath}` |");
            if (!string.IsNullOrWhiteSpace(ctx.BaselineDumpPath)) sb.AppendLine($"| **Baseline Dump** | `{ctx.BaselineDumpPath}` |");
            sb.AppendLine($"| **Report** | {Esc(ctx.ReportFormat)} |");
            sb.AppendLine($"| **Config** | {(ctx.UsedConfigFile ? "config file" : "command line") + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({Esc(ctx.ConfigPath)})")} |");
            sb.AppendLine($"| **Diagnostic Mode** | {(ctx.DiagnosticMode ? "on" : "off")} |");
            sb.AppendLine($"| **Runtime** | {Esc(ctx.RuntimeFlavor ?? "n/a")}{(string.IsNullOrWhiteSpace(ctx.RuntimeVersion) ? string.Empty : " " + Esc(ctx.RuntimeVersion))} |");
            sb.AppendLine($"| **GC Mode** | {Esc(ctx.GcMode ?? "n/a")} |");
            sb.AppendLine($"| **Heap Count** | {(ctx.HeapCount.HasValue ? ctx.HeapCount.Value.ToString() : "n/a")} |");
            sb.AppendLine($"| **Heap Walkable** | {(ctx.HeapCanWalk ? "yes" : "no")} |");
            sb.AppendLine($"| **Active Analyzers** | {ctx.ActiveAnalyzerCount} |");
            sb.AppendLine($"| **Analysis Elapsed** | {ctx.AnalysisElapsedSeconds:F1}s |");
            if (ctx.TrendSnapshots is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("### Snapshot Contexts");
                sb.AppendLine();
                sb.AppendLine("| Snapshot | Dump | Elapsed | Analyzers | Findings |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var snap in ctx.TrendSnapshots)
                {
                    string role = snap.IsBaseline ? "Baseline" : snap.IsCurrent ? "Current" : $"Snapshot {snap.Index + 1}";
                    sb.AppendLine($"| {role} | `{snap.DumpPath}` | {snap.ElapsedSeconds:F1}s | {snap.AnalyzerCount} | {snap.FindingCount} |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (doc.Domains is null && doc.AnalyzerSections.Count > 0)
        {
            sb.AppendLine("## Detailed Analyzer Sections");
            sb.AppendLine();
            for (int si = 0; si < doc.AnalyzerSections.Count; si++)
            {
                AnalyzerDetailSection section = doc.AnalyzerSections[si];
                string anchor = string.IsNullOrEmpty(section.SectionId) ? $"detail-{si}" : section.SectionId;
                sb.AppendLine($"<a id=\"{anchor}\"></a>");
                sb.AppendLine($"### {Esc(section.DisplayTitle)}");
                sb.AppendLine();
                RenderBlocksMd(section.Blocks, sb);
                RenderSectionTablesMd(section.CompactTables, sb);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void RenderHealthScorecard(HealthScorecard scorecard, StringBuilder sb)
    {
        sb.AppendLine("## Health Summary");
        sb.AppendLine();
        bool hasTrend   = scorecard.Domains.Values.Any(d => d.Change.HasValue);
        bool hasHistory = hasTrend && scorecard.Domains.Values.Any(d => d.SeverityHistory is { Count: > 2 });
        if (hasTrend)
        {
            if (hasHistory)
            {
                sb.AppendLine("| Domain | Baseline | Progression | Current | Change | Critical | Warning |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
            }
            else
            {
                sb.AppendLine("| Domain | Baseline | Current | Change | Critical | Warning |");
                sb.AppendLine("|---|---|---|---|---|---|");
            }
            foreach (var entry in scorecard.Domains.Values)
            {
                string cur = entry.Severity.ToString();
                string bas = entry.BaselineSeverity?.ToString() ?? "—";
                string chg = entry.Change switch
                {
                    DomainSeverityChange.Regressed => "⬆ Regressed",
                    DomainSeverityChange.Improved  => "⬇ Improved",
                    DomainSeverityChange.NewDomain => "🆕 New",
                    DomainSeverityChange.Removed   => "🗑 Removed",
                    _                              => "= Stable"
                };
                if (hasHistory)
                {
                    // Render intermediates only (skip index 0 = baseline, skip last = current; they have their own columns)
                    string progression = entry.SeverityHistory is { Count: > 2 }
                        ? string.Join(" → ", entry.SeverityHistory.Skip(1).SkipLast(1).Select((s, i) => $"{s}(#{i + 2})"))
                        : "—";
                    sb.AppendLine($"| {Esc(entry.Domain)} | {bas} | {progression} | {cur} | {chg} | {entry.CriticalCount} | {entry.WarningCount} |");
                }
                else
                {
                    sb.AppendLine($"| {Esc(entry.Domain)} | {bas} | {cur} | {chg} | {entry.CriticalCount} | {entry.WarningCount} |");
                }
            }
        }
        else
        {
            sb.AppendLine("| Domain | Severity | Critical | Warning |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var entry in scorecard.Domains.Values)
                sb.AppendLine($"| {Esc(entry.Domain)} | {entry.Severity} | {entry.CriticalCount} | {entry.WarningCount} |");
        }
        sb.AppendLine();
    }

    private static void RenderExecutiveSummaryMarkdown(
        ExecutiveSummaryRecord summary,
        IReadOnlyList<CorrelationEventRecord>? correlationEvents,
        StringBuilder sb)
    {
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine($"- Total managed bytes: {summary.TotalManagedBytes:N0}");
        sb.AppendLine($"- Leak likelihood score: {summary.LeakLikelihoodScore}");
        sb.AppendLine($"- GC pressure score: {summary.GcPressureScore}");
        sb.AppendLine($"- Thread contention score: {summary.ThreadContentionScore}");
 

        if (summary.TopActions is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Action Queue");
            if (!string.IsNullOrWhiteSpace(summary.ActionScoringModelVersion))
                sb.AppendLine($"> Scoring model: `{Esc(summary.ActionScoringModelVersion!)}`");

            for (int i = 0; i < summary.TopActions.Count && i < 10; i++)
            {
                RankedActionRecord action = summary.TopActions[i];
                sb.AppendLine($"{i + 1}. **{Esc(action.Title)}**");
                sb.AppendLine($"   - Action: {Esc(action.Action)}");
                sb.AppendLine($"   - Why now: {Esc(action.WhyNow)}");

                if (action.Confidence is { } confidence)
                {
                    sb.AppendLine($"   - Confidence: {confidence.Composite:0.00} (evidence {confidence.EvidenceCompleteness:0.00}, consistency {confidence.CrossAnalyzerConsistency:0.00}, penalty {confidence.HeuristicPenalty:0.00})");
                    if (confidence.Caveats is { Count: > 0 })
                        sb.AppendLine($"   - Caveats: {Esc(string.Join("; ", confidence.Caveats))}");
                }

                if (!string.IsNullOrWhiteSpace(action.Validation))
                    sb.AppendLine($"   - Validation: {Esc(action.Validation!)}");
            }
        }

        if (correlationEvents is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Cross-Domain Correlation Signals");

            for (int i = 0; i < correlationEvents.Count && i < 6; i++)
            {
                CorrelationEventRecord evt = correlationEvents[i];
                sb.AppendLine($"- **{Esc(evt.Title)}** ({Esc(evt.EventType)}, {evt.Confidence:0.00})");
                sb.AppendLine($"  - Rationale: {Esc(evt.Rationale)}");
                sb.AppendLine($"  - Domains: {Esc(string.Join(", ", evt.Domains))}");
                sb.AppendLine($"  - Signals: {Esc(string.Join(", ", evt.SignalKeys))}");
            }
        }

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

    private static void RenderHealthScorecardHtml(HealthScorecard scorecard, StringBuilder sb)
    {
        sb.Append(ReportHtmlShared.RenderHealthScorecard(scorecard));
    }

    private static void RenderBlocksMd(IReadOnlyList<SectionBlock> blocks, StringBuilder sb)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            SectionBlock block = blocks[i];
            switch (block)
            {
                case HeadingBlock h:
                    sb.AppendLine($"{new string('#', h.IndentLevel + 4)} {h.Text}");
                    break;
                case MetricBlock m:
                    sb.AppendLine($"**{m.Label}**: {m.Value}");
                    sb.AppendLine();
                    break;
                case PathBlock p:
                    sb.AppendLine($"**{p.Label}**: `{p.Path}`");
                    sb.AppendLine();
                    break;
                case StackFrameBlock sf:
                    sb.AppendLine($"- `{sf.Frame}`");
                    break;
                case TextBlock t:
                    sb.AppendLine(t.Text);
                    sb.AppendLine();
                    break;
                case ListItemBlock l:
                    sb.AppendLine($"- {l.Text}");
                    break;
                case DividerBlock:
                    sb.AppendLine("---");
                    break;
                case BlankBlock:
                    sb.AppendLine();
                    break;
                case TableBlock tbl:
                    RenderTableMd(tbl, sb);
                    break;
                case ChartBlock chart:
                    sb.AppendLine($"**{chart.Title}** ({chart.Kind})");
                    sb.AppendLine();
                    break;
                case ConfidenceBandBlock band:
                    sb.AppendLine($"> {band.Symbol} {band.Band} confidence{(band.Caveats.Length > 0 ? $" — {string.Join("; ", band.Caveats)}" : string.Empty)}");
                    sb.AppendLine();
                    break;
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"<details><summary>{cs.Title}</summary>");
                    sb.AppendLine();
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine("</details>");
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
                        sb.AppendLine($"**{Esc(spark.MetricKey)}** `{Esc(spark.Unit)}`: {label}");
                        sb.AppendLine();
                        break;
                    }
            }
        }
    }

    private static void RenderTableMd(TableBlock tbl, StringBuilder sb)
    {
        if (tbl.Caption is not null)
            sb.AppendLine($"*{tbl.Caption}*");

        sb.Append("| ");
        for (int c = 0; c < tbl.Headers.Count; c++) { sb.Append(Esc(tbl.Headers[c])); sb.Append(c < tbl.Headers.Count - 1 ? " | " : " |"); }
        sb.AppendLine();
        sb.Append("|"); for (int c = 0; c < tbl.Headers.Count; c++) sb.Append("---|"); sb.AppendLine();

        foreach (TableRow row in tbl.Rows)
        {
            sb.Append("| ");
            for (int c = 0; c < tbl.Headers.Count; c++)
            {
                string cell = c < row.Cells.Count ? Esc(row.Cells[c].Display) : string.Empty;
                string? linkTarget = c < row.Cells.Count ? row.Cells[c].LinkTarget : null;
                string rendered = (linkTarget is { Length: > 0 })
                    ? $"[{cell}](#{Esc(linkTarget)})"
                    : cell;
                sb.Append(rendered); sb.Append(c < tbl.Headers.Count - 1 ? " | " : " |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private static void RenderSectionTablesMd(IReadOnlyList<CompactTable>? tables, StringBuilder sb)
    {
        if (tables is not { Count: > 0 })
            return;

        for (int i = 0; i < tables.Count; i++)
        {
            CompactTable table = tables[i];
            RenderTableMd(
                new TableBlock(
                    Caption: table.Title,
                    Headers: table.Headers.Select(h => h.Name).ToArray(),
                    Rows: table.Rows.Select(r => new TableRow(r.Values.Select(v => new TableCell(v?.ToString() ?? string.Empty)).ToArray())).ToArray()),
                sb);
        }
    }

    private static string Esc(string s) => s.Replace("|", "\\|");
}

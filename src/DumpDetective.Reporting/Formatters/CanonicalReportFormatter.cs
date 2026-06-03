using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            sb.AppendLine($"- Report: {ctx.ReportFormat} / {ctx.ReportAudience}");
            sb.AppendLine($"- Config: {(ctx.UsedConfigFile ? "config file" : "command line")}" + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({ctx.ConfigPath})"));
            sb.AppendLine($"- Diagnostic Mode: {(ctx.DiagnosticMode ? "on" : "off")}");
            sb.AppendLine($"- Index Prebuild: {ctx.IndexPrebuildMode}");
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

        if (summary.CriticalFindings is { Count: > 0 })
        {
            sb.AppendLine("Critical findings:");
                foreach (FindingRecord finding in summary.CriticalFindings)
                sb.AppendLine($"  - {finding.Title}: {finding.GetSummaryText()} | {finding.Recommendation}");
        }

        if (summary.WarningFindings is { Count: > 0 })
        {
            sb.AppendLine("Warning findings:");
                foreach (FindingRecord finding in summary.WarningFindings)
                sb.AppendLine($"  - {finding.Title}: {finding.GetSummaryText()} | {finding.Recommendation}");
        }

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
            sb.AppendLine($"| **Report** | {Esc(ctx.ReportFormat)} / {ctx.ReportAudience} |");
            sb.AppendLine($"| **Config** | {(ctx.UsedConfigFile ? "config file" : "command line") + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({Esc(ctx.ConfigPath)})")} |");
            sb.AppendLine($"| **Diagnostic Mode** | {(ctx.DiagnosticMode ? "on" : "off")} |");
            sb.AppendLine($"| **Index Prebuild** | {Esc(ctx.IndexPrebuildMode)} |");
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
                RenderSectionTablesMd(section.Tables, sb);
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

        if (summary.CriticalFindings is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Critical Findings");
            foreach (FindingRecord finding in summary.CriticalFindings)
                sb.AppendLine($"- **{Esc(finding.Title)}**: {Esc(finding.GetSummaryText())} | {Esc(finding.Recommendation)}");
        }

        if (summary.WarningFindings is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Warning Findings");
            foreach (FindingRecord finding in summary.WarningFindings)
                sb.AppendLine($"- **{Esc(finding.Title)}**: {Esc(finding.GetSummaryText())} | {Esc(finding.Recommendation)}");
        }

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

        if (summary.CriticalFindings is { Count: > 0 })
        {
            sb.AppendLine("Critical findings:");
                foreach (FindingRecord finding in summary.CriticalFindings)
                sb.AppendLine($"  - {finding.Title}: {finding.GetSummaryText()} | {finding.Recommendation}");
        }

        if (summary.WarningFindings is { Count: > 0 })
        {
            sb.AppendLine("Warning findings:");
                foreach (FindingRecord finding in summary.WarningFindings)
                sb.AppendLine($"  - {finding.Title}: {finding.GetSummaryText()} | {finding.Recommendation}");
        }

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

    private static void RenderSectionTablesMd(IReadOnlyList<SectionTable>? tables, StringBuilder sb)
    {
        if (tables is not { Count: > 0 })
            return;

        for (int i = 0; i < tables.Count; i++)
        {
            SectionTable table = tables[i];
            RenderTableMd(
                new TableBlock(
                    Caption: table.Title,
                    Headers: table.Headers,
                    Rows: table.Rows),
                sb);
        }
    }

    private static string Esc(string s) => s.Replace("|", "\\|");
}

// ── HTML (transitional — CSS/JS extracted to Templates/ in Phase E) ──────────

internal sealed class HtmlCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        static string Enc(string v) => System.Net.WebUtility.HtmlEncode(v);
        static string SevCss(string s) => s.ToLowerInvariant() switch
        {
            "critical" => "severity-critical",
            "warning" => "severity-warning",
            _ => "severity-info"
        };
        static string WrapAddr(string html) =>
            Regex.Replace(html, @"0x[0-9A-Fa-f]{4,}",
                m => $"<span class=\"addr\">{m.Value}<button class=\"copy-btn\" type=\"button\" aria-label=\"Copy {m.Value}\" data-copy=\"{m.Value}\" title=\"Copy to clipboard\">&#x2398;</button></span>",
                RegexOptions.CultureInvariant);

        bool isTrend = doc is TrendReportDocument;
        string title = isTrend ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = isTrend ? "Latest dump" : "Dump";
        string dumpPath = ReportFormatterHelpers.GetCanonicalDumpPath(doc);
        string exportFn = Enc(System.IO.Path.GetFileNameWithoutExtension(dumpPath));

        var sb = new StringBuilder();

        // ── Head + CSS ──────────────────────────────────────────────────────
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.AppendLine($"<title>{Enc(title)}</title>");
        sb.AppendLine("<style>");
        AppendCss(sb);
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<a class=\"skip-link\" href=\"#main\">Skip to main content</a>");
        sb.AppendLine("<div role=\"status\" aria-live=\"polite\" aria-atomic=\"true\" id=\"clipboard-status\" class=\"sr-only\"></div>");
        sb.AppendLine("<main class=\"container\" id=\"main\" tabindex=\"-1\">");

        // ── Header card ─────────────────────────────────────────────────────
        sb.AppendLine("<section class=\"header-card\">");
        sb.AppendLine($"<h1>{Enc(title)}</h1>");
        sb.AppendLine("<div class=\"meta-grid\">");
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">{Enc(dumpLabel)}:</span> <span class=\"wrap\">{Enc(dumpPath)}</span></div>");
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">Generated (UTC):</span> <time datetime=\"{doc.GeneratedAtUtc:yyyy-MM-ddTHH:mm:ssZ}\">{doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</time></div>");
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">Elapsed:</span> {doc.ElapsedSeconds:F1}s</div>");
        sb.AppendLine("</div>");
        // dedup diagnostics removed from report document
        sb.AppendLine($"<div class=\"action-bar\" role=\"toolbar\"><button type=\"button\" class=\"action-btn\" id=\"btn-download-json\" data-filename=\"{exportFn}\">\u2B07 JSON</button><button type=\"button\" class=\"action-btn\" id=\"btn-export-csv\" data-filename=\"{exportFn}\">\u2B07 CSV</button><button type=\"button\" class=\"action-btn\" id=\"btn-print\">\u2399 Print</button></div>");
        sb.AppendLine("</section>");

        if (doc is TrendReportDocument trendDoc)
        {
            sb.AppendLine($"<div class=\"dedup-note\"><strong>Dumps analyzed:</strong> {trendDoc.TrendDumpCount}</div>");
            if (trendDoc.TrendDumpPaths is { Count: > 0 })
            {
                string dumpList = string.Join("<br/>", trendDoc.TrendDumpPaths.Select(p => $"&bull; {Enc(p)}"));
                sb.AppendLine($"<div class=\"dedup-note\"><strong>Analyzed dumps:</strong><br/>{dumpList}</div>");
            }
            // T2.2: Lifecycle summary
            sb.AppendLine($"<div class=\"dedup-note\"><strong>Finding lifecycle:</strong> New={trendDoc.TrendNewFindingCount} &nbsp;|&nbsp; Persistent={trendDoc.TrendPersistentFindingCount} &nbsp;|&nbsp; Resolved={trendDoc.TrendResolvedFindingCount}</div>");
        }

        if (doc.HealthScorecard is { } scorecard)
        {
            RenderHealthScorecardHtml(scorecard, sb);
        }

        if (doc.IncidentContext is { } ctx)
        {
            string configText = (ctx.UsedConfigFile ? "config file" : "command line")
                + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({ctx.ConfigPath})");
            string runtimeText = ctx.RuntimeFlavor ?? "n/a";
            if (!string.IsNullOrWhiteSpace(ctx.RuntimeVersion))
                runtimeText += $" {ctx.RuntimeVersion}";

            string heapCountText = ctx.HeapCount.HasValue ? ctx.HeapCount.Value.ToString() : "n/a";

            sb.AppendLine("<section class=\"section-card\"><h2>Incident Context</h2>");
            sb.AppendLine("<table><thead><tr><th scope=\"col\">Field</th><th scope=\"col\">Value</th></tr></thead><tbody>");
            sb.AppendLine($"<tr><td>Mode</td><td>{Enc(ctx.Mode)}</td></tr>");
            sb.AppendLine($"<tr><td>Dump Path</td><td class=\"wrap\">{Enc(ctx.DumpPath)}</td></tr>");
            if (!string.IsNullOrWhiteSpace(ctx.BaselineDumpPath)) sb.AppendLine($"<tr><td>Baseline Dump</td><td class=\"wrap\">{Enc(ctx.BaselineDumpPath)}</td></tr>");
            sb.AppendLine($"<tr><td>Report</td><td>{Enc(ctx.ReportFormat)} / {ctx.ReportAudience}</td></tr>");
            sb.AppendLine($"<tr><td>Config</td><td>{Enc(configText)}</td></tr>");
            sb.AppendLine($"<tr><td>Diagnostic Mode</td><td>{(ctx.DiagnosticMode ? "on" : "off")}</td></tr>");
            sb.AppendLine($"<tr><td>Index Prebuild</td><td>{Enc(ctx.IndexPrebuildMode)}</td></tr>");
            sb.AppendLine($"<tr><td>Runtime</td><td>{Enc(runtimeText)}</td></tr>");
            sb.AppendLine($"<tr><td>GC Mode</td><td>{Enc(ctx.GcMode ?? "n/a")}</td></tr>");
            sb.AppendLine($"<tr><td>Heap Count</td><td>{heapCountText}</td></tr>");
            sb.AppendLine($"<tr><td>Heap Walkable</td><td>{(ctx.HeapCanWalk ? "yes" : "no")}</td></tr>");
            sb.AppendLine($"<tr><td>Active Analyzers</td><td>{ctx.ActiveAnalyzerCount}</td></tr>");
            sb.AppendLine($"<tr><td>Analysis Elapsed</td><td>{ctx.AnalysisElapsedSeconds:F1}s</td></tr>");
            sb.AppendLine("</tbody></table>");
            if (ctx.TrendSnapshots is { Count: > 0 })
            {
                sb.AppendLine("<h3>Snapshot Contexts</h3>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Snapshot</th><th scope=\"col\">Dump</th><th scope=\"col\">Elapsed</th><th scope=\"col\">Analyzers</th><th scope=\"col\">Findings</th></tr></thead><tbody>");
                foreach (var snap in ctx.TrendSnapshots)
                {
                    string role = snap.IsBaseline ? "Baseline" : snap.IsCurrent ? "Current" : $"Snapshot {snap.Index + 1}";
                    sb.AppendLine($"<tr><td>{Enc(role)}</td><td class=\"wrap\">{Enc(snap.DumpPath)}</td><td>{snap.ElapsedSeconds:F1}s</td><td>{snap.AnalyzerCount}</td><td>{snap.FindingCount}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</section>");
        }

        // ── Table Of Contents (HTML) ───────────────────────────────────────
        if (doc.Domains is null && (doc.Findings.Count > 0 || doc.AnalyzerSections.Count > 0))
        {
            sb.AppendLine("<nav class=\"toc\" aria-label=\"Report table of contents\">\n<div class=\"toc-title\">Table of contents</div>");
            if (doc.Findings.Count > 0)
            {
                sb.AppendLine("<div class=\"toc-section\"><strong>Findings</strong><ol>");
                for (int fi = 0; fi < doc.Findings.Count; fi++)
                    sb.AppendLine($"<li><a href=\"#finding-{fi}\">{Enc(doc.Findings[fi].Title)}</a></li>");
                sb.AppendLine("</ol></div>");
            }
            if (doc.AnalyzerSections.Count > 0)
            {
                sb.AppendLine("<div class=\"toc-section\"><strong>Analyzer sections</strong><ol>");
                for (int si = 0; si < doc.AnalyzerSections.Count; si++)
                    sb.AppendLine($"<li><a href=\"#detail-{si}\">{Enc(doc.AnalyzerSections[si].DisplayTitle)}</a></li>");
                sb.AppendLine("</ol></div>");
            }
            sb.AppendLine("</nav>");
        }

        // ── Filter bar ──────────────────────────────────────────────────────
        if (doc.Domains is null && doc.Findings.Count > 0)
        {
            int crit = 0, warn = 0;
            foreach (FindingRecord f in doc.Findings) { if (f.Severity == "Critical") crit++; else if (f.Severity == "Warning") warn++; }
            int info = doc.Findings.Count - crit - warn;
            sb.AppendLine("<div class=\"filter-bar\" id=\"filter-bar\" role=\"search\"><div class=\"filter-group\">");
            sb.AppendLine($"<button class=\"filter-btn active\" data-sev=\"all\" aria-pressed=\"true\" type=\"button\">All ({doc.Findings.Count})</button>");
            if (crit > 0) sb.AppendLine($"<button class=\"filter-btn filter-critical\" data-sev=\"critical\" aria-pressed=\"false\" type=\"button\">Critical ({crit})</button>");
            if (warn > 0) sb.AppendLine($"<button class=\"filter-btn filter-warning\" data-sev=\"warning\" aria-pressed=\"false\" type=\"button\">Warning ({warn})</button>");
            if (info > 0) sb.AppendLine($"<button class=\"filter-btn filter-info\" data-sev=\"info\" aria-pressed=\"false\" type=\"button\">Info ({info})</button>");
            sb.AppendLine("</div><input type=\"search\" id=\"filter-search\" class=\"filter-search\" placeholder=\"Search findings\u2026\" />");
            sb.AppendLine("<span id=\"filter-count\" class=\"filter-count\" aria-live=\"polite\" aria-atomic=\"true\"></span></div>");
        }

        // ── Finding cards ───────────────────────────────────────────────────
        for (int i = 0; i < doc.Findings.Count; i++)
        {
            FindingRecord f = doc.Findings[i];
            string sevCss = SevCss(f.Severity);
            string evSummary = f.GetSummaryText();
            string summary = Enc(evSummary.Length > 200 ? evSummary[..200] : evSummary);
            sb.AppendLine($"<section id=\"finding-{i}\" class=\"section-card\" data-severity=\"{Enc(f.Severity.ToLowerInvariant())}\" data-title=\"{Enc(f.Title)}\" data-summary=\"{summary}\">");
            sb.AppendLine($"<div class=\"section-header\"><span class=\"severity-badge {sevCss}\">{Enc(f.Severity)}</span><h2>{Enc(f.Title)} <a class=\"permalink\" href=\"#finding-{i}\" aria-label=\"Permalink\">🔗</a></h2><span class=\"category\">{Enc(f.Category)}</span></div>");

            if (f.Details is { Count: > 1 })
            {
                sb.AppendLine("<div class=\"summary\">" + string.Join("<br/>", f.Details.Select(Enc)) + "</div>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Details</td><td class=\"wrap\"><ul>" + string.Join(string.Empty, f.Details.Select(e => $"<li>{WrapAddr(Enc(e))}</li>")) + "</ul></td></tr>");
            }
            else
            {
                sb.AppendLine($"<p class=\"summary\">{Enc(evSummary)}</p>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Details</td><td class=\"wrap\">{WrapAddr(Enc(evSummary))}</td></tr>");
            }

            if (f.Confidence is not null)
                sb.AppendLine($"<tr><td>Confidence</td><td class=\"wrap\">{Enc(f.Confidence.Value.ToString("F2"))}</td></tr>");

            if (f.Caveats is { Count: > 0 })
                sb.AppendLine($"<tr><td>Caveats</td><td class=\"wrap\">{WrapAddr(Enc(string.Join("\n", f.Caveats)))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.Recommendation))
            {
                sb.AppendLine($"<tr><td>Recommendation</td><td class=\"wrap\">{WrapAddr(Enc(f.Recommendation))}</td></tr>");
            }
            sb.AppendLine("</tbody></table></section>");
        }

        // ── Analyzer sections ───────────────────────────────────────────────
        for (int i = 0; i < doc.AnalyzerSections.Count; i++)
        {
            AnalyzerDetailSection section = doc.AnalyzerSections[i];
            string colorClass = $"detail-color-{i % 6}";
            // Use SectionId as id when available (enables stable anchor links e.g. detail-0, T3, T4)
            string sectionId = string.IsNullOrEmpty(section.SectionId) ? $"detail-{i}" : section.SectionId;
            sb.AppendLine($"<section id=\"{Enc(sectionId)}\" class=\"analyzer-section {colorClass}\">");
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{Enc(section.DisplayTitle)} <a class=\"permalink\" href=\"#{Enc(sectionId)}\" aria-label=\"Permalink\">🔗</a></summary>");
            sb.AppendLine("<div class=\"detail-block\">");
            RenderBlocksHtml(section.Blocks, sb);
            sb.AppendLine("</div></details></section>");
        }

        // ── JSON embed + JS ─────────────────────────────────────────────────
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        sb.AppendLine($"<script type=\"application/json\" id=\"report-data\">{json}</script>");
        AppendJs(sb, exportFn);

        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderBlocksHtml(IReadOnlyList<SectionBlock> blocks, StringBuilder sb)
    {
        ReportHtmlShared.RenderBlocksHtml(blocks, sb);
    }

    private static void RenderHealthScorecardHtml(HealthScorecard scorecard, StringBuilder sb)
    {
        sb.Append(ReportHtmlShared.RenderHealthScorecard(scorecard));
    }

    private static void RenderTableHtml(TableBlock tbl, StringBuilder sb)
    {
        ReportHtmlShared.RenderTableHtml(tbl, sb);
    }

    private static void AppendCss(StringBuilder sb)
    {
        string baseDir = AppContext.BaseDirectory ?? string.Empty;
        string[] candidates = new[] {
            Path.Combine(baseDir, "Templates", "report.css"),
            Path.Combine(baseDir, "DumpDetective.Reporting", "Templates", "report.css"),
            Path.Combine(baseDir, "src", "DumpDetective.Reporting", "Templates", "report.css")
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                sb.AppendLine(File.ReadAllText(p));
                return;
            }
        }

        // fallback: inline CSS (preserve previous content)
        sb.AppendLine("body{margin:0;padding:0;background:#f5f7fb;color:#1f2937;font-family:Segoe UI,Arial,sans-serif;line-height:1.45;}");
        sb.AppendLine(".container{max-width:1200px;margin:0 auto;padding:24px;}");
        sb.AppendLine(".header-card,.section-card{background:#ffffff;border:1px solid #e5e7eb;border-radius:10px;box-shadow:0 1px 2px rgba(0,0,0,.05);}");
        sb.AppendLine(".header-card{padding:16px 18px;margin-bottom:16px;}");
        sb.AppendLine(".meta-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:10px 16px;margin-top:10px;}");
        sb.AppendLine(".meta-item{font-size:14px;}.meta-label{font-weight:600;color:#374151;}");
        sb.AppendLine(".dedup-note{margin-top:10px;padding:10px 12px;border-radius:8px;background:#eff6ff;color:#1d4ed8;font-size:14px;}");
        sb.AppendLine(".section-card{padding:14px 16px;margin-bottom:14px;}");
        sb.AppendLine(".section-header{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:6px;}");
        sb.AppendLine(".severity-badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em;text-transform:uppercase;}");
        sb.AppendLine(".severity-critical{background:#fee2e2;color:#b91c1c;}.severity-warning{background:#fef3c7;color:#92400e;}.severity-info{background:#dbeafe;color:#1e3a8a;}");
        sb.AppendLine(".category{font-size:12px;color:#6b7280;background:#f3f4f6;padding:2px 8px;border-radius:999px;}");
        sb.AppendLine(".summary{margin:8px 0 10px 0;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;background:#ffffff;}");
        sb.AppendLine("thead th{background:#f9fafb;font-weight:600;border:1px solid #e5e7eb;padding:8px;text-align:left;}");
        sb.AppendLine("tbody td{border:1px solid #e5e7eb;padding:8px;vertical-align:top;}");
        sb.AppendLine("tbody tr:nth-child(even){background:#fcfcfd;}.wrap{overflow-wrap:anywhere;word-break:break-word;}");
        // P1.2: Score contributor styles
        sb.AppendLine(".score-delta{display:inline-block;margin-left:8px;padding:1px 7px;border-radius:999px;font-size:11px;font-weight:600;}");
        sb.AppendLine(".score-delta-up{background:#fee2e2;color:#b91c1c;}.score-delta-down{background:#dcfce7;color:#166534;}.score-delta-flat{background:#f3f4f6;color:#6b7280;}");
        sb.AppendLine(".score-contributors-row td{background:#f9fafb!important;border-top:none!important;padding:4px 8px 4px 16px;}");
        sb.AppendLine(".score-contrib-toggle{background:none;border:none;cursor:pointer;color:#6b7280;font-size:12px;padding:2px 0;text-align:left;}");
        sb.AppendLine(".score-contrib-list{margin:4px 0 0 4px;padding-left:16px;font-size:12px;color:#374151;list-style:disc;}");
        sb.AppendLine(".remediation-title{margin:12px 0 6px 0;font-size:15px;}.remediation-list{margin:0;padding-left:20px;}");
        sb.AppendLine(".analyzer-section{background:#fff;border:1px solid #e2e8f0;border-left:4px solid #3b82f6;border-radius:10px;margin-bottom:14px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.07);}");
        sb.AppendLine(".detail-color-0{border-left-color:#3b82f6;}.detail-color-1{border-left-color:#7c3aed;}.detail-color-2{border-left-color:#0891b2;}");
        sb.AppendLine(".detail-color-3{border-left-color:#059669;}.detail-color-4{border-left-color:#d97706;}.detail-color-5{border-left-color:#e11d48;}");
        sb.AppendLine(".analyzer-section>details>summary{display:flex;align-items:center;gap:10px;padding:13px 16px;font-weight:600;font-size:14px;color:#1e293b;cursor:pointer;list-style:none;user-select:none;} ");
        sb.AppendLine(".analyzer-section>details>summary::-webkit-details-marker{display:none;} ");
        sb.AppendLine(".analyzer-section>details>summary:hover{background:rgba(0,0,0,0.02);} ");
        sb.AppendLine(".analyzer-section>details[open]>summary{background:rgba(0,0,0,0.02);border-bottom:1px solid #e2e8f0;} ");
        sb.AppendLine(".analyzer-section>details>summary::before{content:'';flex-shrink:0;display:inline-block;width:8px;height:8px;border-right:2px solid #94a3b8;border-bottom:2px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;}");
        sb.AppendLine(".analyzer-section>details[open]>summary::before{transform:rotate(45deg) translate(-2px,-2px);border-color:#3b82f6;} ");
        sb.AppendLine(".detail-color-1>details[open]>summary::before{border-color:#7c3aed;} .detail-color-2>details[open]>summary::before{border-color:#0891b2;} ");
        sb.AppendLine(".detail-color-3>details[open]>summary::before{border-color:#059669;} .detail-color-4>details[open]>summary::before{border-color:#d97706;} .detail-color-5>details[open]>summary::before{border-color:#e11d48;} ");
        sb.AppendLine(".analyzer-section .detail-block{border-radius:0 0 6px 6px;margin:0;} ");
        sb.AppendLine(".detail-block{background:#f8fafc;color:#1f2937;border-radius:8px;padding:12px;overflow:auto;font-family:Consolas,\"Cascadia Mono\",monospace;font-size:13px;line-height:1.5;} ");
        sb.AppendLine(".detail-subheading{font-weight:700;color:#1d4ed8;margin:8px 0 4px 0;} ");
        sb.AppendLine(".detail-divider{height:1px;background:#e2e8f0;margin:6px 0;} .detail-line{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;} ");
        sb.AppendLine(".detail-key{color:#059669;font-weight:600;} .detail-value{color:#374151;} .detail-path{color:#b45309;font-weight:600;} ");
        sb.AppendLine(".detail-gap{height:8px;} .detail-indent-1{padding-left:12px;} .detail-indent-2{padding-left:24px;} ");
        sb.AppendLine(".detail-block table{background:transparent;border-collapse:collapse;width:100%;margin:8px 0;color:#1f2937;} ");
        sb.AppendLine(".detail-block thead th{background:#f1f5f9;color:#1e293b;font-weight:600;border:1px solid #e2e8f0;padding:6px 8px;text-align:left;} ");
        sb.AppendLine(".detail-block tbody td{border:1px solid #e2e8f0;padding:5px 8px;vertical-align:top;overflow-wrap:anywhere;word-break:break-word;} ");
        sb.AppendLine(".detail-block tbody tr:nth-child(even){background:rgba(0,0,0,0.02);} ");
        sb.AppendLine(".detail-block caption{color:#6b7280;font-size:13px;font-weight:600;text-align:left;padding:2px 0 4px 0;caption-side:top;} ");
        sb.AppendLine(".detail-confidence{margin:8px 0 10px;display:flex;flex-direction:column;gap:6px;} ");
        sb.AppendLine(".confidence-band{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em;width:max-content;border:1px solid transparent;} ");
        sb.AppendLine(".confidence-high{background:#dcfce7;color:#166534;border-color:#bbf7d0;} ");
        sb.AppendLine(".confidence-medium{background:#fef3c7;color:#92400e;border-color:#fde68a;} ");
        sb.AppendLine(".confidence-low{background:#fee2e2;color:#b91c1c;border-color:#fecaca;} ");
        sb.AppendLine(".confidence-caveats{margin:0;padding-left:18px;color:#475569;font-size:12px;} ");
        sb.AppendLine(".confidence-caveats li{margin:0;} ");
        sb.AppendLine(".detail-nested{margin:6px 0;border:1px solid #e2e8f0;border-radius:6px;overflow:hidden;} ");
        sb.AppendLine(".detail-nested>summary{display:flex;align-items:center;gap:8px;padding:8px 10px;color:#374151;font-weight:600;font-size:13px;cursor:pointer;list-style:none;user-select:none;} ");
        sb.AppendLine(".detail-nested>summary::-webkit-details-marker{display:none;} .detail-nested>summary:hover{background:rgba(0,0,0,0.03);} ");
        sb.AppendLine(".detail-nested[open]>summary{background:rgba(0,0,0,0.03);border-bottom:1px solid #e2e8f0;} ");
        sb.AppendLine(".detail-nested>summary::before{content:'';flex-shrink:0;display:inline-block;width:7px;height:7px;border-right:1.5px solid #94a3b8;border-bottom:1.5px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;} ");
        sb.AppendLine(".detail-nested[open]>summary::before{transform:rotate(45deg) translate(-1px,-1px);border-color:#3b82f6;} ");
        sb.AppendLine(".detail-nested-content{padding:8px 4px;} ");
        sb.AppendLine(".skip-link{position:absolute;left:-9999px;top:8px;z-index:999;padding:8px 16px;background:#1d4ed8;color:#fff;border-radius:6px;font-weight:600;text-decoration:none;white-space:nowrap;} ");
        sb.AppendLine(".skip-link:focus{left:8px;} :focus-visible{outline:2px solid #2563eb;outline-offset:2px;border-radius:2px;} ");
        sb.AppendLine(".sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;} ");
        sb.AppendLine(".copy-btn{border:none;background:none;cursor:pointer;color:#64748b;font-size:11px;padding:1px 3px;border-radius:3px;vertical-align:middle;margin-left:3px;transition:background 0.15s,color 0.15s;line-height:1;} ");
        sb.AppendLine(".copy-btn:hover{background:#eff6ff;color:#1d4ed8;} .addr{white-space:nowrap;display:inline;} ");
        sb.AppendLine(".detail-frame{margin:1px 0;border-radius:3px;padding:1px 4px 1px 6px;border-left:2px solid transparent;} ");
        sb.AppendLine(".frame-code{font-family:Consolas,\"Cascadia Mono\",monospace;font-size:12px;display:block;overflow-wrap:anywhere;word-break:break-all;} ");
        sb.AppendLine(".frame-app{border-left-color:#3b82f6;background:rgba(219,234,254,0.3);} ");
        sb.AppendLine(".frame-app .frame-code{color:#1e3a8a;font-weight:600;} ");
        sb.AppendLine(".frame-fw{border-left-color:#e2e8f0;} .frame-fw .frame-code{color:#6b7280;} ");
        sb.AppendLine(".filter-bar{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px 0 6px 0;margin-bottom:6px} ");
        sb.AppendLine(".filter-group{display:flex;gap:4px;flex-wrap:wrap} ");
        sb.AppendLine(".filter-btn{padding:3px 12px;border:1px solid #e2e8f0;border-radius:20px;background:#fff;color:#374151;font-size:12px;font-weight:600;cursor:pointer;transition:all 0.15s;white-space:nowrap;} ");
        sb.AppendLine(".filter-btn:hover{background:#f1f5f9;border-color:#94a3b8} .filter-btn.active{background:#1d4ed8;color:#fff;border-color:#1d4ed8} ");
        sb.AppendLine(".filter-btn.filter-critical.active{background:#b91c1c;border-color:#b91c1c} .filter-btn.filter-warning.active{background:#92400e;border-color:#b45309} ");
        sb.AppendLine(".filter-search{flex:1;min-width:180px;max-width:360px;padding:5px 10px;border:1px solid #e2e8f0;border-radius:6px;font-size:14px;color:#1f2937;background:#fff;} ");
        sb.AppendLine(".toc{background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:12px;margin-bottom:14px;}");
        sb.AppendLine(".toc-title{font-weight:700;margin-bottom:8px;color:#111827;}");
        sb.AppendLine(".toc-section{margin:6px 0;padding-left:8px;}");
        sb.AppendLine(".toc-section ol{margin:6px 0 0 18px;padding:0;}");
        sb.AppendLine(".permalink{margin-left:8px;font-size:0.9em;color:#6b7280;text-decoration:none;}");
        sb.AppendLine(".permalink:hover{color:#111827;}");
        sb.AppendLine(".toc a.active{font-weight:700;color:#111827;}");
        sb.AppendLine(".filter-count{font-size:12px;color:#6b7280;white-space:nowrap;padding:0 4px;}");
        sb.AppendLine("thead th.sortable{cursor:pointer;user-select:none;} thead th.sortable:hover{background:#e9ebef;}");
        sb.AppendLine("thead th.sortable::after{content:' \u21c5';font-size:11px;opacity:0.35;margin-left:3px;}");
        sb.AppendLine("thead th.sortable[aria-sort=\"ascending\"]::after{content:' \u2191';opacity:1;} thead th.sortable[aria-sort=\"descending\"]::after{content:' \u2193';opacity:1;}");
        sb.AppendLine(".action-bar{display:flex;gap:8px;justify-content:flex-end;margin-top:12px;flex-wrap:wrap;}");
        sb.AppendLine(".action-btn{display:inline-flex;align-items:center;gap:5px;padding:6px 14px;border:1px solid #e2e8f0;border-radius:6px;background:#fff;color:#374151;font-size:13px;font-weight:500;cursor:pointer;transition:background 0.15s;}");
        sb.AppendLine(".action-btn:hover{background:#f1f5f9;border-color:#94a3b8;color:#1e293b;}");
        sb.AppendLine("@media print{.skip-link,.action-bar,.filter-bar,.copy-btn{display:none!important;}body{background:#fff;}");
        sb.AppendLine(".header-card,.section-card,.analyzer-section{box-shadow:none!important;border:1px solid #d1d5db!important;page-break-inside:avoid;}");
        sb.AppendLine(".analyzer-section>details{display:block!important;} .detail-block{border:1px solid #e2e8f0!important;} }");
    }

    private static void AppendJs(StringBuilder sb, string exportFn)
    {
        string baseDir = AppContext.BaseDirectory ?? string.Empty;
        string[] candidates = new[] {
            Path.Combine(baseDir, "Templates", "report.js"),
            Path.Combine(baseDir, "DumpDetective.Reporting", "Templates", "report.js"),
            Path.Combine(baseDir, "src", "DumpDetective.Reporting", "Templates", "report.js")
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                sb.AppendLine("<script>");
                sb.AppendLine(File.ReadAllText(p));
                sb.AppendLine("</script>");
                return;
            }
        }

        // fallback: no inline JS included here (templates/report.js expected in Templates/). Keep a minimal marker script.
        sb.AppendLine("<script>/* report.js not found — use Templates/report.js or rely on inline fallbacks in previous releases */</script>");
    }
}

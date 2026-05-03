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

// ── Text ──────────────────────────────────────────────────────────────────────

internal sealed class TextCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Text;

    public string Render(AnalysisReportDocument doc)
    {
        string title    = doc.IsTrendReport ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = doc.IsTrendReport ? "Latest dump" : "Dump";

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(StringConstants.Equals80);
        sb.AppendLine($"{dumpLabel}: {doc.DumpPath}");
        sb.AppendLine($"Generated (UTC): {doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Elapsed: {doc.ElapsedSeconds:F1}s");
        sb.AppendLine($"Schema: {doc.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine($"Dedup: merged {doc.DedupDiagnostics.MergedSections}/{doc.DedupDiagnostics.DuplicateCandidates} candidate duplicates");
        sb.AppendLine();

        if (doc.IsTrendReport)
        {
            sb.AppendLine($"Dumps analyzed: {doc.TrendDumpCount}");
            if (doc.TrendDumpPaths is { Count: > 0 })
            {
                sb.AppendLine("Analyzed dumps:");
                foreach (string path in doc.TrendDumpPaths)
                    sb.AppendLine($"  - {path}");
            }
            sb.AppendLine();
        }

        if (doc.ExecutiveSummary is { } ex)
        {
            sb.AppendLine("EXECUTIVE SUMMARY");
            sb.AppendLine(StringConstants.Equals80);
            sb.AppendLine($"- Total Managed Bytes: {FormatHelper.FormatBytes((ulong)ex.TotalManagedBytes)}");
            sb.AppendLine($"- Leak Likelihood Score: {ex.LeakLikelihoodScore}/100");
            sb.AppendLine($"- GC Pressure Score: {ex.GcPressureScore}/100");
            sb.AppendLine($"- Thread Contention Score: {ex.ThreadContentionScore}/100");
            foreach (FindingRecord rec in ex.TopRecommendations)
                sb.AppendLine($"  [{rec.Severity}] {rec.Title}");
            sb.AppendLine();
        }

        if (doc.DeveloperActionPlan.Count > 0)
        {
            sb.AppendLine("DEVELOPER ACTION PLAN");
            sb.AppendLine(StringConstants.Equals80);
            foreach (DeveloperActionRecord action in doc.DeveloperActionPlan)
            {
                sb.AppendLine($"[{action.Priority}] {action.Title}");
                sb.AppendLine($"  Action: {action.Action}");
                sb.AppendLine($"  Impact: {action.Impact}");
                sb.AppendLine();
            }
        }

        if (doc.Findings.Count > 0)
        {
            sb.AppendLine("FINDINGS");
            sb.AppendLine(StringConstants.Equals80);
            foreach (FindingRecord f in doc.Findings)
            {
                sb.AppendLine($"[{f.Severity}] {f.Title} ({f.Category})");
                sb.AppendLine(StringConstants.Separator80);
                sb.AppendLine(f.Evidence);
                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    sb.AppendLine("Remediation:");
                    foreach (string line in TableWrapHelper.Wrap(f.Recommendation, 96))
                        sb.AppendLine($"  - {line}");
                }
                sb.AppendLine();
            }
        }

        if (doc.Confidence.Count > 0)
        {
            sb.AppendLine("CONFIDENCE NOTES");
            sb.AppendLine(StringConstants.Separator80);
            foreach (ConfidenceNote note in doc.Confidence)
                sb.AppendLine($"- [{note.Analyzer}] {note.Reason}");
            sb.AppendLine();
        }

        if (doc.AnalyzerSections.Count > 0)
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
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"[{cs.Title}]");
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine();
                    break;
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

    private static string Indent(int level) => level switch { 1 => "  ", 2 => "    ", >= 3 => "      ", _ => string.Empty };
}

// ── Markdown ──────────────────────────────────────────────────────────────────

internal sealed class MarkdownCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Markdown;

    public string Render(AnalysisReportDocument doc)
    {
        string title    = doc.IsTrendReport ? "# DumpDetective Trend Analysis Report" : "# DumpDetective Analysis Report";
        string dumpLabel = doc.IsTrendReport ? "Latest dump" : "Dump";

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine($"> {dumpLabel}: `{doc.DumpPath}`  ");
        sb.AppendLine($"> Generated (UTC): `{doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}`  ");
        sb.AppendLine($"> Elapsed: `{doc.ElapsedSeconds:F1}s`");
        sb.AppendLine($"> Schema: `{doc.SchemaVersion}`");
        sb.AppendLine();
        sb.AppendLine($"> Dedup merged **{doc.DedupDiagnostics.MergedSections}** section(s) from **{doc.DedupDiagnostics.DuplicateCandidates}** candidate duplicate(s).");
        sb.AppendLine();

        if (doc.IsTrendReport)
        {
            sb.AppendLine($"> Dumps analyzed: **{doc.TrendDumpCount}**");
            if (doc.TrendDumpPaths is { Count: > 0 })
            {
                sb.AppendLine("> Analyzed dumps:");
                foreach (string path in doc.TrendDumpPaths)
                    sb.AppendLine($"> - `{path}`");
            }
            sb.AppendLine();
        }

        if (doc.ExecutiveSummary is { } ex)
        {
            sb.AppendLine("## Executive Summary");
            sb.AppendLine();
            sb.AppendLine("| Signal | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| **Total Managed Bytes** | {FormatHelper.FormatBytes((ulong)ex.TotalManagedBytes)} |");
            sb.AppendLine($"| **Leak Likelihood** | {ex.LeakLikelihoodScore}/100 |");
            sb.AppendLine($"| **GC Pressure** | {ex.GcPressureScore}/100 |");
            sb.AppendLine($"| **Thread Contention** | {ex.ThreadContentionScore}/100 |");
            sb.AppendLine();
            if (ex.TopRecommendations.Count > 0)
            {
                sb.AppendLine("**Top Recommendations**");
                sb.AppendLine();
                foreach (FindingRecord rec in ex.TopRecommendations)
                    sb.AppendLine($"- [{rec.Severity}] {rec.Title}");
                sb.AppendLine();
            }
        }

        if (doc.DeveloperActionPlan.Count > 0)
        {
            sb.AppendLine("## Developer Action Plan");
            sb.AppendLine();
            sb.AppendLine("| Priority | Title | Action | Impact |");
            sb.AppendLine("|---|---|---|---|");
            foreach (DeveloperActionRecord action in doc.DeveloperActionPlan)
                sb.AppendLine($"| {action.Priority} | {Esc(action.Title)} | {Esc(action.Action)} | {Esc(action.Impact)} |");
            sb.AppendLine();
        }

        if (doc.Findings.Count > 0)
        {
            sb.AppendLine("## Findings");
            sb.AppendLine();
            foreach (FindingRecord f in doc.Findings)
            {
                sb.AppendLine($"## [{f.Severity}] {f.Title}");
                sb.AppendLine();
                sb.AppendLine(f.Evidence);
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    sb.AppendLine("**Remediation**");
                    sb.AppendLine();
                    sb.AppendLine($"- {f.Recommendation}");
                    sb.AppendLine();
                }
            }
        }

        if (doc.Confidence.Count > 0)
        {
            sb.AppendLine("## Confidence Notes");
            sb.AppendLine();
            foreach (ConfidenceNote note in doc.Confidence)
                sb.AppendLine($"- **[{note.Analyzer}]** {note.Reason}");
            sb.AppendLine();
        }

        if (doc.AnalyzerSections.Count > 0)
        {
            sb.AppendLine("## Detailed Analyzer Sections");
            sb.AppendLine();
            foreach (AnalyzerDetailSection section in doc.AnalyzerSections)
            {
                sb.AppendLine($"### {section.DisplayTitle}");
                sb.AppendLine();
                RenderBlocksMd(section.Blocks, sb);
                sb.AppendLine();
            }
        }

        return sb.ToString();
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
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"<details><summary>{cs.Title}</summary>");
                    sb.AppendLine();
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                    break;
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
                sb.Append(cell); sb.Append(c < tbl.Headers.Count - 1 ? " | " : " |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
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
            "warning"  => "severity-warning",
            _          => "severity-info"
        };
        static string WrapAddr(string html) =>
            Regex.Replace(html, @"0x[0-9A-Fa-f]{4,}",
                m => $"<span class=\"addr\">{m.Value}<button class=\"copy-btn\" type=\"button\" aria-label=\"Copy {m.Value}\" data-copy=\"{m.Value}\" title=\"Copy to clipboard\">&#x2398;</button></span>",
                RegexOptions.CultureInvariant);

        string title    = doc.IsTrendReport ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = doc.IsTrendReport ? "Latest dump" : "Dump";
        string exportFn  = Enc(System.IO.Path.GetFileNameWithoutExtension(doc.DumpPath));

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
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">{Enc(dumpLabel)}:</span> <span class=\"wrap\">{Enc(doc.DumpPath)}</span></div>");
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">Generated (UTC):</span> <time datetime=\"{doc.GeneratedAtUtc:yyyy-MM-ddTHH:mm:ssZ}\">{doc.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</time></div>");
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">Elapsed:</span> {doc.ElapsedSeconds:F1}s</div>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<div class=\"dedup-note\"><strong>Dedup:</strong> merged {doc.DedupDiagnostics.MergedSections}/{doc.DedupDiagnostics.DuplicateCandidates}</div>");
        sb.AppendLine($"<div class=\"action-bar\" role=\"toolbar\"><button type=\"button\" class=\"action-btn\" id=\"btn-download-json\" data-filename=\"{exportFn}\">\u2B07 JSON</button><button type=\"button\" class=\"action-btn\" id=\"btn-export-csv\" data-filename=\"{exportFn}\">\u2B07 CSV</button><button type=\"button\" class=\"action-btn\" id=\"btn-print\">\u2399 Print</button></div>");
        sb.AppendLine("</section>");

        if (doc.IsTrendReport)
        {
            sb.AppendLine($"<div class=\"dedup-note\"><strong>Dumps analyzed:</strong> {doc.TrendDumpCount}</div>");
            if (doc.TrendDumpPaths is { Count: > 0 })
            {
                string dumpList = string.Join("<br/>", doc.TrendDumpPaths.Select(p => $"&bull; {Enc(p)}"));
                sb.AppendLine($"<div class=\"dedup-note\"><strong>Analyzed dumps:</strong><br/>{dumpList}</div>");
            }
        }

        // ── Executive summary ───────────────────────────────────────────────
        if (doc.ExecutiveSummary is { } ex)
        {
            sb.AppendLine("<section class=\"section-card\"><h2>Executive Summary</h2>");
            sb.AppendLine("<table><thead><tr><th scope=\"col\">Signal</th><th scope=\"col\">Value</th></tr></thead><tbody>");
            sb.AppendLine($"<tr><td>Total Managed Bytes</td><td>{FormatHelper.FormatBytes((ulong)ex.TotalManagedBytes)}</td></tr>");
            sb.AppendLine($"<tr><td>Leak Likelihood Score</td><td>{ex.LeakLikelihoodScore}/100</td></tr>");
            sb.AppendLine($"<tr><td>GC Pressure Score</td><td>{ex.GcPressureScore}/100</td></tr>");
            sb.AppendLine($"<tr><td>Thread Contention Score</td><td>{ex.ThreadContentionScore}/100</td></tr>");
            sb.AppendLine("</tbody></table>");
            if (ex.TopRecommendations.Count > 0)
            {
                sb.AppendLine("<h3>Top Recommendations</h3><ul>");
                foreach (FindingRecord rec in ex.TopRecommendations)
                    sb.AppendLine($"<li><span class=\"severity-badge {SevCss(rec.Severity)}\">{Enc(rec.Severity)}</span> {Enc(rec.Title)}</li>");
                sb.AppendLine("</ul>");
            }
            sb.AppendLine("</section>");
        }

        // ── Developer action plan ───────────────────────────────────────────
        if (doc.DeveloperActionPlan.Count > 0)
        {
            sb.AppendLine("<section class=\"section-card\"><h2>Developer Action Plan</h2>");
            sb.AppendLine("<table><thead><tr><th scope=\"col\">Priority</th><th scope=\"col\">Title</th><th scope=\"col\">Action</th><th scope=\"col\">Impact</th></tr></thead><tbody>");
            foreach (DeveloperActionRecord action in doc.DeveloperActionPlan)
                sb.AppendLine($"<tr><td>{Enc(action.Priority)}</td><td>{Enc(action.Title)}</td><td class=\"wrap\">{Enc(action.Action)}</td><td class=\"wrap\">{Enc(action.Impact)}</td></tr>");
            sb.AppendLine("</tbody></table></section>");
        }

        // ── Filter bar ──────────────────────────────────────────────────────
        if (doc.Findings.Count > 0)
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
            string sevCss  = SevCss(f.Severity);
            string summary = Enc(f.Evidence.Length > 200 ? f.Evidence[..200] : f.Evidence);
            sb.AppendLine($"<section id=\"finding-{i}\" class=\"section-card\" data-severity=\"{Enc(f.Severity.ToLowerInvariant())}\" data-title=\"{Enc(f.Title)}\" data-summary=\"{summary}\">");
            sb.AppendLine($"<div class=\"section-header\"><span class=\"severity-badge {sevCss}\">{Enc(f.Severity)}</span><h2>{Enc(f.Title)}</h2><span class=\"category\">{Enc(f.Category)}</span></div>");
            sb.AppendLine($"<p class=\"summary\">{Enc(f.Evidence)}</p>");
            sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
            sb.AppendLine($"<tr><td>Evidence</td><td class=\"wrap\">{WrapAddr(Enc(f.Evidence))}</td></tr>");
            if (!string.IsNullOrWhiteSpace(f.Recommendation))
                sb.AppendLine($"<tr><td>Recommendation</td><td class=\"wrap\">{WrapAddr(Enc(f.Recommendation))}</td></tr>");
            sb.AppendLine("</tbody></table></section>");
        }

        if (doc.Confidence.Count > 0)
        {
            sb.AppendLine("<section class=\"section-card\"><h2>Confidence Notes</h2><ul>");
            foreach (ConfidenceNote note in doc.Confidence)
                sb.AppendLine($"<li><strong>[{Enc(note.Analyzer)}]</strong> {Enc(note.Reason)}</li>");
            sb.AppendLine("</ul></section>");
        }

        // ── Analyzer sections ───────────────────────────────────────────────
        for (int i = 0; i < doc.AnalyzerSections.Count; i++)
        {
            AnalyzerDetailSection section = doc.AnalyzerSections[i];
            string colorClass = $"detail-color-{i % 6}";
            sb.AppendLine($"<section id=\"detail-{i}\" class=\"analyzer-section {colorClass}\">");
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{Enc(section.DisplayTitle)}</summary>");
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
        static string Enc(string v) => System.Net.WebUtility.HtmlEncode(v);
        static string IndCss(int lvl) => lvl switch { 1 => " detail-indent-1", 2 => " detail-indent-2", >= 3 => " detail-indent-2", _ => string.Empty };
        static string WrapAddr(string html) =>
            Regex.Replace(html, @"0x[0-9A-Fa-f]{4,}",
                m => $"<span class=\"addr\">{m.Value}<button class=\"copy-btn\" type=\"button\" aria-label=\"Copy {m.Value}\" data-copy=\"{m.Value}\" title=\"Copy\">&#x2398;</button></span>",
                RegexOptions.CultureInvariant);

        foreach (SectionBlock block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    sb.AppendLine($"<div class=\"detail-subheading{IndCss(h.IndentLevel)}\">{Enc(h.Text)}</div>");
                    break;
                case MetricBlock m:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(m.IndentLevel)}\"><span class=\"detail-key\">{Enc(m.Label)}:</span> <span class=\"detail-value wrap\">{WrapAddr(Enc(m.Value))}</span></div>");
                    break;
                case PathBlock p:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(p.IndentLevel)}\"><span class=\"detail-key\">{Enc(p.Label)}:</span> <span class=\"detail-path wrap\">{WrapAddr(Enc(p.Path))}</span></div>");
                    break;
                case StackFrameBlock sf:
                    {
                        string fwCls = sf.IsFrameworkFrame ? " frame-fw" : " frame-app";
                        sb.AppendLine($"<div class=\"detail-line detail-frame{fwCls}{IndCss(sf.IndentLevel)}\"><code class=\"frame-code\">{WrapAddr(Enc("at " + sf.Frame))}</code></div>");
                        break;
                    }
                case TextBlock t:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(t.IndentLevel)}\">{WrapAddr(Enc(t.Text))}</div>");
                    break;
                case ListItemBlock l:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(l.IndentLevel)}\">&#x2022; {WrapAddr(Enc(l.Text))}</div>");
                    break;
                case DividerBlock:
                    sb.AppendLine("<div class=\"detail-divider\"></div>");
                    break;
                case BlankBlock:
                    sb.AppendLine("<div class=\"detail-gap\"></div>");
                    break;
                case TableBlock tbl:
                    RenderTableHtml(tbl, sb);
                    break;
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"<details class=\"detail-nested\"><summary>{Enc(cs.Title)}</summary><div class=\"detail-nested-content\">");
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine("</div></details>");
                    break;
            }
        }
    }

    private static void RenderTableHtml(TableBlock tbl, StringBuilder sb)
    {
        static string Enc(string v) => System.Net.WebUtility.HtmlEncode(v);
        sb.Append("<table>");
        if (tbl.Caption is not null) sb.Append($"<caption>{Enc(tbl.Caption)}</caption>");
        sb.Append("<thead><tr>");
        foreach (string h in tbl.Headers) sb.Append($"<th scope=\"col\">{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (TableRow row in tbl.Rows)
        {
            sb.Append("<tr>");
            foreach (TableCell cell in row.Cells)
            {
                string da = cell.RawValue.HasValue ? $" data-value=\"{cell.RawValue.Value}\"" : string.Empty;
                sb.Append($"<td{da}>{Enc(cell.Display)}</td>");
            }
            sb.Append("</tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendCss(StringBuilder sb)
    {
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
        sb.AppendLine(".remediation-title{margin:12px 0 6px 0;font-size:15px;}.remediation-list{margin:0;padding-left:20px;}");
        sb.AppendLine(".analyzer-section{background:#fff;border:1px solid #e2e8f0;border-left:4px solid #3b82f6;border-radius:10px;margin-bottom:14px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.07);}");
        sb.AppendLine(".detail-color-0{border-left-color:#3b82f6;}.detail-color-1{border-left-color:#7c3aed;}.detail-color-2{border-left-color:#0891b2;}");
        sb.AppendLine(".detail-color-3{border-left-color:#059669;}.detail-color-4{border-left-color:#d97706;}.detail-color-5{border-left-color:#e11d48;}");
        sb.AppendLine(".analyzer-section>details>summary{display:flex;align-items:center;gap:10px;padding:13px 16px;font-weight:600;font-size:14px;color:#1e293b;cursor:pointer;list-style:none;user-select:none;}");
        sb.AppendLine(".analyzer-section>details>summary::-webkit-details-marker{display:none;}");
        sb.AppendLine(".analyzer-section>details>summary:hover{background:rgba(0,0,0,0.02);}");
        sb.AppendLine(".analyzer-section>details[open]>summary{background:rgba(0,0,0,0.02);border-bottom:1px solid #e2e8f0;}");
        sb.AppendLine(".analyzer-section>details>summary::before{content:'';flex-shrink:0;display:inline-block;width:8px;height:8px;border-right:2px solid #94a3b8;border-bottom:2px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;}");
        sb.AppendLine(".analyzer-section>details[open]>summary::before{transform:rotate(45deg) translate(-2px,-2px);border-color:#3b82f6;}");
        sb.AppendLine(".detail-color-1>details[open]>summary::before{border-color:#7c3aed;}.detail-color-2>details[open]>summary::before{border-color:#0891b2;}");
        sb.AppendLine(".detail-color-3>details[open]>summary::before{border-color:#059669;}.detail-color-4>details[open]>summary::before{border-color:#d97706;}.detail-color-5>details[open]>summary::before{border-color:#e11d48;}");
        sb.AppendLine(".analyzer-section .detail-block{border-radius:0 0 6px 6px;margin:0;}");
        sb.AppendLine(".detail-block{background:#f8fafc;color:#1f2937;border-radius:8px;padding:12px;overflow:auto;font-family:Consolas,\"Cascadia Mono\",monospace;font-size:13px;line-height:1.5;}");
        sb.AppendLine(".detail-subheading{font-weight:700;color:#1d4ed8;margin:8px 0 4px 0;}");
        sb.AppendLine(".detail-divider{height:1px;background:#e2e8f0;margin:6px 0;}.detail-line{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;}");
        sb.AppendLine(".detail-key{color:#059669;font-weight:600;}.detail-value{color:#374151;}.detail-path{color:#b45309;font-weight:600;}");
        sb.AppendLine(".detail-gap{height:8px;}.detail-indent-1{padding-left:12px;}.detail-indent-2{padding-left:24px;}");
        sb.AppendLine(".detail-block table{background:transparent;border-collapse:collapse;width:100%;margin:8px 0;color:#1f2937;}");
        sb.AppendLine(".detail-block thead th{background:#f1f5f9;color:#1e293b;font-weight:600;border:1px solid #e2e8f0;padding:6px 8px;text-align:left;}");
        sb.AppendLine(".detail-block tbody td{border:1px solid #e2e8f0;padding:5px 8px;vertical-align:top;overflow-wrap:anywhere;word-break:break-word;}");
        sb.AppendLine(".detail-block tbody tr:nth-child(even){background:rgba(0,0,0,0.02);}");
        sb.AppendLine(".detail-block caption{color:#6b7280;font-size:13px;font-weight:600;text-align:left;padding:2px 0 4px 0;caption-side:top;}");
        sb.AppendLine(".detail-nested{margin:6px 0;border:1px solid #e2e8f0;border-radius:6px;overflow:hidden;}");
        sb.AppendLine(".detail-nested>summary{display:flex;align-items:center;gap:8px;padding:8px 10px;color:#374151;font-weight:600;font-size:13px;cursor:pointer;list-style:none;user-select:none;}");
        sb.AppendLine(".detail-nested>summary::-webkit-details-marker{display:none;}.detail-nested>summary:hover{background:rgba(0,0,0,0.03);}");
        sb.AppendLine(".detail-nested[open]>summary{background:rgba(0,0,0,0.03);border-bottom:1px solid #e2e8f0;}");
        sb.AppendLine(".detail-nested>summary::before{content:'';flex-shrink:0;display:inline-block;width:7px;height:7px;border-right:1.5px solid #94a3b8;border-bottom:1.5px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;}");
        sb.AppendLine(".detail-nested[open]>summary::before{transform:rotate(45deg) translate(-1px,-1px);border-color:#3b82f6;}");
        sb.AppendLine(".detail-nested-content{padding:8px 4px;}");
        sb.AppendLine(".skip-link{position:absolute;left:-9999px;top:8px;z-index:999;padding:8px 16px;background:#1d4ed8;color:#fff;border-radius:6px;font-weight:600;text-decoration:none;white-space:nowrap;}");
        sb.AppendLine(".skip-link:focus{left:8px;}:focus-visible{outline:2px solid #2563eb;outline-offset:2px;border-radius:2px;}");
        sb.AppendLine(".sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;}");
        sb.AppendLine(".copy-btn{border:none;background:none;cursor:pointer;color:#64748b;font-size:11px;padding:1px 3px;border-radius:3px;vertical-align:middle;margin-left:3px;transition:background 0.15s,color 0.15s;line-height:1;}");
        sb.AppendLine(".copy-btn:hover{background:#eff6ff;color:#1d4ed8;}.addr{white-space:nowrap;display:inline;}");
        // Stack frame rendering — app frames highlighted, framework frames muted with accent
        sb.AppendLine(".detail-frame{margin:1px 0;border-radius:3px;padding:1px 4px 1px 6px;border-left:2px solid transparent;}");
        sb.AppendLine(".frame-code{font-family:Consolas,\"Cascadia Mono\",monospace;font-size:12px;display:block;overflow-wrap:anywhere;word-break:break-all;}");
        sb.AppendLine(".frame-app{border-left-color:#3b82f6;background:rgba(219,234,254,0.3);}");
        sb.AppendLine(".frame-app .frame-code{color:#1e3a8a;font-weight:600;}");
        sb.AppendLine(".frame-fw{border-left-color:#e2e8f0;}");
        sb.AppendLine(".frame-fw .frame-code{color:#6b7280;}");
        sb.AppendLine(".filter-bar{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px 0 6px 0;margin-bottom:6px;}");
        sb.AppendLine(".filter-group{display:flex;gap:4px;flex-wrap:wrap;}");
        sb.AppendLine(".filter-btn{padding:3px 12px;border:1px solid #e2e8f0;border-radius:20px;background:#fff;color:#374151;font-size:12px;font-weight:600;cursor:pointer;transition:all 0.15s;white-space:nowrap;}");
        sb.AppendLine(".filter-btn:hover{background:#f1f5f9;border-color:#94a3b8;}.filter-btn.active{background:#1d4ed8;color:#fff;border-color:#1d4ed8;}");
        sb.AppendLine(".filter-btn.filter-critical.active{background:#b91c1c;border-color:#b91c1c;}.filter-btn.filter-warning.active{background:#92400e;border-color:#b45309;}");
        sb.AppendLine(".filter-search{flex:1;min-width:180px;max-width:360px;padding:5px 10px;border:1px solid #e2e8f0;border-radius:6px;font-size:14px;color:#1f2937;background:#fff;}");
        sb.AppendLine(".filter-count{font-size:12px;color:#6b7280;white-space:nowrap;padding:0 4px;}");
        sb.AppendLine("thead th.sortable{cursor:pointer;user-select:none;}.thead th.sortable:hover{background:#e9ebef;}");
        sb.AppendLine("thead th.sortable::after{content:' \u21c5';font-size:11px;opacity:0.35;margin-left:3px;}");
        sb.AppendLine("thead th.sortable[aria-sort=\"ascending\"]::after{content:' \u2191';opacity:1;}thead th.sortable[aria-sort=\"descending\"]::after{content:' \u2193';opacity:1;}");
        sb.AppendLine(".action-bar{display:flex;gap:8px;justify-content:flex-end;margin-top:12px;flex-wrap:wrap;}");
        sb.AppendLine(".action-btn{display:inline-flex;align-items:center;gap:5px;padding:6px 14px;border:1px solid #e2e8f0;border-radius:6px;background:#fff;color:#374151;font-size:13px;font-weight:500;cursor:pointer;transition:background 0.15s;}");
        sb.AppendLine(".action-btn:hover{background:#f1f5f9;border-color:#94a3b8;color:#1e293b;}");
        sb.AppendLine("@media print{.skip-link,.action-bar,.filter-bar,.copy-btn{display:none!important;}body{background:#fff;}");
        sb.AppendLine(".header-card,.section-card,.analyzer-section{box-shadow:none!important;border:1px solid #d1d5db!important;page-break-inside:avoid;}");
        sb.AppendLine(".analyzer-section>details{display:block!important;}.detail-block{border:1px solid #e2e8f0!important;}}");
    }

    private static void AppendJs(StringBuilder sb, string exportFn)
    {
        sb.AppendLine("<script>(function(){");
        sb.AppendLine("document.querySelectorAll('.analyzer-section details').forEach(function(d){var s=d.querySelector('summary');if(s)s.setAttribute('aria-expanded',d.open);d.addEventListener('toggle',function(){if(s)s.setAttribute('aria-expanded',d.open);});});");
        sb.AppendLine("var sr=document.getElementById('clipboard-status');");
        sb.AppendLine("function flash(m){if(sr){sr.textContent=m;setTimeout(function(){sr.textContent='';},2000);}}");
        sb.AppendLine("document.querySelectorAll('.copy-btn').forEach(function(btn){btn.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();if(navigator.clipboard)navigator.clipboard.writeText(btn.dataset.copy||'').then(function(){flash('Copied: '+btn.dataset.copy);});});});");
        sb.AppendLine($"var btnJson=document.getElementById('btn-download-json');if(btnJson)btnJson.addEventListener('click',function(){{var el=document.getElementById('report-data');if(!el)return;var blob=new Blob([el.textContent],{{type:'application/json'}});var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='{exportFn}.json';a.click();URL.revokeObjectURL(a.href);}});");
        sb.AppendLine($"var btnCsv=document.getElementById('btn-export-csv');if(btnCsv)btnCsv.addEventListener('click',function(){{var el=document.getElementById('report-data');if(!el)return;try{{var d=JSON.parse(el.textContent);var rows=[['ID','Severity','Category','Title','Evidence','Recommendation']];(d.findings||[]).forEach(function(f){{rows.push([f.fingerprint||'',f.severity||'',f.category||'',f.title||'',f.evidence||'',f.recommendation||'']);}});var csv=rows.map(function(r){{return r.map(function(c){{return'\"'+(c||'').replace(/\"/g,'\"\"')+'\"';}}).join(',');}}).join('\\r\\n');var blob=new Blob(['\\uFEFF'+csv],{{type:'text/csv;charset=utf-8'}});var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='{exportFn}-findings.csv';a.click();URL.revokeObjectURL(a.href);}}catch(e){{}}}});");
        sb.AppendLine("var btnPrint=document.getElementById('btn-print');if(btnPrint)btnPrint.addEventListener('click',function(){window.print();});");
        sb.AppendLine("var fbs=document.querySelectorAll('.filter-btn[data-sev]');var fsi=document.getElementById('filter-search');var fco=document.getElementById('filter-count');");
        sb.AppendLine("function applyFilter(){var txt=fsi?fsi.value.trim().toLowerCase():'';var asev='all';fbs.forEach(function(b){if(b.classList.contains('active'))asev=b.dataset.sev;});");
        sb.AppendLine("var cards=document.querySelectorAll('.section-card[data-severity]');var vis=0;cards.forEach(function(c){var s=(c.dataset.severity||'').toLowerCase();var ok=(asev==='all'||s===asev)&&(!txt||(c.dataset.title||'').toLowerCase().indexOf(txt)>=0||(c.dataset.summary||'').toLowerCase().indexOf(txt)>=0);c.hidden=!ok;if(ok)vis++;});");
        sb.AppendLine("if(fco)fco.textContent=cards.length?vis+' of '+cards.length+' finding(s)':'';}");
        sb.AppendLine("fbs.forEach(function(b){b.addEventListener('click',function(){fbs.forEach(function(x){x.classList.remove('active');x.setAttribute('aria-pressed','false');});b.classList.add('active');b.setAttribute('aria-pressed','true');applyFilter();});});");
        sb.AppendLine("if(fsi)fsi.addEventListener('input',applyFilter);applyFilter();");
        sb.AppendLine("document.querySelectorAll('table').forEach(function(tbl){var ths=tbl.querySelectorAll('thead th');ths.forEach(function(th,col){th.classList.add('sortable');th.setAttribute('tabindex','0');var dir=1;");
        sb.AppendLine("function doSort(){var tb=tbl.querySelector('tbody');if(!tb)return;var rows=Array.from(tb.querySelectorAll('tr'));");
        sb.AppendLine("rows.sort(function(a,b){var ac=a.cells[col],bc=b.cells[col];var av=ac&&ac.dataset.value!==undefined&&ac.dataset.value!==''?parseFloat(ac.dataset.value):NaN;var bv=bc&&bc.dataset.value!==undefined&&bc.dataset.value!==''?parseFloat(bc.dataset.value):NaN;");
        sb.AppendLine("if(!isNaN(av)&&!isNaN(bv))return dir*(av-bv);var at=(ac?ac.textContent:'').toLowerCase(),bt=(bc?bc.textContent:'').toLowerCase();return dir*(at<bt?-1:at>bt?1:0);});");
        sb.AppendLine("rows.forEach(function(r){tb.appendChild(r);});ths.forEach(function(h){h.removeAttribute('aria-sort');});th.setAttribute('aria-sort',dir>0?'ascending':'descending');dir=-dir;}");
        sb.AppendLine("th.addEventListener('click',doSort);th.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();doSort();}});});});");
        sb.AppendLine("})();</script>");
    }
}

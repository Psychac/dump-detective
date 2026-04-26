using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DumpDetective.Reporting.Formatters;

internal interface IReportFormatter
{
    ReportFormat Format { get; }
    string Render(ComposedReport report);
}

internal sealed class TextCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Text;

    public string Render(ComposedReport report)
    {
        string reportTitle = report.IsTrendReport ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = report.IsTrendReport ? "Latest dump" : "Dump";

        List<string> lines =
        [
            reportTitle,
            new string('=', 100),
            $"{dumpLabel}: {report.DumpPath}",
            $"Generated (UTC): {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}",
            $"Elapsed: {report.Elapsed.TotalSeconds:F1}s",
            string.Empty,
            $"Dedup: merged {report.DedupDiagnostics.MergedSections}/{report.DedupDiagnostics.DuplicateCandidates} candidate duplicates",
            string.Empty
        ];

        if (report.IsTrendReport)
        {
            lines.Add($"Dumps analyzed: {report.TrendDumpCount}");
            if (report.TrendDumpPaths is { Count: > 0 })
            {
                lines.Add("Analyzed dumps:");
                foreach (string dumpPath in report.TrendDumpPaths)
                {
                    lines.Add($"  - {dumpPath}");
                }
            }
            lines.Add(string.Empty);
        }

        if (report.ExecutiveSummary.Count > 0)
        {
            lines.Add("EXECUTIVE SUMMARY");
            lines.Add(new string('=', 100));
            foreach (ExecutiveSummaryItem item in report.ExecutiveSummary)
            {
                lines.Add($"- {item.Label}: {item.Value}");
            }
            lines.Add(string.Empty);
        }

        if (report.DeveloperActionPlan.Count > 0)
        {
            lines.Add("DEVELOPER ACTION PLAN");
            lines.Add(new string('=', 100));
            foreach (DeveloperActionItem action in report.DeveloperActionPlan)
            {
                lines.Add($"[{action.Priority}] {action.Title}");
                lines.Add($"  Action: {action.Action}");
                lines.Add($"  Impact: {action.Impact}");
                lines.Add(string.Empty);
            }
        }

        foreach (ReportSection section in report.Sections)
        {
            lines.Add($"[{section.Severity}] {section.Title} ({section.Category})");
            lines.Add(new string('-', 100));
            lines.Add(section.NarrativeSummary);

            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                IReadOnlyList<string> wrapped = TableWrapHelper.Wrap(row.Value, 78);
                lines.Add($"- {row.Label}: {wrapped[0]}");
                for (int i = 1; i < wrapped.Count; i++)
                {
                    lines.Add($"  {new string(' ', row.Label.Length + 2)}{wrapped[i]}");
                }
            }

            if (section.RemediationHints.Count > 0)
            {
                lines.Add("Remediation:");
                foreach (string hint in section.RemediationHints)
                {
                    foreach (string wrapped in TableWrapHelper.Wrap(hint, 96))
                    {
                        lines.Add($"  - {wrapped}");
                    }
                }
            }

            lines.Add(string.Empty);
        }

        if (report.DetailedAnalyzerSections is { Count: > 0 })
        {
            IReadOnlyList<DetailedAnalyzerSection> detailedSections = report.DetailedAnalyzerSections;

            lines.Add("DETAILED ANALYZER SECTIONS");
            lines.Add(new string('=', 100));
            foreach (DetailedAnalyzerSection detail in detailedSections)
            {
                lines.Add($"[{detail.Title}]");
                lines.Add(detail.Content);
                lines.Add(string.Empty);
            }
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class MarkdownCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Markdown;

    public string Render(ComposedReport report)
    {
        string reportTitle = report.IsTrendReport ? "# DumpDetective Trend Analysis Report" : "# DumpDetective Analysis Report";
        string dumpLabel = report.IsTrendReport ? "Latest dump" : "Dump";

        List<string> lines =
        [
            reportTitle,
            string.Empty,
            $"> {dumpLabel}: `{report.DumpPath}`  ",
            $"> Generated (UTC): `{report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}`  ",
            $"> Elapsed: `{report.Elapsed.TotalSeconds:F1}s`",
            string.Empty,
            $"> Dedup merged **{report.DedupDiagnostics.MergedSections}** section(s) from **{report.DedupDiagnostics.DuplicateCandidates}** candidate duplicate(s).",
            string.Empty
        ];

        if (report.IsTrendReport)
        {
            lines.Add($"> Dumps analyzed: **{report.TrendDumpCount}**");
            if (report.TrendDumpPaths is { Count: > 0 })
            {
                lines.Add("> Analyzed dumps:");
                foreach (string dumpPath in report.TrendDumpPaths)
                {
                    lines.Add($"> - `{dumpPath}`");
                }
            }
            lines.Add(string.Empty);
        }

        if (report.ExecutiveSummary.Count > 0)
        {
            lines.Add("## Executive Summary");
            lines.Add(string.Empty);
            lines.Add("| Signal | Value |");
            lines.Add("|---|---|");
            foreach (ExecutiveSummaryItem item in report.ExecutiveSummary)
            {
                string escapedValue = item.Value.Replace("|", "\\|");
                lines.Add($"| **{item.Label}** | {escapedValue} |");
            }
            lines.Add(string.Empty);
        }

        if (report.DeveloperActionPlan.Count > 0)
        {
            lines.Add("## Developer Action Plan");
            lines.Add(string.Empty);
            lines.Add("| Priority | Title | Action | Impact |");
            lines.Add("|---|---|---|---|");
            foreach (DeveloperActionItem action in report.DeveloperActionPlan)
            {
                string escapedTitle = action.Title.Replace("|", "\\|");
                string escapedAction = action.Action.Replace("|", "\\|");
                string escapedImpact = action.Impact.Replace("|", "\\|");
                lines.Add($"| {action.Priority} | {escapedTitle} | {escapedAction} | {escapedImpact} |");
            }
            lines.Add(string.Empty);
        }

        foreach (ReportSection section in report.Sections)
        {
            lines.Add($"## [{section.Severity}] {section.Title}");
            lines.Add(string.Empty);
            lines.Add(section.NarrativeSummary);
            lines.Add(string.Empty);
            lines.Add("| Label | Value | ");
            lines.Add("|---|---|");
            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(row.Value, 72).Select(v => v.Replace("|", "\\|")));
                lines.Add($"| {row.Label.Replace("|", "\\|")} | {wrapped} |");
            }

            if (section.RemediationHints.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("**Remediation**");
                foreach (string hint in section.RemediationHints)
                {
                    foreach (string wrapped in TableWrapHelper.Wrap(hint, 96))
                    {
                        lines.Add($"- {wrapped}");
                    }
                }
            }

            lines.Add(string.Empty);
        }

        if (report.DetailedAnalyzerSections is { Count: > 0 })
        {
            IReadOnlyList<DetailedAnalyzerSection> detailedSections = report.DetailedAnalyzerSections;

            lines.Add("## Detailed analyzer sections");
            lines.Add(string.Empty);
            foreach (DetailedAnalyzerSection detail in detailedSections)
            {
                lines.Add($"### {detail.Title}");
                lines.Add(string.Empty);
                lines.Add("```text");
                lines.Add(detail.Content);
                lines.Add("```");
                lines.Add(string.Empty);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class HtmlCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Html;

    public string Render(ComposedReport report)
    {
        static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);
        static string WrapAddresses(string html) =>
            Regex.Replace(html, @"0x[0-9A-Fa-f]{4,}",
                m => $"<span class=\"addr\">{m.Value}<button class=\"copy-btn\" type=\"button\" aria-label=\"Copy {m.Value}\" data-copy=\"{m.Value}\" title=\"Copy to clipboard\">&#x2398;</button></span>",
                RegexOptions.CultureInvariant);
        static string RenderDetailedContentHtml(DetailedAnalyzerSection section)
        {
            List<string> rendered = [];

            if (section.Submodules is { Count: > 0 })
            {
                foreach (DetailedAnalyzerSubmodule submodule in section.Submodules)
                {
                    string indentClass = submodule.IndentLevel >= 2
                        ? " detail-indent-2"
                        : submodule.IndentLevel == 1
                            ? " detail-indent-1"
                            : string.Empty;

                    switch (submodule.Kind)
                    {
                        case DetailedAnalyzerSubmoduleKind.Heading:
                            rendered.Add($"<div class=\"detail-subheading{indentClass}\">{Encode(submodule.Text ?? string.Empty)}</div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Metric:
                            rendered.Add($"<div class=\"detail-line{indentClass}\"><span class=\"detail-key\">{Encode(submodule.Label ?? string.Empty)}:</span> <span class=\"detail-value wrap\">{WrapAddresses(Encode(submodule.Value ?? string.Empty))}</span></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Path:
                            rendered.Add($"<div class=\"detail-line{indentClass}\"><span class=\"detail-key\">{Encode(submodule.Label ?? string.Empty)}:</span> <span class=\"detail-path wrap\">{WrapAddresses(Encode(submodule.Value ?? string.Empty))}</span></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.ListItem:
                            rendered.Add($"<div class=\"detail-line{indentClass}\">• {WrapAddresses(Encode(submodule.Text ?? string.Empty))}</div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Divider:
                            rendered.Add("<div class=\"detail-divider\"></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Empty:
                            rendered.Add("<div class=\"detail-gap\"></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.SectionBegin:
                            rendered.Add($"<details class=\"detail-nested\"><summary>{Encode(submodule.Text ?? string.Empty)}</summary><div class=\"detail-nested-content\">");
                            break;
                        case DetailedAnalyzerSubmoduleKind.SectionEnd:
                            rendered.Add("</div></details>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Table when submodule.TableData is { } td:
                        {
                            System.Text.StringBuilder tableSb = new();
                            tableSb.Append("<table>");
                            if (td.Caption is not null)
                                tableSb.Append($"<caption>{Encode(td.Caption)}</caption>");
                            tableSb.Append("<thead><tr>");
                            foreach (string header in td.Headers)
                                tableSb.Append($"<th scope=\"col\">{Encode(header)}</th>");
                            tableSb.Append("</tr></thead><tbody>");
                            foreach (DetailedAnalyzerTableRow row in td.Rows)
                            {
                                tableSb.Append("<tr>");
                                foreach (DetailedAnalyzerTableCell cell in row.Cells)
                                {
                                    string dataAttr = cell.RawValue.HasValue ? $" data-value=\"{cell.RawValue.Value}\"" : string.Empty;
                                    tableSb.Append($"<td{dataAttr}>{WrapAddresses(Encode(cell.Display))}</td>");
                                }
                                tableSb.Append("</tr>");
                            }
                            tableSb.Append("</tbody></table>");
                            rendered.Add(tableSb.ToString());
                            break;
                        }
                        default:
                            rendered.Add($"<div class=\"detail-line{indentClass}\">{WrapAddresses(Encode(submodule.Text ?? string.Empty))}</div>");
                            break;
                    }
                }
            }
            else
            {
                string[] lines = section.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        rendered.Add("<div class=\"detail-gap\"></div>");
                        continue;
                    }

                    int leadingWhitespace = line.TakeWhile(char.IsWhiteSpace).Count();
                    string indentClass = leadingWhitespace >= 4
                        ? " detail-indent-2"
                        : leadingWhitespace >= 2
                            ? " detail-indent-1"
                            : string.Empty;

                    rendered.Add($"<div class=\"detail-line{indentClass}\">{WrapAddresses(Encode(line.TrimStart()))}</div>");
                }
            }

            return string.Join(Environment.NewLine, rendered);
        }

        string reportTitle = report.IsTrendReport ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = report.IsTrendReport ? "Latest dump" : "Dump";
        string exportFileName = Encode(System.IO.Path.GetFileNameWithoutExtension(report.DumpPath));

        List<string> lines =
        [
            "<!DOCTYPE html>",
            "<html lang=\"en\">",
            "<head>",
            "<meta charset=\"utf-8\" />",
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />",
            $"<title>{Encode(reportTitle)}</title>",
            "<style>",
            "body{margin:0;padding:0;background:#f5f7fb;color:#1f2937;font-family:Segoe UI,Arial,sans-serif;line-height:1.45;}",
            ".container{max-width:1200px;margin:0 auto;padding:24px;}",
            ".header-card,.section-card{background:#ffffff;border:1px solid #e5e7eb;border-radius:10px;box-shadow:0 1px 2px rgba(0,0,0,.05);}",
            ".header-card{padding:16px 18px;margin-bottom:16px;}",
            ".meta-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:10px 16px;margin-top:10px;}",
            ".meta-item{font-size:14px;}",
            ".meta-label{font-weight:600;color:#374151;}",
            ".dedup-note{margin-top:10px;padding:10px 12px;border-radius:8px;background:#eff6ff;color:#1d4ed8;font-size:14px;}",
            ".section-card{padding:14px 16px;margin-bottom:14px;}",
            ".section-header{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:6px;}",
            ".severity-badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.02em;text-transform:uppercase;}",
            ".severity-critical{background:#fee2e2;color:#b91c1c;}",
            ".severity-warning{background:#fef3c7;color:#92400e;}",
            ".severity-info{background:#dbeafe;color:#1e3a8a;}",
            ".category{font-size:12px;color:#6b7280;background:#f3f4f6;padding:2px 8px;border-radius:999px;}",
            ".summary{margin:8px 0 10px 0;}",
            "table{border-collapse:collapse;width:100%;background:#ffffff;}",
            "thead th{background:#f9fafb;font-weight:600;border:1px solid #e5e7eb;padding:8px;text-align:left;}",
            "tbody td{border:1px solid #e5e7eb;padding:8px;vertical-align:top;}",
            "tbody tr:nth-child(even){background:#fcfcfd;}",
            ".wrap{overflow-wrap:anywhere;word-break:break-word;}",
            ".remediation-title{margin:12px 0 6px 0;font-size:15px;}",
            ".remediation-list{margin:0;padding-left:20px;}",
            ".analyzer-section{background:#fff;border:1px solid #e2e8f0;border-left:4px solid #3b82f6;border-radius:10px;margin-bottom:14px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.07);}",
            ".detail-color-0{border-left-color:#3b82f6;}",
            ".detail-color-1{border-left-color:#7c3aed;}",
            ".detail-color-2{border-left-color:#0891b2;}",
            ".detail-color-3{border-left-color:#059669;}",
            ".detail-color-4{border-left-color:#d97706;}",
            ".detail-color-5{border-left-color:#e11d48;}",
            ".analyzer-section>details>summary{display:flex;align-items:center;gap:10px;padding:13px 16px;background:transparent;font-weight:600;font-size:14px;color:#1e293b;cursor:pointer;list-style:none;user-select:none;}",
            ".analyzer-section>details>summary::-webkit-details-marker{display:none;}",
            ".analyzer-section>details>summary:hover{background:rgba(0,0,0,0.02);}",
            ".analyzer-section>details[open]>summary{background:rgba(0,0,0,0.02);border-bottom:1px solid #e2e8f0;}",
            ".analyzer-section>details>summary::before{content:'';flex-shrink:0;display:inline-block;width:8px;height:8px;border-right:2px solid #94a3b8;border-bottom:2px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;}",
            ".analyzer-section>details[open]>summary::before{transform:rotate(45deg) translate(-2px,-2px);border-color:#3b82f6;}",
            ".detail-color-1>details[open]>summary::before{border-color:#7c3aed;}",
            ".detail-color-2>details[open]>summary::before{border-color:#0891b2;}",
            ".detail-color-3>details[open]>summary::before{border-color:#059669;}",
            ".detail-color-4>details[open]>summary::before{border-color:#d97706;}",
            ".detail-color-5>details[open]>summary::before{border-color:#e11d48;}",
            ".analyzer-section .detail-block{border-radius:0 0 6px 6px;margin:0;}",
            ".detail-block{background:#f8fafc;color:#1f2937;border-radius:8px;padding:12px;overflow:auto;font-family:Consolas,\"Cascadia Mono\",monospace;font-size:13px;line-height:1.5;}",
            ".detail-subheading{font-weight:700;color:#1d4ed8;margin:8px 0 4px 0;}",
            ".detail-divider{height:1px;background:#e2e8f0;margin:6px 0;}",
            ".detail-line{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;}",
            ".detail-key{color:#059669;font-weight:600;}",
            ".detail-value{color:#374151;}",
            ".detail-path{color:#b45309;font-weight:600;}",
            ".detail-gap{height:8px;}",
            ".detail-indent-1{padding-left:12px;}",
            ".detail-indent-2{padding-left:24px;}",
            ".detail-block table{background:transparent;border-collapse:collapse;width:100%;margin:8px 0;color:#1f2937;}",
            ".detail-block thead th{background:#f1f5f9;color:#1e293b;font-weight:600;border:1px solid #e2e8f0;padding:6px 8px;text-align:left;}",
            ".detail-block tbody td{border:1px solid #e2e8f0;padding:5px 8px;vertical-align:top;overflow-wrap:anywhere;word-break:break-word;}",
            ".detail-block tbody tr:nth-child(even){background:rgba(0,0,0,0.02);}",
            ".detail-block caption{color:#6b7280;font-size:13px;font-weight:600;text-align:left;padding:2px 0 4px 0;caption-side:top;}",
            ".detail-nested{margin:6px 0;border:1px solid #e2e8f0;border-radius:6px;overflow:hidden;}",
            ".detail-nested>summary{display:flex;align-items:center;gap:8px;padding:8px 10px;background:transparent;color:#374151;font-weight:600;font-size:13px;cursor:pointer;list-style:none;user-select:none;}",
            ".detail-nested>summary::-webkit-details-marker{display:none;}",
            ".detail-nested>summary:hover{background:rgba(0,0,0,0.03);}",
            ".detail-nested[open]>summary{background:rgba(0,0,0,0.03);border-bottom:1px solid #e2e8f0;}",
            ".detail-nested>summary::before{content:'';flex-shrink:0;display:inline-block;width:7px;height:7px;border-right:1.5px solid #94a3b8;border-bottom:1.5px solid #94a3b8;transform:rotate(-45deg);transition:transform 0.2s;margin-bottom:1px;}",
            ".detail-nested[open]>summary::before{transform:rotate(45deg) translate(-1px,-1px);border-color:#3b82f6;}",
            ".detail-nested-content{padding:8px 4px;}",
            "nav.toc{background:#ffffff;border:1px solid #e5e7eb;border-radius:10px;box-shadow:0 1px 2px rgba(0,0,0,.05);padding:16px 18px;margin-bottom:16px;}",
            "nav.toc h2{margin:0 0 10px 0;font-size:16px;color:#1f2937;}",
            "nav.toc ol{margin:0;padding-left:20px;column-count:auto;column-width:280px;column-gap:24px;}",
            "nav.toc li{margin:4px 0;font-size:14px;}",
            "nav.toc a{color:#1d4ed8;text-decoration:none;}",
            "nav.toc a:hover{text-decoration:underline;}",
            ".toc-badge{display:inline-block;padding:1px 6px;border-radius:999px;font-size:11px;font-weight:700;text-transform:uppercase;margin-left:5px;vertical-align:middle;}",
            ".skip-link{position:absolute;left:-9999px;top:8px;z-index:999;padding:8px 16px;background:#1d4ed8;color:#fff;border-radius:6px;font-weight:600;text-decoration:none;white-space:nowrap;}",
            ".skip-link:focus{left:8px;}",
            ":focus-visible{outline:2px solid #2563eb;outline-offset:2px;border-radius:2px;}",
            ".analyzer-section>details>summary:focus-visible{outline:2px solid #2563eb;outline-offset:-2px;border-radius:0;}",
            "a:focus-visible{outline:2px solid #2563eb;outline-offset:2px;}",
            ".sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;}",
            ".copy-btn{border:none;background:none;cursor:pointer;color:#64748b;font-size:11px;padding:1px 3px;border-radius:3px;vertical-align:middle;margin-left:3px;transition:background 0.15s,color 0.15s;line-height:1;}",
            ".copy-btn:hover{background:#eff6ff;color:#1d4ed8;}",
            ".copy-btn:focus-visible{outline:2px solid #2563eb;outline-offset:1px;}",
            ".permalink-btn{opacity:0;margin-left:8px;color:#94a3b8;font-size:0.8em;text-decoration:none;vertical-align:middle;transition:opacity 0.15s;}",
            "h2:hover .permalink-btn,.permalink-btn:focus{opacity:1;color:#3b82f6;}",
            ".addr{white-space:nowrap;display:inline;}",
            ".detail-block .copy-btn{color:#64748b;background:rgba(0,0,0,0.04);}",
            ".detail-block .copy-btn:hover{background:rgba(0,0,0,0.08);color:#1d4ed8;}",
            ".filter-bar{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px 0 6px 0;margin-bottom:6px;}",
            ".filter-group{display:flex;gap:4px;flex-wrap:wrap;}",
            ".filter-btn{padding:3px 12px;border:1px solid #e2e8f0;border-radius:20px;background:#fff;color:#374151;font-size:12px;font-weight:600;cursor:pointer;transition:all 0.15s;white-space:nowrap;}",
            ".filter-btn:hover{background:#f1f5f9;border-color:#94a3b8;}",
            ".filter-btn.active{background:#1d4ed8;color:#fff;border-color:#1d4ed8;}",
            ".filter-btn.filter-critical.active{background:#b91c1c;border-color:#b91c1c;}",
            ".filter-btn.filter-warning.active{background:#92400e;border-color:#b45309;}",
            ".filter-btn:focus-visible{outline:2px solid #2563eb;outline-offset:2px;}",
            ".filter-search{flex:1;min-width:180px;max-width:360px;padding:5px 10px;border:1px solid #e2e8f0;border-radius:6px;font-size:14px;color:#1f2937;background:#fff;transition:border-color 0.15s;}",
            ".filter-search:focus{outline:none;border-color:#2563eb;box-shadow:0 0 0 3px rgba(37,99,235,.12);}",
            ".filter-count{font-size:12px;color:#6b7280;white-space:nowrap;padding:0 4px;}",
            "thead th.sortable{cursor:pointer;user-select:none;}",
            "thead th.sortable:hover{background:#e9ebef;}",
            "thead th.sortable::after{content:' \u21c5';font-size:11px;opacity:0.35;margin-left:3px;}",
            "thead th.sortable[aria-sort=\"ascending\"]::after{content:' \u2191';opacity:1;}",
            "thead th.sortable[aria-sort=\"descending\"]::after{content:' \u2193';opacity:1;}",
            ".detail-block thead th.sortable::after{content:none;}",
            ".action-bar{display:flex;gap:8px;justify-content:flex-end;margin-top:12px;flex-wrap:wrap;}",
            ".action-btn{display:inline-flex;align-items:center;gap:5px;padding:6px 14px;border:1px solid #e2e8f0;border-radius:6px;background:#fff;color:#374151;font-size:13px;font-weight:500;cursor:pointer;transition:background 0.15s,border-color 0.15s;}",
            ".action-btn:hover{background:#f1f5f9;border-color:#94a3b8;color:#1e293b;}",
            ".action-btn:focus-visible{outline:2px solid #2563eb;outline-offset:2px;}",
            "@media print{",
            ".skip-link,.action-bar,.filter-bar,.copy-btn,.permalink-btn,nav.toc{display:none!important;}",
            "body{background:#fff;}",
            ".header-card,.section-card,.analyzer-section{box-shadow:none!important;border:1px solid #d1d5db!important;page-break-inside:avoid;}",
            ".analyzer-section>details{display:block!important;}",
            ".analyzer-section>details>summary{list-style:none;background:#f0f4f8!important;border-bottom:1px solid #d1d5db;}",
            ".detail-block{border:1px solid #e2e8f0!important;}",
            ".detail-divider{background:#e2e8f0!important;}",
            ".severity-critical{background:#fee2e2!important;color:#b91c1c!important;}",
            ".severity-warning{background:#fef3c7!important;color:#92400e!important;}",
            ".severity-info{background:#dbeafe!important;color:#1e3a8a!important;}",
            "}",
            "</style>",
            "</head>",
            "<body>",
            "<a href=\"#main-content\" class=\"skip-link\">Skip to main content</a>",
            "<div role=\"status\" aria-live=\"polite\" aria-atomic=\"true\" id=\"clipboard-status\" class=\"sr-only\"></div>",
            "<main class=\"container\" id=\"main-content\" tabindex=\"-1\">",
            "<section id=\"section-header\" class=\"header-card\" aria-labelledby=\"section-header-heading\">",
            $"<h1 id=\"section-header-heading\">{Encode(reportTitle)}</h1>",
            "<div class=\"meta-grid\">",
            $"<div class=\"meta-item\"><span class=\"meta-label\">{Encode(dumpLabel)}:</span> <span class=\"wrap\">{Encode(report.DumpPath)}</span></div>",
            $"<div class=\"meta-item\"><span class=\"meta-label\">Generated (UTC):</span> <time datetime=\"{report.GeneratedAtUtc:yyyy-MM-ddTHH:mm:ssZ}\">{report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</time></div>",
            $"<div class=\"meta-item\"><span class=\"meta-label\">Elapsed:</span> <span data-value=\"{report.Elapsed.TotalSeconds:F3}\">{report.Elapsed.TotalSeconds:F1}s</span></div>",
            "</div>",
            $"<div class=\"dedup-note\"><strong>Dedup:</strong> merged <span data-value=\"{report.DedupDiagnostics.MergedSections}\">{report.DedupDiagnostics.MergedSections}</span>/<span data-value=\"{report.DedupDiagnostics.DuplicateCandidates}\">{report.DedupDiagnostics.DuplicateCandidates}</span></div>",
            $"<div class=\"action-bar\" role=\"toolbar\" aria-label=\"Report actions\">" +
             $"<button type=\"button\" class=\"action-btn\" id=\"btn-download-json\" data-filename=\"{exportFileName}\" aria-label=\"Download report as JSON\">\u2B07 JSON</button>" +
             $"<button type=\"button\" class=\"action-btn\" id=\"btn-export-csv\" data-filename=\"{exportFileName}\" aria-label=\"Export findings as CSV\">\u2B07 CSV</button>" +
             $"<button type=\"button\" class=\"action-btn\" id=\"btn-print\" aria-label=\"Print this report\">\u2399 Print</button>" +
             "</div>",
            "</section>"
        ];

        if (report.IsTrendReport)
        {
            lines.Insert(lines.Count - 1, $"<div class=\"dedup-note\"><strong>Dumps analyzed:</strong> {report.TrendDumpCount}</div>");
            if (report.TrendDumpPaths is { Count: > 0 })
            {
                string dumpList = string.Join("<br/>", report.TrendDumpPaths.Select(p => $"• {Encode(p)}"));
                lines.Insert(lines.Count - 1, $"<div class=\"dedup-note\"><strong>Analyzed dumps:</strong><br/>{dumpList}</div>");
            }
        }

        // Build Table of Contents nav
        {
            List<string> tocItems = [];
            if (report.ExecutiveSummary.Count > 0)
                tocItems.Add("<li><a href=\"#section-executive-summary\">Executive Summary</a></li>");
            if (report.DeveloperActionPlan.Count > 0)
                tocItems.Add("<li><a href=\"#section-developer-action-plan\">Developer Action Plan</a></li>");
            for (int i = 0; i < report.Sections.Count; i++)
            {
                ReportSection s = report.Sections[i];
                string severityCssForToc = s.Severity switch
                {
                    Core.Models.FindingSeverity.Critical => "severity-critical",
                    Core.Models.FindingSeverity.Warning => "severity-warning",
                    _ => "severity-info"
                };
                tocItems.Add($"<li><a href=\"#finding-{i}\">{Encode(s.Title)}</a><span class=\"toc-badge {severityCssForToc}\">{Encode(s.Severity.ToString())}</span></li>");
            }
            if (report.DetailedAnalyzerSections is { Count: > 0 })
            {
                foreach ((DetailedAnalyzerSection detail, int di) in report.DetailedAnalyzerSections.Select((d, i) => (d, i)))
                    tocItems.Add($"<li><a href=\"#detail-{di}\">{Encode(detail.Title)}</a></li>");
            }

            if (tocItems.Count > 0)
            {
                lines.Add("<nav class=\"toc\" aria-labelledby=\"toc-heading\">");
                lines.Add("<h2 id=\"toc-heading\">Contents</h2>");
                lines.Add("<ol>");
                lines.AddRange(tocItems);
                lines.Add("</ol>");
                lines.Add("</nav>");
            }
        }

        // Severity counts shared by filter bar (point 11) and report-meta (point 12)
        int critCount = report.Sections.Count(s => s.Severity == Core.Models.FindingSeverity.Critical);
        int warnCount = report.Sections.Count(s => s.Severity == Core.Models.FindingSeverity.Warning);
        int infoCount = report.Sections.Count - critCount - warnCount;

        // Point 11: filter bar — only rendered when there are finding sections to filter
        if (report.Sections.Count > 0)
        {
            lines.Add("<div class=\"filter-bar\" id=\"filter-bar\" role=\"search\" aria-label=\"Filter findings\">");
            lines.Add("<div class=\"filter-group\" aria-label=\"Severity filter\">");
            lines.Add($"<button class=\"filter-btn active\" data-sev=\"all\" aria-pressed=\"true\" type=\"button\">All ({report.Sections.Count})</button>");
            if (critCount > 0) lines.Add($"<button class=\"filter-btn filter-critical\" data-sev=\"critical\" aria-pressed=\"false\" type=\"button\">Critical ({critCount})</button>");
            if (warnCount > 0) lines.Add($"<button class=\"filter-btn filter-warning\" data-sev=\"warning\" aria-pressed=\"false\" type=\"button\">Warning ({warnCount})</button>");
            if (infoCount > 0) lines.Add($"<button class=\"filter-btn filter-info\" data-sev=\"info\" aria-pressed=\"false\" type=\"button\">Info ({infoCount})</button>");
            lines.Add("</div>");
            lines.Add("<input type=\"search\" id=\"filter-search\" class=\"filter-search\" placeholder=\"Search findings\u2026\" aria-label=\"Search findings by title or summary\" />");
            lines.Add("<span id=\"filter-count\" class=\"filter-count\" aria-live=\"polite\" aria-atomic=\"true\"></span>");
            lines.Add("</div>");
        }

        if (report.ExecutiveSummary.Count > 0)
        {
            lines.Add("<section id=\"section-executive-summary\" class=\"section-card\" aria-labelledby=\"section-executive-summary-heading\">");
            lines.Add("<h2 id=\"section-executive-summary-heading\">Executive summary<a href=\"#section-executive-summary\" class=\"permalink-btn\" aria-label=\"Permalink to Executive summary\" title=\"Copy link\">\u00b6</a></h2>");
            lines.Add("<table><thead><tr><th scope=\"col\">Signal</th><th scope=\"col\">Value</th></tr></thead><tbody>");
            foreach (ExecutiveSummaryItem item in report.ExecutiveSummary)
            {
                lines.Add($"<tr><td>{Encode(item.Label)}</td><td class=\"wrap\">{Encode(item.Value)}</td></tr>");
            }
            lines.Add("</tbody></table>");
            lines.Add("</section>");
        }

        if (report.DeveloperActionPlan.Count > 0)
        {
            lines.Add("<section id=\"section-developer-action-plan\" class=\"section-card\" aria-labelledby=\"section-developer-action-plan-heading\">");
            lines.Add("<h2 id=\"section-developer-action-plan-heading\">Developer action plan<a href=\"#section-developer-action-plan\" class=\"permalink-btn\" aria-label=\"Permalink to Developer action plan\" title=\"Copy link\">\u00b6</a></h2>");
            lines.Add("<table><thead><tr><th scope=\"col\">Priority</th><th scope=\"col\">Title</th><th scope=\"col\">Action</th><th scope=\"col\">Impact</th></tr></thead><tbody>");
            foreach (DeveloperActionItem action in report.DeveloperActionPlan)
            {
                lines.Add($"<tr><td>{Encode(action.Priority)}</td><td>{Encode(action.Title)}</td><td class=\"wrap\">{Encode(action.Action)}</td><td class=\"wrap\">{Encode(action.Impact)}</td></tr>");
            }
            lines.Add("</tbody></table>");
            lines.Add("</section>");
        }

        for (int sectionIndex = 0; sectionIndex < report.Sections.Count; sectionIndex++)
        {
            ReportSection section = report.Sections[sectionIndex];
            string severityCss = section.Severity switch
            {
                Core.Models.FindingSeverity.Critical => "severity-critical",
                Core.Models.FindingSeverity.Warning => "severity-warning",
                _ => "severity-info"
            };

            lines.Add($"<section id=\"finding-{sectionIndex}\" class=\"section-card\" aria-labelledby=\"finding-{sectionIndex}-heading\" data-severity=\"{Encode(section.Severity.ToString().ToLowerInvariant())}\" data-category=\"{Encode(section.Category)}\" data-title=\"{Encode(section.Title)}\" data-summary=\"{Encode(section.NarrativeSummary.Length > 200 ? section.NarrativeSummary[..200] : section.NarrativeSummary)}\">");
            lines.Add("<div class=\"section-header\">");
            lines.Add($"<span class=\"severity-badge {severityCss}\">{Encode(section.Severity.ToString())}</span>");
            lines.Add($"<h2 id=\"finding-{sectionIndex}-heading\">{Encode(section.Title)}<a href=\"#finding-{sectionIndex}\" class=\"permalink-btn\" aria-label=\"Permalink to {Encode(section.Title)}\" title=\"Copy link\">\u00b6</a></h2>");
            lines.Add($"<span class=\"category\">{Encode(section.Category)}</span>");
            lines.Add("</div>");
            lines.Add($"<p class=\"summary\">{Encode(section.NarrativeSummary)}</p>");
            lines.Add("<table>");
            lines.Add("<thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead>");
            lines.Add("<tbody>");
            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(row.Value, 90).Select(v => WrapAddresses(Encode(v))));
                lines.Add($"<tr><td>{Encode(row.Label)}</td><td class=\"wrap\">{wrapped}</td></tr>");
            }
            lines.Add("</tbody>");
            lines.Add("</table>");

            if (section.RemediationHints.Count > 0)
            {
                lines.Add("<h3 class=\"remediation-title\">Remediation</h3><ul class=\"remediation-list\">");
                foreach (string hint in section.RemediationHints)
                {
                    string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(hint, 96).Select(Encode));
                    lines.Add($"<li class=\"wrap\">{wrapped}</li>");
                }
                lines.Add("</ul>");
            }

            lines.Add("</section>");
        }

        if (report.DetailedAnalyzerSections is { Count: > 0 })
        {
            IReadOnlyList<DetailedAnalyzerSection> detailedSections = report.DetailedAnalyzerSections;

            for (int i = 0; i < detailedSections.Count; i++)
            {
                DetailedAnalyzerSection detail = detailedSections[i];
                string colorClass = $"detail-color-{i % 6}";
                lines.Add($"<section id=\"detail-{i}\" class=\"analyzer-section {colorClass}\" aria-labelledby=\"detail-{i}-heading\">");
                lines.Add("<details>");
                lines.Add($"<summary id=\"detail-{i}-heading\" aria-controls=\"detail-{i}-content\" aria-expanded=\"false\">{Encode(detail.Title)}</summary>");
                lines.Add($"<div id=\"detail-{i}-content\" class=\"detail-block\" role=\"region\" aria-labelledby=\"detail-{i}-heading\">{RenderDetailedContentHtml(detail)}</div>");
                lines.Add("</details>");
                lines.Add("</section>");
            }
        }

        // Inline JS: aria-expanded sync + copy-to-clipboard + permalink actions (point 7 + point 8) + export/print (point 10)
        // TODO: [WORKAROUND] Global CSV export only covers report.Sections (finding cards).
        // Now that analyzerSections is also in #report-data, the CSV should be redesigned to either:
        //   (a) export per-analyzer metric tables as separate CSV sheets, or
        //   (b) produce a flat metrics CSV from analyzerSections[*].metrics + analyzerSections[*].tables, or
        //   (c) remove the CSV button entirely if a richer export format (e.g., XLSX) is preferred.
        // For now the button is retained but only exports findings rows, which will be empty when
        // the analysis produces no flagged findings.  Remove or replace this before shipping to users.
        //
        // TODO: [FIX] Print button calls window.print() directly but the @media print stylesheet
        // currently hides action-bar and copy/permalink buttons via display:none. Verify that
        // window.print() triggers the correct @media print rules in all target browsers (Chrome,
        // Edge, Firefox).  Also confirm that details[open] forced-expansion in @media print works
        // for sections that were never opened by the user (they may still be collapsed in the
        // print preview).  Consider using a beforeprint event to programmatically open all
        // <details> elements and restore them afterprint instead of relying solely on CSS.
        lines.Add("<script>(function(){" +
            "document.querySelectorAll('.analyzer-section details').forEach(function(d){var s=d.querySelector('summary');s.setAttribute('aria-expanded',d.open);d.addEventListener('toggle',function(){s.setAttribute('aria-expanded',d.open);});});" +
            "var sr=document.getElementById('clipboard-status');" +
            "function flash(m){if(sr){sr.textContent=m;setTimeout(function(){sr.textContent='';},2000);}}" +
            "document.querySelectorAll('.copy-btn').forEach(function(btn){btn.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();if(navigator.clipboard)navigator.clipboard.writeText(btn.dataset.copy||'').then(function(){flash('Copied: '+btn.dataset.copy);});});});" +
            "document.querySelectorAll('.permalink-btn').forEach(function(a){a.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();var u=(window.location.href.split('#')[0])+(a.getAttribute('href')||'');if(navigator.clipboard)navigator.clipboard.writeText(u).then(function(){flash('Link copied');});});});" +
            "var btnJson=document.getElementById('btn-download-json');" +
            "if(btnJson)btnJson.addEventListener('click',function(){var el=document.getElementById('report-data');if(!el)return;" +
            "var blob=new Blob([el.textContent],{type:'application/json'});var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=(btnJson.dataset.filename||'report')+'.json';a.click();URL.revokeObjectURL(a.href);});" +
            "var btnCsv=document.getElementById('btn-export-csv');" +
            "if(btnCsv)btnCsv.addEventListener('click',function(){var el=document.getElementById('report-data');if(!el)return;" +
            "try{var d=JSON.parse(el.textContent);var rows=[['ID','Severity','Category','Title','Summary','Remediation']];" +
            "(d.sections||[]).forEach(function(s){rows.push([s.id,s.severity||'',s.category||'',s.title||'',s.summary||'',(s.remediation||[]).join('; ')]);});" +
            "var csv=rows.map(function(r){return r.map(function(c){return'\"'+(c||'').replace(/\"/g,'\"\"')+'\"';}).join(',');}).join('\\r\\n');" +
            "var blob=new Blob(['\uFEFF'+csv],{type:'text/csv;charset=utf-8'});var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=(btnCsv.dataset.filename||'findings')+'-findings.csv';a.click();URL.revokeObjectURL(a.href);}catch(e){}});" +
            "var btnPrint=document.getElementById('btn-print');if(btnPrint)btnPrint.addEventListener('click',function(){window.print();});" +
            // Point 11: findings filter (severity pills + text search)
            "var fbs=document.querySelectorAll('.filter-btn[data-sev]');" +
            "var fsi=document.getElementById('filter-search');" +
            "var fco=document.getElementById('filter-count');" +
            "function applyFilter(){" +
            "  var txt=fsi?fsi.value.trim().toLowerCase():'';" +
            "  var asev='all';" +
            "  fbs.forEach(function(b){if(b.classList.contains('active'))asev=b.dataset.sev;});" +
            "  var cards=document.querySelectorAll('.section-card[data-severity]');" +
            "  var vis=0;" +
            "  cards.forEach(function(c){" +
            "    var s=(c.dataset.severity||'').toLowerCase();" +
            "    var ok=(asev==='all'||s===asev)&&(!txt||(c.dataset.title||'').toLowerCase().indexOf(txt)>=0||(c.dataset.summary||'').toLowerCase().indexOf(txt)>=0);" +
            "    c.hidden=!ok;if(ok)vis++;" +
            "  });" +
            "  if(fco)fco.textContent=cards.length?vis+' of '+cards.length+' finding(s)':'';" +
            "}" +
            "fbs.forEach(function(b){b.addEventListener('click',function(){" +
            "  fbs.forEach(function(x){x.classList.remove('active');x.setAttribute('aria-pressed','false');});" +
            "  b.classList.add('active');b.setAttribute('aria-pressed','true');applyFilter();" +
            "});});" +
            "if(fsi)fsi.addEventListener('input',applyFilter);" +
            "applyFilter();" +
            // Point 11: sortable table columns (numeric data-value aware, keyboard accessible)
            "document.querySelectorAll('table').forEach(function(tbl){" +
            "  var ths=tbl.querySelectorAll('thead th');" +
            "  ths.forEach(function(th,col){" +
            "    th.classList.add('sortable');th.setAttribute('tabindex','0');" +
            "    var dir=1;" +
            "    function doSort(){" +
            "      var tb=tbl.querySelector('tbody');if(!tb)return;" +
            "      var rows=Array.from(tb.querySelectorAll('tr'));" +
            "      rows.sort(function(a,b){" +
            "        var ac=a.cells[col],bc=b.cells[col];" +
            "        var av=ac&&ac.dataset.value!==undefined&&ac.dataset.value!==''?parseFloat(ac.dataset.value):NaN;" +
            "        var bv=bc&&bc.dataset.value!==undefined&&bc.dataset.value!==''?parseFloat(bc.dataset.value):NaN;" +
            "        if(!isNaN(av)&&!isNaN(bv))return dir*(av-bv);" +
            "        var at=(ac?ac.textContent:'').toLowerCase(),bt=(bc?bc.textContent:'').toLowerCase();" +
            "        return dir*(at<bt?-1:at>bt?1:0);" +
            "      });" +
            "      rows.forEach(function(r){tb.appendChild(r);});" +
            "      ths.forEach(function(h){h.removeAttribute('aria-sort');});" +
            "      th.setAttribute('aria-sort',dir>0?'ascending':'descending');dir=-dir;" +
            "    }" +
            "    th.addEventListener('click',doSort);" +
            "    th.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();doSort();}});" +
            "  });" +
            "});" +
            "})();</script>");

        // Point 4/12: machine-readable report-meta summary, then canonical report-data
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var reportMeta = new
        {
            generated = report.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            dumpPath = report.DumpPath,
            elapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 3),
            isTrendReport = report.IsTrendReport,
            findings = new
            {
                total = report.Sections.Count,
                critical = critCount,
                warning = warnCount,
                info = infoCount
            },
            analyzerSectionCount = report.DetailedAnalyzerSections?.Count ?? 0
        };
        lines.Add($"<script type=\"application/json\" id=\"report-meta\">{JsonSerializer.Serialize(reportMeta, jsonOptions)}</script>");

        // Point 4: embed canonical JSON for client-side consumption
        var reportData = new
        {
            schemaVersion = report.ReportSchemaVersion,
            dumpPath = report.DumpPath,
            generatedAtUtc = report.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            elapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 3),
            isTrendReport = report.IsTrendReport,
            trendDumpCount = report.IsTrendReport ? (int?)report.TrendDumpCount : null,
            trendDumpPaths = report.IsTrendReport ? report.TrendDumpPaths : null,
            dedup = new
            {
                mergedSections = report.DedupDiagnostics.MergedSections,
                duplicateCandidates = report.DedupDiagnostics.DuplicateCandidates
            },
            executiveSummary = report.ExecutiveSummary
                .Select(e => new { label = e.Label, value = e.Value }),
            developerActionPlan = report.DeveloperActionPlan
                .Select(a => new { priority = a.Priority, title = a.Title, action = a.Action, impact = a.Impact }),
            sections = report.Sections
                .Select((s, i) => new
                {
                    id = $"finding-{i}",
                    title = s.Title,
                    category = s.Category,
                    severity = s.Severity.ToString(),
                    summary = s.NarrativeSummary,
                    evidence = s.EvidenceRows.Select(r => new { label = r.Label, value = r.Value }),
                    remediation = s.RemediationHints
                }),
            analyzerSections = (report.DetailedAnalyzerSections ?? [])
                .Select((ds, i) => new
                {
                    id = $"detail-{i}",
                    title = ds.Title,
                    metrics = (ds.Submodules ?? [])
                        .Where(sm => sm.Kind == DetailedAnalyzerSubmoduleKind.Metric)
                        .Select(sm => new { label = sm.Label, value = sm.Value }),
                    tables = (ds.Submodules ?? [])
                        .Where(sm => sm.Kind == DetailedAnalyzerSubmoduleKind.Table && sm.TableData != null)
                        .Select(sm => new
                        {
                            caption = sm.TableData!.Caption,
                            headers = sm.TableData.Headers,
                            rows = sm.TableData.Rows.Select(r =>
                                r.Cells.Select(c => c.RawValue.HasValue
                                    ? (object)new { display = c.Display, rawValue = c.RawValue.Value }
                                    : new { display = c.Display, rawValue = (long?)null }))
                        })
                })
        };
        string reportDataJson = JsonSerializer.Serialize(reportData, jsonOptions);
        lines.Add($"<script type=\"application/json\" id=\"report-data\">{reportDataJson}</script>");

        lines.Add("</main>");
        lines.Add("</body></html>");
        return string.Join(Environment.NewLine, lines);
    }
}

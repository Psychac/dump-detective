using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;

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

        if (report.ExecutiveSummary.Count > 0)
        {
            lines.Add("## Executive summary");
            lines.Add(string.Empty);
            foreach (ExecutiveSummaryItem item in report.ExecutiveSummary)
            {
                lines.Add($"- **{item.Label}:** {item.Value}");
            }
            lines.Add(string.Empty);
        }

        if (report.DeveloperActionPlan.Count > 0)
        {
            lines.Add("## Developer action plan");
            lines.Add(string.Empty);
            foreach (DeveloperActionItem action in report.DeveloperActionPlan)
            {
                lines.Add($"- **{action.Priority}** {action.Title}");
                lines.Add($"  - Action: {action.Action}");
                lines.Add($"  - Impact: {action.Impact}");
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
                            rendered.Add($"<div class=\"detail-line{indentClass}\"><span class=\"detail-key\">{Encode(submodule.Label ?? string.Empty)}:</span> <span class=\"detail-value wrap\">{Encode(submodule.Value ?? string.Empty)}</span></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Path:
                            rendered.Add($"<div class=\"detail-line{indentClass}\"><span class=\"detail-key\">{Encode(submodule.Label ?? string.Empty)}:</span> <span class=\"detail-path wrap\">{Encode(submodule.Value ?? string.Empty)}</span></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.ListItem:
                            rendered.Add($"<div class=\"detail-line{indentClass}\">• {Encode(submodule.Text ?? string.Empty)}</div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Divider:
                            rendered.Add("<div class=\"detail-divider\"></div>");
                            break;
                        case DetailedAnalyzerSubmoduleKind.Empty:
                            rendered.Add("<div class=\"detail-gap\"></div>");
                            break;
                        default:
                            rendered.Add($"<div class=\"detail-line{indentClass}\">{Encode(submodule.Text ?? string.Empty)}</div>");
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

                    rendered.Add($"<div class=\"detail-line{indentClass}\">{Encode(line.TrimStart())}</div>");
                }
            }

            return string.Join(Environment.NewLine, rendered);
        }

        string reportTitle = report.IsTrendReport ? "DumpDetective Trend Analysis Report" : "DumpDetective Analysis Report";
        string dumpLabel = report.IsTrendReport ? "Latest dump" : "Dump";

        List<string> lines =
        [
            "<!DOCTYPE html>",
            "<html>",
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
            ".detail-item{margin-bottom:10px;border:1px solid #e5e7eb;border-radius:8px;background:#f8fafc;}",
            ".detail-item>summary{cursor:pointer;padding:10px 12px;font-weight:600;color:#0f172a;list-style:none;}",
            ".detail-item>summary::-webkit-details-marker{display:none;}",
            ".detail-item>summary::after{content:'▸';float:right;color:#64748b;}",
            ".detail-item[open]>summary::after{content:'▾';}",
            ".detail-block{background:#0f172a;color:#e5e7eb;border-radius:8px;padding:12px;overflow:auto;font-family:Consolas,\"Cascadia Mono\",monospace;font-size:13px;line-height:1.5;}",
            ".detail-subheading{font-weight:700;color:#93c5fd;margin:8px 0 4px 0;}",
            ".detail-divider{height:1px;background:#334155;margin:6px 0;}",
            ".detail-line{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;}",
            ".detail-key{color:#a7f3d0;font-weight:600;}",
            ".detail-value{color:#e5e7eb;}",
            ".detail-path{color:#fde68a;font-weight:600;}",
            ".detail-gap{height:8px;}",
            ".detail-indent-1{padding-left:12px;}",
            ".detail-indent-2{padding-left:24px;}",
            "</style>",
            "</head>",
            "<body>",
            "<main class=\"container\">",
            "<section class=\"header-card\">",
            $"<h1>{Encode(reportTitle)}</h1>",
            "<div class=\"meta-grid\">",
            $"<div class=\"meta-item\"><span class=\"meta-label\">{Encode(dumpLabel)}:</span> <span class=\"wrap\">{Encode(report.DumpPath)}</span></div>",
            $"<div class=\"meta-item\"><span class=\"meta-label\">Generated (UTC):</span> {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</div>",
            $"<div class=\"meta-item\"><span class=\"meta-label\">Elapsed:</span> {report.Elapsed.TotalSeconds:F1}s</div>",
            "</div>",
            $"<div class=\"dedup-note\"><strong>Dedup:</strong> merged {report.DedupDiagnostics.MergedSections}/{report.DedupDiagnostics.DuplicateCandidates}</div>",
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

        if (report.ExecutiveSummary.Count > 0)
        {
            lines.Add("<section class=\"section-card\">");
            lines.Add("<h2>Executive summary</h2>");
            lines.Add("<table><thead><tr><th>Signal</th><th>Value</th></tr></thead><tbody>");
            foreach (ExecutiveSummaryItem item in report.ExecutiveSummary)
            {
                lines.Add($"<tr><td>{Encode(item.Label)}</td><td class=\"wrap\">{Encode(item.Value)}</td></tr>");
            }
            lines.Add("</tbody></table>");
            lines.Add("</section>");
        }

        if (report.DeveloperActionPlan.Count > 0)
        {
            lines.Add("<section class=\"section-card\">");
            lines.Add("<h2>Developer action plan</h2>");
            lines.Add("<table><thead><tr><th>Priority</th><th>Title</th><th>Action</th><th>Impact</th></tr></thead><tbody>");
            foreach (DeveloperActionItem action in report.DeveloperActionPlan)
            {
                lines.Add($"<tr><td>{Encode(action.Priority)}</td><td>{Encode(action.Title)}</td><td class=\"wrap\">{Encode(action.Action)}</td><td class=\"wrap\">{Encode(action.Impact)}</td></tr>");
            }
            lines.Add("</tbody></table>");
            lines.Add("</section>");
        }

        foreach (ReportSection section in report.Sections)
        {
            string severityCss = section.Severity switch
            {
                Core.Models.FindingSeverity.Critical => "severity-critical",
                Core.Models.FindingSeverity.Warning => "severity-warning",
                _ => "severity-info"
            };

            lines.Add("<section class=\"section-card\">");
            lines.Add("<div class=\"section-header\">");
            lines.Add($"<span class=\"severity-badge {severityCss}\">{Encode(section.Severity.ToString())}</span>");
            lines.Add($"<h2>{Encode(section.Title)}</h2>");
            lines.Add($"<span class=\"category\">{Encode(section.Category)}</span>");
            lines.Add("</div>");
            lines.Add($"<p class=\"summary\">{Encode(section.NarrativeSummary)}</p>");
            lines.Add("<table>");
            lines.Add("<thead><tr><th>Label</th><th>Value</th></tr></thead>");
            lines.Add("<tbody>");
            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(row.Value, 90).Select(Encode));
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

            lines.Add("<section class=\"section-card\">");
            lines.Add("<h2>Detailed analyzer sections</h2>");

            for (int i = 0; i < detailedSections.Count; i++)
            {
                DetailedAnalyzerSection detail = detailedSections[i];
                string openAttribute = i == 0 ? " open" : string.Empty;
                lines.Add($"<details class=\"detail-item\"{openAttribute}>");
                lines.Add($"<summary>{Encode(detail.Title)}</summary>");
                lines.Add($"<div class=\"detail-block\">{RenderDetailedContentHtml(detail)}</div>");
                lines.Add("</details>");
            }

            lines.Add("</section>");
        }

        lines.Add("</main>");
        lines.Add("</body></html>");
        return string.Join(Environment.NewLine, lines);
    }
}

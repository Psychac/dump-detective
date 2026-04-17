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
        List<string> lines =
        [
            "DumpDetective Analysis Report",
            new string('=', 100),
            $"Dump: {report.DumpPath}",
            $"Generated (UTC): {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}",
            $"Elapsed: {report.Elapsed.TotalSeconds:F1}s",
            string.Empty,
            $"Dedup: merged {report.DedupDiagnostics.MergedSections}/{report.DedupDiagnostics.DuplicateCandidates} candidate duplicates",
            string.Empty
        ];

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

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class MarkdownCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Markdown;

    public string Render(ComposedReport report)
    {
        List<string> lines =
        [
            "# DumpDetective Analysis Report",
            string.Empty,
            $"> Dump: `{report.DumpPath}`  ",
            $"> Generated (UTC): `{report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}`  ",
            $"> Elapsed: `{report.Elapsed.TotalSeconds:F1}s`",
            string.Empty,
            $"> Dedup merged **{report.DedupDiagnostics.MergedSections}** section(s) from **{report.DedupDiagnostics.DuplicateCandidates}** candidate duplicate(s).",
            string.Empty
        ];

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

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class HtmlCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Html;

    public string Render(ComposedReport report)
    {
        List<string> lines =
        [
            "<!DOCTYPE html>",
            "<html>",
            "<head>",
            "<meta charset=\"utf-8\" />",
            "<style>body{font-family:Segoe UI,Arial,sans-serif;} table{border-collapse:collapse;width:100%;} td,th{border:1px solid #ddd;padding:6px;vertical-align:top;} .wrap{overflow-wrap:anywhere;word-break:break-word;} </style>",
            "</head>",
            "<body>",
            $"<h1>DumpDetective Analysis Report</h1>",
            $"<p><strong>Dump:</strong> {System.Net.WebUtility.HtmlEncode(report.DumpPath)}<br/><strong>Generated (UTC):</strong> {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}<br/><strong>Elapsed:</strong> {report.Elapsed.TotalSeconds:F1}s</p>",
            $"<p><strong>Dedup:</strong> merged {report.DedupDiagnostics.MergedSections}/{report.DedupDiagnostics.DuplicateCandidates}</p>"
        ];

        foreach (ReportSection section in report.Sections)
        {
            lines.Add($"<h2>[{section.Severity}] {System.Net.WebUtility.HtmlEncode(section.Title)}</h2>");
            lines.Add($"<p>{System.Net.WebUtility.HtmlEncode(section.NarrativeSummary)}</p>");
            lines.Add("<table>");
            lines.Add("<thead><tr><th>Label</th><th>Value</th></tr></thead>");
            lines.Add("<tbody>");
            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(row.Value, 90).Select(System.Net.WebUtility.HtmlEncode));
                lines.Add($"<tr><td>{System.Net.WebUtility.HtmlEncode(row.Label)}</td><td class=\"wrap\">{wrapped}</td></tr>");
            }
            lines.Add("</tbody>");
            lines.Add("</table>");

            if (section.RemediationHints.Count > 0)
            {
                lines.Add("<h3>Remediation</h3><ul>");
                foreach (string hint in section.RemediationHints)
                {
                    string wrapped = string.Join("<br/>", TableWrapHelper.Wrap(hint, 96).Select(System.Net.WebUtility.HtmlEncode));
                    lines.Add($"<li class=\"wrap\">{wrapped}</li>");
                }
                lines.Add("</ul>");
            }
        }

        lines.Add("</body></html>");
        return string.Join(Environment.NewLine, lines);
    }
}

using DumpDetective.Configuration;
using System.Net;
using System.Text;

namespace DumpDetective.Utilities
{
    internal static class ReportFormatter
    {
        public static string Format(ReportFormat format, string detailedReport, IReadOnlyList<string> insights, string dumpPath)
        {
            return format switch
            {
                ReportFormat.Markdown => ToMarkdown(detailedReport, insights, dumpPath),
                ReportFormat.Html => ToHtml(detailedReport, insights, dumpPath),
                _ => ToText(detailedReport, insights, dumpPath)
            };
        }

        private static string ToText(string detailedReport, IReadOnlyList<string> insights, string dumpPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("DumpDetective Analysis Report");
            builder.AppendLine(new string('=', 80));
            builder.AppendLine($"Dump File: {dumpPath}");
            builder.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("INSIGHTS");
            builder.AppendLine(new string('-', 80));
            foreach (string insight in insights)
            {
                builder.AppendLine($"- [OK] {insight}");
            }
            builder.AppendLine();
            builder.AppendLine("DETAILED ANALYSIS");
            builder.AppendLine(new string('-', 80));
            AppendTextSections(builder, detailedReport);
            return builder.ToString();
        }

        private static string ToMarkdown(string detailedReport, IReadOnlyList<string> insights, string dumpPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# 🕵️ DumpDetective Analysis Report");
            builder.AppendLine();
            builder.AppendLine("## Overview");
            builder.AppendLine();
            builder.AppendLine("| Property | Value |");
            builder.AppendLine("|---|---|");
            builder.AppendLine($"| Dump File | `{dumpPath}` |");
            builder.AppendLine($"| Generated (UTC) | `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}` |");
            builder.AppendLine();
            builder.AppendLine("## 🔍 Insights");
            builder.AppendLine();
            foreach (string insight in insights)
            {
                builder.AppendLine($"- ✅ {insight}");
            }
            builder.AppendLine();
            builder.AppendLine("## 📊 Detailed Analysis");
            builder.AppendLine();
            AppendMarkdownSections(builder, ParseSections(detailedReport));
            return builder.ToString();
        }

        private static void AppendMarkdownSections(StringBuilder builder, List<ReportSection> sections)
        {
            foreach (var section in sections)
            {
                FlushMarkdownSection(builder, section.Title, section.Lines);
            }
        }

        private static void FlushMarkdownSection(StringBuilder builder, string title, List<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            builder.AppendLine($"<details open>");
            builder.AppendLine($"<summary><strong>{WebUtility.HtmlEncode(title)}</strong></summary>");
            builder.AppendLine();
            builder.AppendLine("```text");
            foreach (string line in lines)
            {
                builder.AppendLine(line);
            }
            builder.AppendLine("```");
            builder.AppendLine("</details>");
            builder.AppendLine();
        }

        private static void AppendTextSections(StringBuilder builder, string detailedReport)
        {
            foreach (var section in ParseSections(detailedReport))
            {
                builder.AppendLine(section.Title);
                builder.AppendLine(new string('-', 80));
                foreach (var line in section.Lines)
                {
                    builder.AppendLine(line);
                }
                builder.AppendLine();
            }
        }

        private static List<ReportSection> ParseSections(string detailedReport)
        {
            string[] lines = detailedReport.Replace("\r\n", "\n").Split('\n');
            var sections = new List<ReportSection>();
            var currentSectionTitle = "General";
            var currentSectionLines = new List<string>();

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (IsSeparatorLine(line))
                {
                    continue;
                }

                if (IsSectionHeader(line))
                {
                    AddSectionIfNotEmpty(sections, currentSectionTitle, currentSectionLines);
                    currentSectionTitle = line.TrimEnd(':').Trim();
                    currentSectionLines = new List<string>();
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) && currentSectionLines.Count == 0)
                {
                    continue;
                }

                currentSectionLines.Add(line);
            }

            AddSectionIfNotEmpty(sections, currentSectionTitle, currentSectionLines);
            return sections;
        }

        private static void AddSectionIfNotEmpty(List<ReportSection> sections, string title, List<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            sections.Add(new ReportSection(title, lines));
        }

        private static bool IsSectionHeader(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.EndsWith(':'))
            {
                return false;
            }

            string core = line.TrimEnd(':').Trim();
            if (core.Length < 3)
            {
                return false;
            }

            bool hasLetter = false;
            foreach (char c in core)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (char.IsLower(c))
                    {
                        return false;
                    }
                }
            }

            return hasLetter;
        }

        private static bool IsSeparatorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            foreach (char c in line)
            {
                if (c != '=' && c != '-' && c != '_')
                {
                    return false;
                }
            }

            return line.Length >= 8;
        }

        private static string ToHtml(string detailedReport, IReadOnlyList<string> insights, string dumpPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<!DOCTYPE html>");
            builder.AppendLine("<html lang=\"en\">");
            builder.AppendLine("<head>");
            builder.AppendLine("  <meta charset=\"utf-8\" />");
            builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            builder.AppendLine("  <title>DumpDetective Analysis Report</title>");
            builder.AppendLine("  <style>");
            builder.AppendLine("    body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; line-height: 1.5; color: #1f2328; }");
            builder.AppendLine("    h1, h2 { margin-bottom: 8px; }");
            builder.AppendLine("    .meta-table { border-collapse: collapse; margin: 8px 0 20px 0; width: min(900px, 100%); }");
            builder.AppendLine("    .meta-table th, .meta-table td { border: 1px solid #d0d7de; padding: 8px 10px; text-align: left; }");
            builder.AppendLine("    .meta-table th { background: #f6f8fa; width: 220px; }");
            builder.AppendLine("    ul { margin-top: 8px; }");
            builder.AppendLine("    details { margin: 10px 0; border: 1px solid #d0d7de; border-radius: 8px; background: #fff; }");
            builder.AppendLine("    summary { cursor: pointer; padding: 10px 12px; font-weight: 600; background: #f6f8fa; }");
            builder.AppendLine("    .section-content { padding: 12px; }");
            builder.AppendLine("    pre { margin: 0; background: #0d1117; color: #c9d1d9; padding: 14px; border-radius: 6px; overflow: auto; }");
            builder.AppendLine("  </style>");
            builder.AppendLine("</head>");
            builder.AppendLine("<body>");
            builder.AppendLine("  <h1>🕵️ DumpDetective Analysis Report</h1>");
            builder.AppendLine("  <h2>Overview</h2>");
            builder.AppendLine("  <table class=\"meta-table\">");
            builder.AppendLine("    <tr><th>Property</th><th>Value</th></tr>");
            builder.AppendLine($"    <tr><th>Dump File</th><td><code>{WebUtility.HtmlEncode(dumpPath)}</code></td></tr>");
            builder.AppendLine($"    <tr><th>Generated (UTC)</th><td><code>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</code></td></tr>");
            builder.AppendLine("  </table>");
            builder.AppendLine("  <h2>🔍 Insights</h2>");
            builder.AppendLine("  <ul>");
            foreach (string insight in insights)
            {
                builder.AppendLine($"    <li>✅ {WebUtility.HtmlEncode(insight)}</li>");
            }
            builder.AppendLine("  </ul>");
            builder.AppendLine("  <h2>📊 Detailed Analysis</h2>");
            foreach (var section in ParseSections(detailedReport))
            {
                builder.AppendLine("  <details open>");
                builder.AppendLine($"    <summary>{WebUtility.HtmlEncode(section.Title)}</summary>");
                builder.AppendLine("    <div class=\"section-content\">");
                builder.AppendLine("      <pre>");
                foreach (var line in section.Lines)
                {
                    builder.AppendLine(WebUtility.HtmlEncode(line));
                }
                builder.AppendLine("      </pre>");
                builder.AppendLine("    </div>");
                builder.AppendLine("  </details>");
            }
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");
            return builder.ToString();
        }

        private sealed record ReportSection(string Title, List<string> Lines);
    }
}

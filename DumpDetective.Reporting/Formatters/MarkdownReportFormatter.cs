using DumpDetective.Core.Models;
using System.Net;
using System.Text;

namespace DumpDetective.Reporting.Formatters
{
    internal static partial class ReportFormatter
    {
        private static string ToMarkdown(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var b = new StringBuilder();
            b.AppendLine("# ðŸ•µï¸ DumpDetective Analysis Report");
            b.AppendLine();
            b.AppendLine($"> ðŸ“ **Dump:** `{dumpPath}`  ");
            b.AppendLine($"> ðŸ• **Generated (UTC):** `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}`");
            b.AppendLine();
            AppendMarkdownFindingsSummary(b, findings);
            b.AppendLine();
            AppendMarkdownInsights(b, insights);
            b.AppendLine();

            var parsed = ParseDetailedReport(detailedReport);

            if (parsed.TrendSection != null)
            {
                AppendMarkdownTrendSection(b, parsed.TrendSection);
                b.AppendLine();
            }

            b.AppendLine("## ðŸ“Š Detailed Analysis");
            b.AppendLine();

            foreach (var block in parsed.Blocks)
            {
                if (block.Label != null)
                {
                    b.AppendLine("---");
                    b.AppendLine();
                    bool isLast = block == parsed.Blocks[^1];
                    b.AppendLine(isLast
                        ? $"### ðŸ“¸ Snapshot {block.Label} *(current)*"
                        : $"### ðŸ“¸ Snapshot {block.Label}");
                    b.AppendLine();
                }

                string groupHd = block.Label != null ? "####" : "###";
                foreach (var (groupName, groupIcon, groupSections) in GroupSections(block.Sections))
                {
                    b.AppendLine($"{groupHd} {groupIcon} {groupName}");
                    b.AppendLine();
                    foreach (var section in groupSections)
                    {
                        string icon = SectionIcon(section.Title);
                        b.AppendLine("<details>");
                        b.AppendLine($"<summary>{icon} <strong>{WebUtility.HtmlEncode(section.Title)}</strong></summary>");
                        b.AppendLine();
                        b.AppendLine("```text");
                        foreach (string line in section.Lines)
                            b.AppendLine(line);
                        b.AppendLine("```");
                        b.AppendLine("</details>");
                        b.AppendLine();
                    }
                }
            }

            return b.ToString();
        }

        private static void AppendMarkdownFindingsSummary(StringBuilder b, IReadOnlyList<InsightFinding> findings)
        {
            b.AppendLine("## ðŸš¨ Findings Summary");
            b.AppendLine();
            if (findings.Count == 0)
            {
                b.AppendLine("> â„¹ï¸ No structured findings emitted by analyzers.");
                return;
            }

            int critCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warnCount = findings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = findings.Count(f => f.Severity == FindingSeverity.Info);
            b.AppendLine($"ðŸ”´ **Critical: {critCount}** &nbsp;Â·&nbsp; ðŸŸ  **Warning: {warnCount}** &nbsp;Â·&nbsp; ðŸ”µ **Info: {infoCount}**");
            b.AppendLine();
            b.AppendLine("| &nbsp; | Severity | Analyzer | Title | Evidence | Recommendation |");
            b.AppendLine("|:---:|---|---|---|---|---|");

            var ordered = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Analyzer);
            const int MdCap = 25;
            foreach (var f in ordered.Take(MdCap))
            {
                string icon = SeverityIcon(f.Severity);
                b.AppendLine($"| {icon} | {f.Severity} | {EscapePipe(f.Analyzer)} | {EscapePipe(f.Title)} | {EscapePipe(f.Evidence)} | {EscapePipe(f.Recommendation)} |");
            }
            if (findings.Count > MdCap)
                b.AppendLine($"> *â€¦and {findings.Count - MdCap} more findings not shown.*");
        }

        private static void AppendMarkdownInsights(StringBuilder b, IReadOnlyList<string> insights)
        {
            b.AppendLine("## ðŸ” Insights");
            b.AppendLine();
            foreach (string insight in insights)
                b.AppendLine($"- {InsightMarkdownIcon(insight)} {insight}");
        }

        private static void AppendMarkdownTrendSection(StringBuilder b, ReportSection trend)
        {
            b.AppendLine("## ðŸ“ˆ Trend Comparison");
            b.AppendLine();
            var tc = ParseTrendContent(trend);

            if (tc.SummaryKV.Count > 0)
            {
                b.AppendLine("| Metric | Value |");
                b.AppendLine("|---|:---:|");
                foreach (var (k, v) in tc.SummaryKV)
                    b.AppendLine($"| {k} | **{v}** |");
                b.AppendLine();
            }

            if (tc.TimelineGroups.Count > 0)
            {
                b.AppendLine("### ðŸ“Š Metric Timeline");
                b.AppendLine();
                foreach (var (analyzer, metrics) in tc.TimelineGroups.Where(g => g.Metrics.Count > 0))
                {
                    b.AppendLine($"**{analyzer}**");
                    b.AppendLine();
                    foreach (string m in metrics)
                        b.AppendLine($"- {m}");
                    b.AppendLine();
                }
            }

            if (tc.NewFindings.Count > 0)
            {
                b.AppendLine("### ðŸ”º New Findings");
                b.AppendLine();
                foreach (string f in tc.NewFindings) b.AppendLine($"- {f}");
                b.AppendLine();
            }

            if (tc.ResolvedFindings.Count > 0)
            {
                b.AppendLine("### âœ… Resolved Findings");
                b.AppendLine();
                foreach (string f in tc.ResolvedFindings) b.AppendLine($"- {f}");
                b.AppendLine();
            }
        }
    }
}



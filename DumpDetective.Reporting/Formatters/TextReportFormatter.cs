using DumpDetective.Core.Models;
using System.Text;

namespace DumpDetective.Reporting.Formatters
{
    internal static partial class ReportFormatter
    {
        private static string ToText(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var b = new StringBuilder();
            b.AppendLine("DumpDetective Analysis Report");
            b.AppendLine(new string('=', 80));
            b.AppendLine($"Dump File: {dumpPath}");
            b.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            b.AppendLine();
            AppendTextFindingsSummary(b, findings);
            b.AppendLine();
            b.AppendLine("INSIGHTS");
            b.AppendLine(new string('-', 80));
            foreach (string insight in insights)
                b.AppendLine($"  {insight}");
            b.AppendLine();
            b.AppendLine("DETAILED ANALYSIS");
            b.AppendLine(new string('-', 80));
            AppendTextSections(b, detailedReport);
            return b.ToString();
        }

        private static void AppendTextFindingsSummary(StringBuilder b, IReadOnlyList<InsightFinding> findings)
        {
            b.AppendLine("FINDINGS SUMMARY");
            b.AppendLine(new string('-', 80));
            if (findings.Count == 0)
            {
                b.AppendLine("No structured findings emitted by analyzers.");
                return;
            }
            b.AppendLine($"Critical: {findings.Count(f => f.Severity == FindingSeverity.Critical):N0}  Warning: {findings.Count(f => f.Severity == FindingSeverity.Warning):N0}  Info: {findings.Count(f => f.Severity == FindingSeverity.Info):N0}");
            b.AppendLine();
            foreach (var f in findings.Take(8))
            {
                b.AppendLine($"[{f.Severity.ToString().ToUpperInvariant()}] {f.Title}");
                b.AppendLine($"  Evidence:       {f.Evidence}");
                b.AppendLine($"  Recommendation: {f.Recommendation}");
                b.AppendLine();
            }
        }

        private static void AppendTextSections(StringBuilder b, string detailedReport)
        {
            foreach (var section in ParseSections(detailedReport))
            {
                b.AppendLine(section.Title);
                b.AppendLine(new string('-', 80));
                foreach (var line in section.Lines)
                    b.AppendLine(line);
                b.AppendLine();
            }
        }
    }
}



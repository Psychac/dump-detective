using DumpDetective.Configuration;
using DumpDetective.Models;
using System.Net;
using System.Text;

namespace DumpDetective.Utilities
{
    internal static partial class ReportFormatter
    {
        public static string Format(ReportFormat format, string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            return format switch
            {
                ReportFormat.Markdown => ToMarkdown(detailedReport, insights, dumpPath, findings),
                ReportFormat.Html => ToHtml(detailedReport, insights, dumpPath, findings),
                _ => ToText(detailedReport, insights, dumpPath, findings)
            };
        }

        // ── Severity / section / group classification ──────────────────────────

        private static string SeverityIcon(FindingSeverity severity) => severity switch
        {
            FindingSeverity.Critical => "🔴",
            FindingSeverity.Warning  => "🟠",
            _                        => "🔵"
        };

        private static string InsightMarkdownIcon(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "🔴";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "🟠";
            return "🔵";
        }

        private static string InsightHtmlClass(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "ins-crit";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "ins-warn";
            return "ins-info";
        }

        // ── Section classification ─────────────────────────────────────────────

        private static string SectionIcon(string title)
        {
            string u = title.ToUpperInvariant();
            if (u.Contains("MEMORY LEAK"))      return "💧";
            if (u.Contains("MEMORY"))           return "🧠";
            if (u.Contains("GC GENERATION"))    return "♻️";
            if (u.Contains("GC HANDLE"))        return "🔗";
            if (u.Contains("CRASH"))            return "💥";
            if (u.Contains("HANG"))             return "⏸️";
            if (u.Contains("COLLECTION"))       return "📦";
            if (u.Contains("THREAD STACK"))     return "📚";
            if (u.Contains("THREAD"))           return "🧵";
            if (u.Contains("EVENT"))            return "📡";
            if (u.Contains("LOH"))              return "🧩";
            if (u.Contains("DEPENDENT HANDLE")) return "⛓️";
            if (u.Contains("STATIC ROOT"))      return "🌱";
            if (u.Contains("REFERENCE CHAIN"))  return "🔍";
            if (u.Contains("MODULE"))           return "📚";
            if (u.Contains("CLR VERSION"))      return "🔧";
            return "📋";
        }

        private static (string Name, string Icon) SectionGroupInfo(string title)
        {
            string u = title.ToUpperInvariant();
            if (u.Contains("MEMORY LEAK") || (u.Contains("FINALIZER") && !u.Contains("THREAD")) || u.Contains("DUPLICATE") ||
                u.Contains("STATIC ROOT")  || u.Contains("REFERENCE CHAIN") ||
                u.Contains("COLLECTION")   || u.Contains("EVENT LEAK"))
                return ("Leak Detection", "💧");

            if (u.Contains("CRASH") || u.Contains("EXCEPTION") || u.Contains("HANG"))
                return ("Stability", "🩺");

            if (u.Contains("MEMORY") || u.Contains("HEAP")  || u.Contains("LOH") ||
                u.Contains("GC GENERATION") || u.Contains("OVERALL") || u.Contains("TOP TYPES"))
                return ("Memory Health", "🧠");

            if (u.Contains("GC HANDLE") || u.Contains("DEPENDENT HANDLE"))
                return ("Handles & Roots", "🔗");

            if (u.Contains("THREAD"))
                return ("Threading", "🧵");

            if (u.Contains("MODULE") || u.Contains("ASSEMBLY") ||
                u.Contains("CLR VERSION") || u.Contains("VERSION CONFLICT"))
                return ("Infrastructure", "🏗️");

            return ("General", "📋");
        }

        private static int GroupSortOrder(string groupName) => groupName switch
        {
            "Stability"       => 0,
            "Leak Detection"  => 1,
            "Memory Health"   => 2,
            "Handles & Roots" => 3,
            "Threading"       => 4,
            "Infrastructure"  => 5,
            _                 => 6
        };

        private static IEnumerable<(string Name, string Icon, IEnumerable<ReportSection> Sections)> GroupSections(
            IReadOnlyList<ReportSection> sections)
        {
            return sections
                .Select(s => (Section: s, Info: SectionGroupInfo(s.Title)))
                .GroupBy(x => x.Info)
                .OrderBy(g => GroupSortOrder(g.Key.Name))
                .Select(g => (g.Key.Name, g.Key.Icon, g.Select(x => x.Section)));
        }

        // ── Shared utilities ───────────────────────────────────────────────────

        private static string HtmlEnc(string s) => WebUtility.HtmlEncode(s);

        private static string EscapePipe(string value) =>
            value.Replace("|", "\\|", StringComparison.Ordinal);

        // ── Private records ────────────────────────────────────────────────────

        private sealed record ReportSection(string Title, List<string> Lines);
        private sealed record ParsedReport(ReportSection? TrendSection, IReadOnlyList<ParsedDumpBlock> Blocks);
        private sealed record ParsedDumpBlock(string? Label, IReadOnlyList<ReportSection> Sections);
        private sealed record TrendContent(
            List<(string K, string V)> SummaryKV,
            List<(string Analyzer, List<string> Metrics)> TimelineGroups,
            List<string> NewFindings,
            List<string> ResolvedFindings);
    }
}


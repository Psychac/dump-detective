using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using System.Net;
using System.Text;

namespace DumpDetective.Reporting.Formatters
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

        // â”€â”€ Severity / section / group classification â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string SeverityIcon(FindingSeverity severity) => severity switch
        {
            FindingSeverity.Critical => "ðŸ”´",
            FindingSeverity.Warning  => "ðŸŸ ",
            _                        => "ðŸ”µ"
        };

        private static string InsightMarkdownIcon(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "ðŸ”´";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "ðŸŸ ";
            return "ðŸ”µ";
        }

        private static string InsightHtmlClass(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "ins-crit";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "ins-warn";
            return "ins-info";
        }

        // â”€â”€ Section classification â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string SectionIcon(string title)
        {
            string u = CanonicalizeSectionTitle(title);
            if (u.Contains("MEMORY LEAK"))                       return "ðŸ’§";
            if (u.Contains("FINALIZER") && !u.Contains("THREAD")) return "ðŸ’§";
            if (u.Contains("HIGHLY REFERENCED"))                 return "ðŸ“Ž";
            if (u.Contains("ROOTED OBJECTS"))                    return "âš“";
            if (u.Contains("STATIC FIELD"))                      return "ðŸŒ±";
            if (u.Contains("OPTIMIZATION TIPS") || u.Contains("CORRELATION")) return "ðŸ’¡";
            if (u.Contains("RUN CONTEXT") || u == "GENERAL")    return "ðŸ§­";
            if (u.Contains("DEADLOCK"))                          return "ðŸ”´";
            if (u.Contains("LOCK GRAPH") || u.Contains("LOCK CONTENTION") || u.Contains("CAUSALITY")) return "ðŸ”’";
            if (u.Contains("ASYNC TASK"))                        return "â³";
            if (u.Contains("OBJECT TYPES"))                      return "ðŸ“Š";
            if (u.Contains("MEMORY"))                            return "ðŸ§ ";
            if (u.Contains("GC GENERATION"))                     return "â™»ï¸";
            if (u.Contains("GC MODE"))                           return "â™»ï¸";
            if (u.Contains("GC HANDLE"))                         return "ðŸ”—";
            if (u.Contains("DEPENDENT HANDLE"))                  return "â›“ï¸";
            if (u.Contains("HANDLE"))                            return "ðŸ”—";
            if (u.Contains("RETENTION"))                         return "â›“ï¸";
            if (u.Contains("CRASH"))                             return "ðŸ’¥";
            if (u.Contains("HANG"))                              return "â¸ï¸";
            if (u.Contains("COLLECTION"))                        return "ðŸ“¦";
            if (u.Contains("THREAD STACK"))                      return "ðŸ“š";
            if (u.Contains("CLUSTER"))                           return "ðŸŽ¯";
            if (u.Contains("THREAD"))                            return "ðŸ§µ";
            if (u.Contains("WAIT CATEGORY"))                     return "â¸ï¸";
            if (u.Contains("APP DOMAIN"))                        return "ðŸ—ï¸";
            if (u.Contains("HOTSPOT"))                           return "ðŸ”¥";
            if (u.Contains("EVENT"))                             return "ðŸ“¡";
            if (u.Contains("INSTANCES"))                         return "ðŸ“¡";
            if (u.Contains("LOH"))                               return "ðŸ§©";
            if (u.Contains("STATIC ROOT"))                       return "ðŸŒ±";
            if (u.Contains("REFERENCE CHAIN"))                   return "ðŸ”";
            if (u.Contains("MODULE"))                            return "ðŸ“š";
            if (u.Contains("CLR VERSION"))                       return "ðŸ”§";
            return "ðŸ“‹";
        }

        private static (string Name, string Icon) SectionGroupInfo(string title)
        {
            string u = CanonicalizeSectionTitle(title);

            return u switch
            {
                "RUN CONTEXT" or "GENERAL" or "CLR VERSION INFORMATION" or "MODULE SUMMARY" or "LOADED ASSEMBLIES (TOP 30)" or "VERSION CONFLICTS DETECTED"
                    => ("Run Context", "ðŸ§­"),

                "EXCEPTION SUMMARY" or "LIKELY CRASH THREADS" or "DETAILED EXCEPTION INFORMATION" or
                "HANG INDICATORS" or "HANG WAIT CATEGORY BREAKDOWN" or "WAITING THREADS BREAKDOWN" or
                "DEADLOCK DETECTION" or "DEADLOCK CANDIDATES" or
                "LOCK CONTENTION SUMMARY" or "LOCK CONTENTION HOTSPOTS" or "LOCK CAUSALITY CHAIN"
                    => ("Stability", "ðŸ©º"),

                "OVERALL SUMMARY" or "HEAP SUMMARY" or "LARGE OBJECT HEAP (LOH) USAGE" or
                "LOH SUMMARY" or "TOP FRAGMENTED LOH SEGMENTS" or
                "GENERATION SPLIT" or "TOP LOH OBJECT TYPES" or
                "TOP 20 OBJECT TYPES BY MEMORY SIZE" or "TOP 20 OBJECT TYPES BY COUNT"
                    => ("Memory Health", "ðŸ§ "),

                "FINALIZER QUEUE" or "DUPLICATE STRING ANALYSIS" or "HIGHLY REFERENCED OBJECTS" or
                "COLLECTION SUMMARY" or "MOST WASTEFUL COLLECTIONS (TOP 15)" or "WASTE SIGNAL" or
                "EVENT LEAK ANALYSIS" or "SUMMARY BY EVENT TYPE" or "DETAILED INSTANCES" or
                "STATIC FIELD REFERENCES" or "ROOTED OBJECTS ANALYSIS" or "RETENTION PRESSURE SIGNAL" or
                "REFERENCE CHAIN ANALYSIS" or "TOP TYPE SAMPLE TRACE RESULTS" or "REFERENCE CHAINS (SHOWING UP TO 5)"
                    => ("Leak & Retention", "ðŸ’§"),

                "HANDLE SUMMARY" or "HANDLES BY KIND" or "TOP TYPES REFERENCED BY HANDLES" or
                "TOP TYPES REFERENCED BY PINNED HANDLES" or "HANDLE PRESSURE SIGNAL" or
                "DEPENDENT HANDLE SUMMARY" or "TOP SOURCE TYPES" or "TOP TARGET TYPES" or
                "TOP SOURCE -> TARGET EDGES" or "RESOLUTION QUALITY SIGNAL"
                    => ("Handles & Roots", "ðŸ”—"),

                "THREAD ANALYSIS" or "THREAD STATE DISTRIBUTION" or "APP DOMAIN DISTRIBUTION" or
                "GC MODE DISTRIBUTION" or "THREADS WITH LOCKS" or "POTENTIALLY BLOCKED THREADS" or
                "THREADS WITH ACTIVE EXCEPTIONS" or "ACTIVE EXCEPTION TYPES ON THREADS" or
                "TOP STACK HOTSPOTS (TOP FRAME)" or "THREAD POOL STATUS" or "FINALIZER THREAD" or
                "ASYNC TASK ANALYSIS" or "ASYNC THREAD ISSUES" or "THREAD GROUPS" or
                "CLUSTER SUMMARY" or "TOP SIGNATURES" or "TOP THREAD CLUSTERS"
                    => ("Threading & Concurrency", "ðŸ§µ"),

                "ðŸ’¡ OPTIMIZATION TIPS" or "CROSS-ANALYZER CORRELATION INSIGHTS"
                    => ("Optimization Guidance", "ðŸ’¡"),

                _ when u.Contains("OPTIMIZATION TIPS") || u.Contains("CORRELATION")
                    => ("Optimization Guidance", "ðŸ’¡"),
                _ when u.Contains("THREAD") || u.Contains("WAIT CATEGORY") || u.Contains("HOTSPOT") || u.Contains("CLUSTER") || u.Contains("GC MODE")
                    => ("Threading & Concurrency", "ðŸ§µ"),
                _ when u.Contains("HANDLE") || u.Contains("RETENTION")
                    => ("Handles & Roots", "ðŸ”—"),
                _ when u.Contains("LOH") || u.Contains("MEMORY") || u.Contains("HEAP") || u.Contains("GC GENERATION") || u.Contains("OBJECT TYPES")
                    => ("Memory Health", "ðŸ§ "),
                _ when u.Contains("LEAK") || u.Contains("FINALIZER") || u.Contains("DUPLICATE") || u.Contains("EVENT") || u.Contains("ROOTED") || u.Contains("STATIC FIELD")
                    => ("Leak & Retention", "ðŸ’§"),
                _ when u.Contains("EXCEPTION") || u.Contains("HANG") || u.Contains("DEADLOCK") || u.Contains("LOCK CONTENTION")
                    => ("Stability", "ðŸ©º"),
                _ => ("General", "ðŸ“‹")
            };
        }

        private static int GroupSortOrder(string groupName) => groupName switch
        {
            "Run Context"     => 0,
            "Stability"       => 0,
            "Memory Health"   => 1,
            "Leak & Retention"=> 2,
            "Handles & Roots" => 3,
            "Threading & Concurrency" => 4,
            "Optimization Guidance" => 5,
            _                 => 6
        };

        private static int SectionSortOrder(string title)
        {
            string u = CanonicalizeSectionTitle(title);
            return u switch
            {
                "RUN CONTEXT" => 0,
                "CLR VERSION INFORMATION" => 1,
                "MODULE SUMMARY" => 2,
                "LOADED ASSEMBLIES (TOP 30)" => 3,
                "VERSION CONFLICTS DETECTED" => 4,

                "EXCEPTION SUMMARY" => 10,
                "LIKELY CRASH THREADS" => 11,
                "DETAILED EXCEPTION INFORMATION" => 12,
                "HANG INDICATORS" => 13,
                "HANG WAIT CATEGORY BREAKDOWN" => 14,
                "WAITING THREADS BREAKDOWN" => 15,
                "DEADLOCK DETECTION" => 16,
                "DEADLOCK CANDIDATES" => 17,
                "LOCK CONTENTION SUMMARY" => 18,
                "LOCK CONTENTION HOTSPOTS" => 19,
                "LOCK CAUSALITY CHAIN" => 20,

                "OVERALL SUMMARY" => 30,
                "HEAP SUMMARY" => 31,
                "LARGE OBJECT HEAP (LOH) USAGE" => 32,
                "LOH SUMMARY" => 33,
                "TOP FRAGMENTED LOH SEGMENTS" => 34,
                "GENERATION SPLIT" => 35,
                "TOP LOH OBJECT TYPES" => 36,
                "TOP 20 OBJECT TYPES BY MEMORY SIZE" => 37,
                "TOP 20 OBJECT TYPES BY COUNT" => 38,

                "FINALIZER QUEUE" => 40,
                "DUPLICATE STRING ANALYSIS" => 41,
                "HIGHLY REFERENCED OBJECTS" => 42,
                "COLLECTION SUMMARY" => 43,
                "MOST WASTEFUL COLLECTIONS (TOP 15)" => 44,
                "WASTE SIGNAL" => 45,
                "EVENT LEAK ANALYSIS" => 46,
                "SUMMARY BY EVENT TYPE" => 47,
                "DETAILED INSTANCES" => 48,
                "STATIC FIELD REFERENCES" => 49,
                "ROOTED OBJECTS ANALYSIS" => 50,
                "RETENTION PRESSURE SIGNAL" => 51,
                "REFERENCE CHAIN ANALYSIS" => 52,
                "TOP TYPE SAMPLE TRACE RESULTS" => 53,
                "REFERENCE CHAINS (SHOWING UP TO 5)" => 54,

                "HANDLE SUMMARY" => 60,
                "HANDLES BY KIND" => 61,
                "TOP TYPES REFERENCED BY HANDLES" => 62,
                "TOP TYPES REFERENCED BY PINNED HANDLES" => 63,
                "HANDLE PRESSURE SIGNAL" => 64,
                "DEPENDENT HANDLE SUMMARY" => 65,
                "TOP SOURCE TYPES" => 66,
                "TOP TARGET TYPES" => 67,
                "TOP SOURCE -> TARGET EDGES" => 68,
                "RESOLUTION QUALITY SIGNAL" => 69,

                "THREAD ANALYSIS" => 70,
                "THREAD STATE DISTRIBUTION" => 71,
                "APP DOMAIN DISTRIBUTION" => 72,
                "GC MODE DISTRIBUTION" => 73,
                "THREADS WITH LOCKS" => 74,
                "POTENTIALLY BLOCKED THREADS" => 75,
                "THREADS WITH ACTIVE EXCEPTIONS" => 76,
                "ACTIVE EXCEPTION TYPES ON THREADS" => 77,
                "TOP STACK HOTSPOTS (TOP FRAME)" => 78,
                "THREAD POOL STATUS" => 79,
                "FINALIZER THREAD" => 80,
                "ASYNC TASK ANALYSIS" => 81,
                "ASYNC THREAD ISSUES" => 82,
                "THREAD GROUPS" => 83,
                "CLUSTER SUMMARY" => 84,
                "TOP SIGNATURES" => 85,
                "TOP THREAD CLUSTERS" => 86,

                "ðŸ’¡ OPTIMIZATION TIPS" => 90,
                "CROSS-ANALYZER CORRELATION INSIGHTS" => 90,
                _ => 200
            };
        }

        private static string CanonicalizeSectionTitle(string title)
        {
            string normalized = title.Trim().ToUpperInvariant();

            if (normalized.EndsWith(")", StringComparison.Ordinal))
            {
                int lastOpen = normalized.LastIndexOf(" (", StringComparison.Ordinal);
                if (lastOpen > 0)
                {
                    ReadOnlySpan<char> suffix = normalized.AsSpan(lastOpen + 2, normalized.Length - lastOpen - 3);
                    bool isNumericSuffix = suffix.Length > 0;
                    for (int i = 0; i < suffix.Length; i++)
                    {
                        if (!char.IsDigit(suffix[i]))
                        {
                            isNumericSuffix = false;
                            break;
                        }
                    }

                    if (isNumericSuffix)
                    {
                        normalized = normalized[..lastOpen];
                    }
                }
            }

            return normalized;
        }

        private static IEnumerable<(string Name, string Icon, IEnumerable<ReportSection> Sections)> GroupSections(
            IReadOnlyList<ReportSection> sections)
        {
            return sections
                .Select((s, index) => (Section: s, Index: index, Info: SectionGroupInfo(s.Title)))
                .GroupBy(x => x.Info)
                .OrderBy(g => GroupSortOrder(g.Key.Name))
                .Select(g => (g.Key.Name, g.Key.Icon, g
                    .OrderBy(x => SectionSortOrder(x.Section.Title))
                    .ThenBy(x => x.Index)
                    .Select(x => x.Section)));
        }

        // â”€â”€ Shared utilities â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static string HtmlEnc(string s) => WebUtility.HtmlEncode(s);

        private static string EscapePipe(string value) =>
            value.Replace("|", "\\|", StringComparison.Ordinal);

        // â”€â”€ Private records â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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




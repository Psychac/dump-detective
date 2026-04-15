namespace DumpDetective.Utilities
{
    internal static partial class ReportFormatter
    {
        private static List<ReportSection> ParseSections(string detailedReport) =>
            NormalizeSectionsForParity(ParseSectionsFromLines(detailedReport.Replace("\r\n", "\n").Split('\n')));

        private static List<ReportSection> ParseSectionsFromLines(IEnumerable<string> rawLines)
        {
            var sections = new List<ReportSection>();
            string currentTitle = "General";
            var currentLines = new List<string>();

            foreach (string raw in rawLines)
            {
                string line = raw.TrimEnd();
                if (IsSeparatorLine(line)) continue;

                if (IsSectionHeader(line))
                {
                    AddSectionIfNotEmpty(sections, currentTitle, currentLines);
                    currentTitle = line.TrimEnd(':').Trim();
                    currentLines = [];
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) && currentLines.Count == 0) continue;
                currentLines.Add(line);
            }

            AddSectionIfNotEmpty(sections, currentTitle, currentLines);
            return sections;
        }

        private static ParsedReport ParseDetailedReport(string rawReport)
        {
            string[] rawLines = rawReport.Replace("\r\n", "\n").Split('\n');

            var rawBlocks = new List<(string? Label, List<string> Lines)>();
            string? currentLabel = null;
            var buffer = new List<string>();

            foreach (string raw in rawLines)
            {
                if (IsSnapshotHeader(raw.TrimEnd()))
                {
                    rawBlocks.Add((currentLabel, buffer));
                    currentLabel = ExtractSnapshotLabel(raw.TrimEnd());
                    buffer = [];
                }
                else
                {
                    buffer.Add(raw);
                }
            }
            rawBlocks.Add((currentLabel, buffer));

            ReportSection? trendSection = null;
            var dumpBlocks = new List<ParsedDumpBlock>();

            foreach (var (label, lines) in rawBlocks)
            {
                var sections = ParseSectionsFromLines(lines);
                sections = NormalizeSectionsForParity(sections);

                if (label == null && trendSection == null)
                {
                    int idx = sections.FindIndex(s =>
                        s.Title.Equals("TREND COMPARISON", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) { trendSection = sections[idx]; sections.RemoveAt(idx); }
                }

                // Skip "General" — dump path is already shown in the report meta card
                sections.RemoveAll(s => s.Title.Equals("General", StringComparison.Ordinal));

                if (sections.Count > 0)
                    dumpBlocks.Add(new ParsedDumpBlock(label, sections));
            }

            if (dumpBlocks.Count == 0)
                dumpBlocks.Add(new ParsedDumpBlock(null, []));

            return new ParsedReport(trendSection, dumpBlocks);
        }

        private static bool IsSnapshotHeader(string line) =>
            line.StartsWith("ANALYSIS SNAPSHOT ", StringComparison.Ordinal);

        private static string ExtractSnapshotLabel(string line)
        {
            const int prefixLen = 18; // "ANALYSIS SNAPSHOT ".Length
            int colonIdx = line.IndexOf(':', prefixLen);
            if (colonIdx < 0) return line[prefixLen..].Trim();
            string progress = line[prefixLen..colonIdx].Trim();
            string path = line[(colonIdx + 1)..].Trim();
            return $"{progress} — {Path.GetFileName(path)}";
        }

        private static TrendContent ParseTrendContent(ReportSection trend)
        {
            var summaryKV = new List<(string K, string V)>();
            var timelineGroups = new List<(string Analyzer, List<string> Metrics)>();
            var newFindings = new List<string>();
            var resolvedFindings = new List<string>();
            string? currentAnalyzer = null;
            var currentMetrics = new List<string>();
            string state = "summary";

            foreach (string raw in trend.Lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (raw.StartsWith("PER-ANALYZER METRIC TIMELINE", StringComparison.Ordinal))
                { state = "timeline"; continue; }

                if (raw.TrimStart().StartsWith("New findings:", StringComparison.Ordinal))
                {
                    if (currentAnalyzer != null) { timelineGroups.Add((currentAnalyzer, currentMetrics)); currentAnalyzer = null; currentMetrics = []; }
                    state = "new"; continue;
                }

                if (raw.TrimStart().StartsWith("Resolved findings:", StringComparison.Ordinal))
                { state = "resolved"; continue; }

                switch (state)
                {
                    case "summary":
                        if (!raw.StartsWith(" ", StringComparison.Ordinal))
                        {
                            int ci = raw.IndexOf(':');
                            if (ci > 0) summaryKV.Add((raw[..ci].Trim(), raw[(ci + 1)..].Trim()));
                        }
                        break;

                    case "timeline":
                        if (raw.StartsWith("  [", StringComparison.Ordinal) && raw.TrimEnd().EndsWith("]"))
                        {
                            if (currentAnalyzer != null) timelineGroups.Add((currentAnalyzer, currentMetrics));
                            currentAnalyzer = raw.Trim()[1..^1];
                            currentMetrics = [];
                        }
                        else if (raw.StartsWith("    ", StringComparison.Ordinal) && currentAnalyzer != null)
                        {
                            currentMetrics.Add(raw.Trim());
                        }
                        break;

                    case "new":
                        if (raw.TrimStart().StartsWith("-", StringComparison.Ordinal))
                            newFindings.Add(raw.TrimStart()[1..].Trim());
                        break;

                    case "resolved":
                        if (raw.TrimStart().StartsWith("-", StringComparison.Ordinal))
                            resolvedFindings.Add(raw.TrimStart()[1..].Trim());
                        break;
                }
            }
            if (currentAnalyzer != null) timelineGroups.Add((currentAnalyzer, currentMetrics));

            return new TrendContent(summaryKV, timelineGroups, newFindings, resolvedFindings);
        }

        private static void AddSectionIfNotEmpty(List<ReportSection> sections, string title, List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            if (lines.Count > 0)
                sections.Add(new ReportSection(title, lines));
        }

        private static bool IsSectionHeader(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.EndsWith(':')) return false;
            string core = line.TrimEnd(':').Trim();
            if (core.Length < 3) return false;
            bool hasLetter = false;
            foreach (char c in core)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (char.IsLower(c)) return false;
                }
            }
            return hasLetter;
        }

        private static bool IsSeparatorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (char c in line)
                if (c != '=' && c != '-' && c != '_') return false;
            return line.Length >= 8;
        }

        private static List<ReportSection> NormalizeSectionsForParity(List<ReportSection> sections)
        {
            var normalized = sections
                .Select(s => new ReportSection(s.Title, [.. s.Lines]))
                .ToList();

            MergeSectionInto(normalized, "HEAP COMPOSITION SIGNALS", "OVERALL SUMMARY", "Heap Signal");
            MergeSectionInto(normalized, "LOH RISK SIGNAL", "HEAP SUMMARY", "LOH Signal");
            MergeSectionInto(normalized, "FRAGMENTATION SIGNAL", "LOH SUMMARY", "Fragmentation Signal");
            MergeSectionInto(normalized, "HANDLE PRESSURE SIGNAL", "HANDLE SUMMARY", "Handle Signal");
            MergeSectionInto(normalized, "EVENT LEAK SIGNAL", "EVENT LEAK ANALYSIS", "Event Signal");
            MergeSectionInto(normalized, "THREAD HEALTH SIGNAL", "THREAD TRIAGE SUMMARY", "Thread Health Signal");
            MergeSectionInto(normalized, "DIVERSITY SIGNAL", "CLUSTER SUMMARY", "Diversity Signal");
            MergeSectionInto(normalized, "GC-ROOT COVERAGE SIGNAL", "REFERENCE RETENTION SUMMARY", "Retention Signal");
            MergeSectionInto(normalized, "CAPACITY RECOMMENDATION", "WASTE SIGNAL", "Capacity Recommendation");
            MergeSectionInto(normalized, "RESOLUTION QUALITY SIGNAL", "DEPENDENT HANDLE SUMMARY", "Resolution Signal");

            AddAlias(normalized, "HIGH-REFERENCE SIGNAL", "HIGHLY REFERENCED OBJECTS");
            AddAlias(normalized, "THREAD TRIAGE SUMMARY", "THREAD ANALYSIS");
            AddAlias(normalized, "WAIT CATEGORY BREAKDOWN", "WAIT CATEGORY DISTRIBUTION");
            AddAlias(normalized, "TOP FRAGMENTED SEGMENTS", "TOP FRAGMENTED LOH SEGMENTS");
            AddAlias(normalized, "REFERENCE RETENTION SUMMARY", "REFERENCE CHAIN ANALYSIS");
            AddAlias(normalized, "CROSS-ANALYZER CORRELATION INSIGHTS", "💡 OPTIMIZATION TIPS");

            SynthesizeActiveExceptionTypesOnThreads(normalized);
            SynthesizeThreadGroups(normalized);
            SynthesizeLockCausalityChain(normalized);
            SynthesizeRootAndStaticSections(normalized);

            EnsureDistinctSectionTitles(normalized);
            return normalized;
        }

        private static void MergeSectionInto(List<ReportSection> sections, string sourceTitle, string targetTitle, string label)
        {
            int sourceIndex = FindSectionIndex(sections, sourceTitle);
            int targetIndex = FindSectionIndex(sections, targetTitle);
            if (sourceIndex < 0 || targetIndex < 0)
                return;

            var source = sections[sourceIndex];
            var target = sections[targetIndex];

            if (source.Lines.Count > 0)
            {
                if (target.Lines.Count > 0 && !string.IsNullOrWhiteSpace(target.Lines[^1]))
                    target.Lines.Add(string.Empty);

                target.Lines.Add($"{label}:");
                foreach (string line in source.Lines)
                    target.Lines.Add(line);
            }

            sections.RemoveAt(sourceIndex);
        }

        private static void AddAlias(List<ReportSection> sections, string sourceTitle, string aliasTitle)
        {
            if (FindSectionIndex(sections, aliasTitle) >= 0)
                return;

            int sourceIndex = FindSectionIndex(sections, sourceTitle);
            if (sourceIndex < 0)
                return;

            var source = sections[sourceIndex];
            sections.Insert(sourceIndex + 1, new ReportSection(aliasTitle, [.. source.Lines]));
        }

        private static void SynthesizeActiveExceptionTypesOnThreads(List<ReportSection> sections)
        {
            if (FindSectionIndex(sections, "ACTIVE EXCEPTION TYPES ON THREADS") >= 0)
                return;

            int sourceIndex = FindSectionIndex(sections, "THREADS WITH ACTIVE EXCEPTIONS");
            if (sourceIndex < 0)
                return;

            var exceptionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in sections[sourceIndex].Lines)
            {
                const string prefix = "  Exception: ";
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string type = line[prefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(type))
                    continue;

                if (exceptionCounts.TryGetValue(type, out int count))
                    exceptionCounts[type] = count + 1;
                else
                    exceptionCounts[type] = 1;
            }

            var lines = exceptionCounts.Count == 0
                ? new List<string> { "No active exception-type distribution available." }
                : exceptionCounts.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Key}: {kvp.Value:N0}").ToList();

            sections.Insert(sourceIndex, new ReportSection("ACTIVE EXCEPTION TYPES ON THREADS", lines));
        }

        private static void SynthesizeThreadGroups(List<ReportSection> sections)
        {
            if (FindSectionIndex(sections, "THREAD GROUPS") >= 0)
                return;

            int triageIndex = FindSectionIndex(sections, "THREAD TRIAGE SUMMARY");
            if (triageIndex < 0)
                return;

            int locked = CountThreads(sections, "THREADS WITH LOCKS");
            int blocked = CountThreads(sections, "POTENTIALLY BLOCKED THREADS");
            int activeExceptions = CountThreads(sections, "THREADS WITH ACTIVE EXCEPTIONS");

            var lines = new List<string>
            {
                $"Lock-holding threads: {locked:N0}",
                $"Potentially blocked threads: {blocked:N0}",
                $"Threads with active exceptions: {activeExceptions:N0}",
                "Use the detailed sections above to inspect each thread group and correlate hotspots."
            };

            sections.Insert(triageIndex + 1, new ReportSection("THREAD GROUPS", lines));
        }

        private static void SynthesizeLockCausalityChain(List<ReportSection> sections)
        {
            if (FindSectionIndex(sections, "LOCK CAUSALITY CHAIN") >= 0)
                return;

            int summaryIndex = FindSectionIndex(sections, "LOCK CONTENTION SUMMARY");
            int hotspotsIndex = FindSectionIndex(sections, "LOCK CONTENTION HOTSPOTS");
            if (summaryIndex < 0 || hotspotsIndex < 0)
                return;

            var lines = new List<string>
            {
                "Lock causality view is derived from contention summary and hotspots.",
                "If hotspots are empty, no actionable lock-causality chain was detected in this snapshot."
            };

            sections.Insert(hotspotsIndex + 1, new ReportSection("LOCK CAUSALITY CHAIN", lines));
        }

        private static void SynthesizeRootAndStaticSections(List<ReportSection> sections)
        {
            AddAlias(sections, "TOP TYPES KEPT ALIVE", "ROOTED OBJECTS ANALYSIS");
            AddAlias(sections, "STATIC ROOT LEAK DETECTION", "STATIC FIELD REFERENCES");
        }

        private static int FindSectionIndex(List<ReportSection> sections, string title)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                if (string.Equals(sections[i].Title, title, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int CountThreads(List<ReportSection> sections, string title)
        {
            int index = FindSectionIndex(sections, title);
            if (index < 0)
                return 0;

            return sections[index].Lines.Count(l => l.StartsWith("Thread ", StringComparison.Ordinal));
        }

        private static void EnsureDistinctSectionTitles(List<ReportSection> sections)
        {
            var titleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sections.Count; i++)
            {
                ReportSection section = sections[i];
                string title = section.Title;

                if (!titleCounts.TryGetValue(title, out int count))
                {
                    titleCounts[title] = 1;
                    continue;
                }

                count++;
                titleCounts[title] = count;

                string disambiguatedTitle = $"{title} ({count})";
                sections[i] = new ReportSection(disambiguatedTitle, section.Lines);
            }
        }
    }
}

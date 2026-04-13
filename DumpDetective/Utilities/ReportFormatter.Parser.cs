namespace DumpDetective.Utilities
{
    internal static partial class ReportFormatter
    {
        private static List<ReportSection> ParseSections(string detailedReport) =>
            ParseSectionsFromLines(detailedReport.Replace("\r\n", "\n").Split('\n'));

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
    }
}

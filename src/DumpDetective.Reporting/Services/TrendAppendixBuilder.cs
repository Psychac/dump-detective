using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T7 Trend Appendix <see cref="AnalyzerDetailSection"/>:
/// resolved findings, analyzer coverage map, current dump analyzer summary, and trend limitations.
/// </summary>
internal static class TrendAppendixBuilder
{
    public static AnalyzerDetailSection Build(
        TrendReportData trendData,
        IReadOnlyList<AnalyzerRunResult> currentRuns)
    {
        var blocks = new List<SectionBlock>();

        // ── T7a: Resolved Findings ───────────────────────────────────────────
        blocks.Add(new CollapsibleSectionBeginBlock($"Resolved Findings ({trendData.ResolvedFindings.Count})"));
        if (trendData.ResolvedFindings.Count > 0)
        {
            var rows = trendData.ResolvedFindings
                .OrderByDescending(f => f.Severity)
                .Select(f => new TableRow([
                    new TableCell(f.Severity.ToString()),
                    new TableCell(f.Analyzer),
                    new TableCell(f.Category),
                    new TableCell(f.Title)
                ]))
                .ToArray();

            blocks.Add(new TableBlock(
                Caption: null,
                Headers: ["Severity", "Analyzer", "Category", "Title"],
                Rows: rows));
        }
        else
        {
            blocks.Add(new TextBlock("No findings were resolved between baseline and current."));
        }
        blocks.Add(new CollapsibleSectionEndBlock());

        // ── T7b: Analyzer Coverage Map ───────────────────────────────────────
        blocks.Add(new CollapsibleSectionBeginBlock("Analyzer Coverage Map"));

        IReadOnlyList<AnalysisSnapshot> snapshots = trendData.Snapshots;

        // Collect union of all analyzer names across all snapshots
        var allAnalyzers = new List<string>();
        var analyzerSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            foreach (AnalyzerRunResult run in snapshot.Runs)
            {
                if (analyzerSet.Add(run.AnalyzerName))
                    allAnalyzers.Add(run.AnalyzerName);
            }
        }
        allAnalyzers.Sort(StringComparer.OrdinalIgnoreCase);

        // Build headers: ["Analyzer", "S1", "S2", ..., "SN"]
        var headers = new List<string>(snapshots.Count + 1) { "Analyzer" };
        for (int i = 0; i < snapshots.Count; i++)
            headers.Add($"S{i + 1}");

        var coverageRows = new List<TableRow>(allAnalyzers.Count);
        foreach (string analyzerName in allAnalyzers)
        {
            var cells = new List<TableCell>(snapshots.Count + 1) { new TableCell(analyzerName) };
            foreach (AnalysisSnapshot snapshot in snapshots)
            {
                AnalyzerRunResult? run = null;
                foreach (AnalyzerRunResult r in snapshot.Runs)
                {
                    if (string.Equals(r.AnalyzerName, analyzerName, StringComparison.Ordinal))
                    { run = r; break; }
                }

                string symbol = run?.Status switch
                {
                    AnalyzerExecutionStatus.Success              => "✅",
                    AnalyzerExecutionStatus.Failed               => "⚠️",
                    AnalyzerExecutionStatus.SkippedByFilter       => "⏭",
                    AnalyzerExecutionStatus.SkippedByCancellation => "⏭",
                    null                                          => "—",
                    _                                             => "—"
                };
                cells.Add(new TableCell(symbol));
            }
            coverageRows.Add(new TableRow(cells));
        }

        if (coverageRows.Count > 0)
        {
            blocks.Add(new TableBlock(
                Caption: null,
                Headers: headers,
                Rows: coverageRows));
        }
        blocks.Add(new CollapsibleSectionEndBlock());

        // ── T7c: Current Dump Analyzer Run Summary ───────────────────────────
        blocks.Add(new HeadingBlock("Current Dump Analyzer Summary"));
        if (currentRuns.Count > 0)
        {
            var runRows = currentRuns
                .OrderBy(r => r.AnalyzerName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new TableRow([
                    new TableCell(r.AnalyzerName),
                    new TableCell(r.Status.ToString()),
                    new TableCell($"{r.Duration.TotalMilliseconds:F0} ms"),
                    new TableCell(r.FindingCount.ToString()),
                    new TableCell(r.WarningCount.ToString()),
                    new TableCell(r.ErrorMessage ?? "—")
                ]))
                .ToArray();

            blocks.Add(new TableBlock(
                Caption: null,
                Headers: ["Analyzer", "Status", "Duration", "Findings", "Warnings", "Error"],
                Rows: runRows));
        }
        else
        {
            blocks.Add(new TextBlock("No analyzer run data available for current dump."));
        }

        // ── T7d: Trend Limitations ───────────────────────────────────────────
        blocks.Add(new HeadingBlock("Trend Analysis Limitations"));
        blocks.Add(new TableBlock(
            Caption: null,
            Headers: ["Limitation", "Affected Sections"],
            Rows:
            [
                new([new TableCell("Metrics compared only when both dumps ran same analyzer version"), new TableCell("T4")]),
                new([new TableCell("Finding lifecycle uses fingerprint matching; fingerprint changes break continuity"), new TableCell("T2, T3, T7")]),
                new([new TableCell("Snapshot strip shows approximate total bytes from Memory Analysis only"), new TableCell("T5")]),
                new([new TableCell("Severity escalations detected only between first and last snapshot"), new TableCell("T3")]),
                new([new TableCell("New leak signals require memory analysis in both baseline and current"), new TableCell("T3")]),
            ]));

        return new AnalyzerDetailSection(
            AnalyzerName: "TrendAppendix",
            DisplayTitle: "Trend Appendix",
            SortOrder:    9999,
            Blocks:       blocks,
            SectionId:    "T7",
            Domain:       "Trend");
    }
}

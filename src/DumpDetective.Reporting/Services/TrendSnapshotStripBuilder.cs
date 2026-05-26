using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T5 Snapshot Strip <see cref="AnalyzerDetailSection"/>:
/// a compact table/card strip for all snapshots with key metrics and Δ vs baseline.
/// </summary>
internal static class TrendSnapshotStripBuilder
{
    public static AnalyzerDetailSection Build(IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        ulong? baselineBytes = TryGetTotalBytes(snapshots[0]);

        var rows = new List<TableRow>(snapshots.Count);
        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            ulong? bytes = TryGetTotalBytes(snapshot);

            string deltaVsBaseline = "—";
            if (snapshot.Index > 0 && bytes.HasValue && baselineBytes.HasValue && baselineBytes.Value > 0)
            {
                double pct = ((double)bytes.Value - baselineBytes.Value) / baselineBytes.Value * 100.0;
                deltaVsBaseline = $"{pct:+0.0;-0.0}%";
            }

            int criticalCount = snapshot.Findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warningCount  = snapshot.Findings.Count(f => f.Severity == FindingSeverity.Warning);

            string role = snapshot.Index == 0
                ? "Baseline"
                : snapshot.Index == snapshots.Count - 1
                    ? "Current"
                    : "Intermediate";

            rows.Add(new TableRow([
                new TableCell((snapshot.Index + 1).ToString()),
                new TableCell(Path.GetFileName(snapshot.DumpPath), LinkTarget: $"detail-{snapshot.Index}"),
                new TableCell(snapshot.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")),
                new TableCell(snapshot.Runs.Count.ToString()),
                new TableCell(snapshot.Findings.Count.ToString()),
                new TableCell(criticalCount.ToString()),
                new TableCell(warningCount.ToString()),
                new TableCell(bytes.HasValue ? FormatHelper.FormatBytes(bytes.Value) : "—"),
                new TableCell(deltaVsBaseline),
                new TableCell(role)
            ]));
        }

        var blocks = new List<SectionBlock>
        {
            new HeadingBlock("Snapshot Overview"),
            new TableBlock(
                Caption: "Snapshots",
                Headers: ["#", "Dump", "Generated (UTC)", "Analyzers", "Findings", "Critical", "Warning", "Total Bytes", "Δ vs Baseline", "Role"],
                Rows: rows)
        };

        return new AnalyzerDetailSection(
            AnalyzerName: "TrendSnapshotStrip",
            DisplayTitle: "Snapshot Overview",
            SortOrder:    50,
            Blocks:       blocks,
            SectionId:    "T5",
            Domain:       "Trend");
    }

    private static ulong? TryGetTotalBytes(AnalysisSnapshot snapshot)
    {
        // Try by known analyzer display names for memory
        if (snapshot.DomainResults.TryGetValue("Memory Analysis", out AnalyzerDomainResult? raw1) && raw1 is MemoryDomainResult m1)
            return m1.TotalBytes;
        if (snapshot.DomainResults.TryGetValue("MemoryAnalyzer", out AnalyzerDomainResult? raw2) && raw2 is MemoryDomainResult m2)
            return m2.TotalBytes;
        return null;
    }
}

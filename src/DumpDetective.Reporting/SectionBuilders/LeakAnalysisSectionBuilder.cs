using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LeakAnalysisSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.leak-analysis";
    public string DisplayTitle => "Leak Analysis";
    public int SortOrder => 1250;

    public bool CanBuild(AnalyzerResultSet results) => results.Get<LeakCandidateDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        LeakCandidateDomainResult? leak = results.Get<LeakCandidateDomainResult>();
        if (leak is null)
        {
            return new AnalyzerDetailSection(
                AnalyzerName: "Leak Candidate Analysis",
                DisplayTitle: DisplayTitle,
                SortOrder: SortOrder,
                Blocks: [T("Leak candidate analysis not available.")]);
        }

        var blocks = new List<SectionBlock>
        {
            H("LEAK CANDIDATES"),
            M("Total candidates", leak.TotalCandidates.ToString("N0"), leak.TotalCandidates),
            M("Heuristic only", leak.HeuristicOnly ? "Yes" : "No"),
        };

        if (leak.CandidatesByClass.Count > 0)
        {
            blocks.Add(H("CLASS BREAKDOWN"));
            foreach ((LeakClass leakClass, int count) in leak.CandidatesByClass.OrderByDescending(kvp => kvp.Value))
                blocks.Add(Li($"{leakClass}: {count:N0}"));
            blocks.Add(Blank());
        }

        if (leak.TopCandidates.Count > 0)
        {
            blocks.Add(H("TOP CANDIDATES"));
            blocks.Add(new TableBlock(
                Caption: "Top leak candidates by suspicion score",
                Headers: ["Type", "Score", "Total Size", "Instances", "Gen2%", "Class", "Root"],
                Rows: leak.TopCandidates.Select(candidate => Row(
                    Cell(candidate.TypeName),
                    Cell(candidate.SuspicionScore.ToString("N0"), candidate.SuspicionScore),
                    Cell(FormatBytes(candidate.TotalSize), (long)Math.Min(candidate.TotalSize, long.MaxValue)),
                    Cell(candidate.InstanceCount.ToString("N0"), candidate.InstanceCount),
                    Cell(candidate.Gen2Pct.ToString("F1") + "%", (long)Math.Round(candidate.Gen2Pct * 10)),
                    Cell(candidate.Classification.ToString()),
                    Cell(candidate.RootKind ?? "—")
                )).ToList()));

                    blocks.Add(T("Score factors: +30 for Gen2-heavy (>80%), +20 for >100 MB shallow size, +15 for finalizable types with >1,000 Gen2 objects, +10 each for static-rooted, pinned, and dependent-handle candidates, +5 for container-like types, +5 for reference-heavy shapes, and +5 for delegate/event-style types."));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Leak Candidate Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
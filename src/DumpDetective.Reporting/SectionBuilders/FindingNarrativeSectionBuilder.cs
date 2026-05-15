using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class FindingNarrativeSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => [];

    public string SectionId => "prof.finding-narratives";
    public string DisplayTitle => "Critical Finding Narratives";
    public int SortOrder => 1675;

    public bool CanBuild(AnalyzerResultSet results)
        => results.AllFindingsSorted().Any(static finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.Warning);

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        IReadOnlyList<InsightFinding> findings = results.AllFindingsSorted();
        var highSeverity = new List<InsightFinding>(findings.Count);

        foreach (InsightFinding finding in findings)
        {
            if (finding.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                highSeverity.Add(finding);
        }

        var blocks = new List<SectionBlock>
        {
            H("CAUSE -> EFFECT -> EVIDENCE -> FIX"),
            T("Narratives below condense high-severity findings into a decision-ready structure."),
            M("High-severity findings", highSeverity.Count.ToString("N0"), highSeverity.Count),
        };

        int limit = Math.Min(highSeverity.Count, 12);
        if (limit == 0)
        {
            blocks.Add(Blank());
            blocks.Add(T("No Critical or Warning findings were produced by the analyzers."));
        }
        else
        {
            blocks.Add(Blank());
            for (int i = 0; i < limit; i++)
            {
                InsightFinding finding = highSeverity[i];
                blocks.Add(CollapseBegin($"{finding.Severity}: {finding.Title}"));
                blocks.Add(H("CAUSE"));
                blocks.Add(T(BuildCause(finding)));
                blocks.Add(H("EFFECT"));
                blocks.Add(T(BuildEffect(finding)));
                blocks.Add(H("EVIDENCE"));
                blocks.Add(T(FormatEvidence(finding)));
                blocks.Add(H("FIX"));
                blocks.Add(T(FormatFix(finding)));
                if (finding.Tags is { Count: > 0 })
                {
                    blocks.Add(H("TAGS"));
                    foreach (string tag in finding.Tags)
                        blocks.Add(Li(tag));
                }
                blocks.Add(CollapseEnd());

                if (i + 1 < limit)
                    blocks.Add(Blank());
            }

            if (highSeverity.Count > limit)
            {
                blocks.Add(Blank());
                blocks.Add(T($"Showing top {limit:N0} narratives out of {highSeverity.Count:N0} high-severity findings."));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Finding Narratives",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static string BuildCause(InsightFinding finding)
    {
        string category = string.IsNullOrWhiteSpace(finding.Category) ? "the analyzer signal" : finding.Category;
        string analyzer = string.IsNullOrWhiteSpace(finding.Analyzer) ? "an analyzer" : finding.Analyzer;

        return finding.Severity switch
        {
            FindingSeverity.Critical => $"{analyzer} produced a Critical signal in {category}, which usually indicates a direct retention or pressure path that is already large enough to affect the process.",
            FindingSeverity.Warning => $"{analyzer} produced a Warning in {category}, which usually means the pattern is trending in the wrong direction or is close to a threshold.",
            _ => $"{analyzer} produced a non-critical signal in {category}."
        };
    }

    private static string BuildEffect(InsightFinding finding)
    {
        return finding.Severity switch
        {
            FindingSeverity.Critical => $"Expected effect: {finding.Title} can keep memory, threads, or handles alive long enough to cause visible slowdown, retention growth, or recovery failure.",
            FindingSeverity.Warning => $"Expected effect: {finding.Title} can grow into a production issue if the same allocation or retention path continues.",
            _ => $"Expected effect: {finding.Title} is notable but lower urgency."
        };
    }

    private static string FormatEvidence(InsightFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Evidence))
            return finding.Evidence;

        return "No evidence text was provided by the analyzer.";
    }

    private static string FormatFix(InsightFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Recommendation))
            return finding.Recommendation;

        return finding.Severity switch
        {
            FindingSeverity.Critical => "Investigate the owning path, remove the retention source, and verify the object graph no longer grows.",
            FindingSeverity.Warning => "Review the pattern, add a guardrail, and validate that the signal does not continue to increase.",
            _ => "Review the analyzer output and confirm whether the signal is actionable."
        };
    }
}
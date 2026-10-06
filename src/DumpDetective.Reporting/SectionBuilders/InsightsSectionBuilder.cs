using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class InsightsSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{

    public string SectionId => "X1";
    public string DisplayTitle => "Cross-Domain Insights";
    public int SortOrder => 1900;

    public bool CanBuild(AnalyzerResultSet results) => GetCrossDomainFindings(results).Count > 0;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        IReadOnlyList<Claim> findings = GetCrossDomainFindings(results);

        int criticalCount = 0;
        int warningCount = 0;
        double confidenceSum = 0.0;

        for (int i = 0; i < findings.Count; i++)
        {
            Claim finding = findings[i];
            switch (finding.Severity)
            {
                case Severity.Critical:
                    criticalCount++;
                    break;
                case Severity.Warning:
                    warningCount++;
                    break;
            }

            confidenceSum += finding.ReportConfidence();
        }

        double averageConfidence = findings.Count == 0 ? 0.0 : confidenceSum / findings.Count;

        var blocks = new List<SectionBlock>
        {
            H("RANKED FINDINGS"),
            T("Cross-analyzer findings are ranked by severity and surfaced with confidence and actionability context."),
            M("Total findings", findings.Count.ToString("N0"), findings.Count),
            M("Critical", criticalCount.ToString("N0"), criticalCount),
            M("Warning", warningCount.ToString("N0"), warningCount),
            M("Average confidence", averageConfidence.ToString("F2"), (long)Math.Round(averageConfidence * 100)),
        };

        if (findings.Count == 0)
        {
            blocks.Add(Blank());
            blocks.Add(T("No cross-analyzer findings were produced by the current analyzer set."));
        }
        else
        {
            blocks.Add(Blank());

            for (int i = 0; i < findings.Count; i++)
            {
                Claim finding = findings[i];
                blocks.Add(CollapseBegin($"[{i + 1}] {finding.Severity}: {finding.Title}"));
                blocks.Add(M("Analyzer", string.IsNullOrWhiteSpace(finding.Analyzer) ? "n/a" : finding.Analyzer));
                blocks.Add(M("Category", string.IsNullOrWhiteSpace(finding.Category) ? "n/a" : finding.Category));
                blocks.Add(M("Confidence", finding.ReportConfidence().ToString("F2"), (long)Math.Round(finding.ReportConfidence() * 100)));
                blocks.Add(T(FormatText("Evidence", finding.Narrative)));
                blocks.Add(T(FormatText("Recommendation", finding.Recommendation)));

                if (finding.Caveats.Count > 0)
                    blocks.Add(T(FormatText("Caveats", string.Join(" ", finding.Caveats))));

                if (finding.Tags is { Count: > 0 })
                {
                    blocks.Add(H("TAGS"));
                    for (int tagIndex = 0; tagIndex < finding.Tags.Count; tagIndex++)
                        blocks.Add(Li(finding.Tags[tagIndex]));
                }

                blocks.Add(CollapseEnd());

                if (i + 1 < findings.Count)
                    blocks.Add(Blank());
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "InsightEngine",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            SectionId: SectionId,
            Domain: "CrossDomain");
    }

    private static IReadOnlyList<Claim> GetCrossDomainFindings(AnalyzerResultSet results)
    {
        IReadOnlyList<Claim> allFindings = results.AllFindingsSorted();
        var crossDomain = new List<Claim>();

        for (int i = 0; i < allFindings.Count; i++)
        {
            Claim finding = allFindings[i];
            if (string.Equals(finding.Analyzer, "InsightEngine", StringComparison.OrdinalIgnoreCase)
                || finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase)))
            {
                crossDomain.Add(finding);
            }
        }

        return crossDomain;
    }

    private static string FormatText(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{label}: n/a";

        return $"{label}: {value}";
    }
}
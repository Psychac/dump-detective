using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StaticRootSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopRootsToShow = 8;

    public string AnalyzerName => "Static Root Leak Detection";
    public string DisplayTitle => "Static Roots";
    public int SortOrder => 600;

    public bool CanHandle(AnalyzerDomainResult result) => result is StaticRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StaticRootDomainResult)result;
        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Concerning Static Roots", $"{d.RootCount:N0}", d.RootCount),
            KM("Total Retained Bytes", FormatHelper.FormatBytes(d.TotalRetainedBytes), (double)d.TotalRetainedBytes),
        };

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var roots = d.TopRootsByRetainedBytes ?? [];
        if (roots.Count > 0)
        {
            int limit = Math.Min(roots.Count, TopRootsToShow);
            var rootRows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                var r = roots[i];
                rootRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(r.Name, 90)),
                    Cell(FormatHelper.FormatBytes(r.Bytes), (long)r.Bytes)]));
            }
            tables.Add(ST("Top roots by retained bytes", ["Field / Type", "Retained Bytes"], rootRows));
        }
        else
        {
            blocks.Add(T("No root-level retained-byte breakdown available."));
        }

        blocks.Add(d.RootCount >= 10
            ? T("High static-root pressure detected; review long-lived static ownership.")
            : T("Static-root pressure appears moderate in this dump."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}

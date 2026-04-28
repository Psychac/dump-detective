using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StaticRootSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopRootsToShow = 8;

    public string AnalyzerName => "Static Root Leak Detection";
    public int SortOrder => 27;

    public bool CanHandle(AnalyzerDomainResult result) => result is StaticRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StaticRootDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("STATIC FIELD REFERENCES"));
        blocks.Add(Divider());
        blocks.Add(M("Concerning Static Roots", $"{d.RootCount:N0}",                            d.RootCount));
        blocks.Add(M("Total Retained Bytes",    FormatHelper.FormatBytes(d.TotalRetainedBytes),  (double)d.TotalRetainedBytes));

        var roots = d.TopRootsByRetainedBytes ?? [];
        if (roots.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ROOTS BY RETAINED BYTES"));
            blocks.Add(Divider());

            var rootRows = new List<TableRow>(Math.Min(roots.Count, TopRootsToShow));
            int limit = Math.Min(roots.Count, TopRootsToShow);
            for (int i = 0; i < limit; i++)
            {
                var r = roots[i];
                rootRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(r.Name, 90)),
                    Cell(FormatHelper.FormatBytes(r.Bytes), (long)r.Bytes)]));
            }
            blocks.Add(new TableBlock("Top roots by retained bytes", ["Field / Type", "Retained Bytes"], rootRows));
        }
        else
        {
            blocks.Add(T("No root-level retained-byte breakdown available."));
        }

        blocks.Add(Blank());
        blocks.Add(H("RETENTION PRESSURE SIGNAL"));
        blocks.Add(Divider());
        blocks.Add(d.RootCount >= 10
            ? T("High static-root pressure detected; review long-lived static ownership.")
            : T("Static-root pressure appears moderate in this dump."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}

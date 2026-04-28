using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LockGraphSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Lock Graph Analysis";
    public int SortOrder => 70;

    public bool CanHandle(AnalyzerDomainResult result) => result is LockGraphDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (LockGraphDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("LOCK CONTENTION SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Held Locks",                $"{d.TotalHeldLocks:N0}",          d.TotalHeldLocks));
        blocks.Add(M("Contested Locks",           $"{d.ContestedLockCount:N0}",       d.ContestedLockCount));
        blocks.Add(M("Max Waiters on Single Lock", $"{d.MaxWaitersOnSingleLock:N0}",  d.MaxWaitersOnSingleLock));
        blocks.Add(M("Deadlock Candidates",        $"{d.DeadlockCandidateCount:N0}",  d.DeadlockCandidateCount));

        var topTypes = d.TopContestedLockTypes ?? [];
        if (topTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("LOCK CONTENTION HOTSPOTS"));
            blocks.Add(Divider());

            int limit = Math.Min(topTypes.Count, 8);
            var ctRows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
                ctRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(topTypes[i].Name, 70)),
                    Cell($"{topTypes[i].Count:N0} cumulative waiter(s)", topTypes[i].Count)]));
            blocks.Add(new TableBlock("Top contested lock types", ["Type", "Waiters"], ctRows));
        }

        blocks.Add(Blank());
        blocks.Add(H("DEADLOCK SIGNAL"));
        blocks.Add(Divider());
        if (d.DeadlockCandidateCount >= 2)
            blocks.Add(T("Probable deadlock pattern detected."));
        else if (d.ContestedLockCount > 0)
            blocks.Add(T("Lock contention present; monitor lock acquisition order."));
        else
            blocks.Add(T("No lock contention/deadlock candidates detected."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}

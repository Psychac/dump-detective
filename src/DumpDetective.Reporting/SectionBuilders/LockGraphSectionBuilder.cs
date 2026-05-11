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
        blocks.Add(M("Held Locks", $"{d.TotalHeldLocks:N0}", d.TotalHeldLocks));
        blocks.Add(M("Contested Locks", $"{d.ContestedLockCount:N0}", d.ContestedLockCount));
        blocks.Add(M("Max Waiters on Single Lock", $"{d.MaxWaitersOnSingleLock:N0}", d.MaxWaitersOnSingleLock));
        blocks.Add(M("Deadlock Candidates", $"{d.DeadlockCandidateCount:N0}", d.DeadlockCandidateCount));

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

        var contestedDetails = d.ContestedLockDetails ?? [];
        if (contestedDetails.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("CONTESTED LOCK OBJECTS"));
            blocks.Add(Divider());

            var clRows = new List<TableRow>(contestedDetails.Count);
            foreach (var cl in contestedDetails)
            {
                string owner = cl.OwnerManagedThreadId.HasValue
                    ? $"thread {cl.OwnerManagedThreadId.Value}"
                    : "unknown";
                clRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(cl.ObjectTypeName, 60)),
                    Cell($"0x{cl.ObjectAddress:x}"),
                    Cell($"{cl.WaitingThreadCount:N0}", cl.WaitingThreadCount),
                    Cell(owner),
                    Cell($"{cl.RecursionCount:N0}")]));
            }
            blocks.Add(new TableBlock("Contested lock objects",
                ["Type", "Address", "Waiters", "Owner Thread", "Recursion"],
                clRows));
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

        var deadlockDetails = d.DeadlockCandidateDetails ?? [];
        var deadlockOwnerIds = new HashSet<uint>();
        for (int i = 0; i < deadlockDetails.Count; i++)
            deadlockOwnerIds.Add(deadlockDetails[i].ManagedThreadId);

        if (deadlockDetails.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("DEADLOCK CANDIDATE THREADS"));
            blocks.Add(Divider());

            var dcRows = new List<TableRow>(deadlockDetails.Count);
            foreach (var dc in deadlockDetails)
            {
                string lockTypes = dc.LockObjectTypes.Count > 0
                    ? string.Join(", ", dc.LockObjectTypes)
                    : "(none)";
                string lockAddresses = dc.LockObjectAddresses.Count > 0
                    ? string.Join(", ", dc.LockObjectAddresses.Select(address => $"0x{address:x}"))
                    : "(none)";
                dcRows.Add(new TableRow([
                    Cell($"{dc.ManagedThreadId}"),
                    Cell($"{dc.OsThreadId}"),
                    Cell(FormatHelper.TruncateString(lockTypes, 60)),
                    Cell(FormatHelper.TruncateString(lockAddresses, 70)),
                    Cell(FormatHelper.TruncateString(dc.CycleSummary, 80))]));
            }
            blocks.Add(new TableBlock("Deadlock candidate threads",
                ["Managed ID", "OS Thread ID", "Held Lock Types", "Held Lock Addresses", "Summary"],
                dcRows));
        }

        if (deadlockOwnerIds.Count > 0 && contestedDetails.Count > 0)
        {
            var suspectedRows = new List<TableRow>();
            for (int i = 0; i < contestedDetails.Count; i++)
            {
                ContestedLockSnapshot cl = contestedDetails[i];
                if (!cl.OwnerManagedThreadId.HasValue || !deadlockOwnerIds.Contains(cl.OwnerManagedThreadId.Value))
                    continue;

                suspectedRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(cl.ObjectTypeName, 60)),
                    Cell($"0x{cl.ObjectAddress:x}"),
                    Cell(cl.OwnerManagedThreadId.Value.ToString(), cl.OwnerManagedThreadId.Value),
                    Cell($"{cl.WaitingThreadCount:N0}", cl.WaitingThreadCount),
                    Cell($"{cl.RecursionCount:N0}", cl.RecursionCount)]));
            }

            if (suspectedRows.Count > 0)
            {
                blocks.Add(Blank());
                blocks.Add(H("SUSPECTED DEADLOCK LOCKS"));
                blocks.Add(Divider());
                blocks.Add(T("Contested locks owned by threads that already participate in a deadlock candidate."));
                blocks.Add(new TableBlock("Suspected deadlock locks", ["Type", "Address", "Owner Thread", "Waiters", "Recursion"], suspectedRows));
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}

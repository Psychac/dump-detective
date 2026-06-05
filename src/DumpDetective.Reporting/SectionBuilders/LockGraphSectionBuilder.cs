using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LockGraphSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Lock Graph Analysis";
    public string DisplayTitle => "Lock Graph & Deadlocks";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is LockGraphDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (LockGraphDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.85, ["Derived from recorded wait chains and lock ownership."]),
        };

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["held_locks"] = new NumericMetricValue(d.TotalHeldLocks, MetricUnit.Count),
            ["contested_locks"] = new NumericMetricValue(d.ContestedLockCount, MetricUnit.Count),
            ["max_waiters_on_single_lock"] = new NumericMetricValue(d.MaxWaitersOnSingleLock, MetricUnit.Count),
            ["deadlock_candidates"] = new NumericMetricValue(d.DeadlockCandidateCount, MetricUnit.Count),
        };

        var topTypes = d.TopContestedLockTypes ?? [];
        if (topTypes.Count > 0)
        {
            int limit = Math.Min(topTypes.Count, 8);
            var ctRows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
                ctRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(topTypes[i].Name, 70)),
                    Cell($"{topTypes[i].Count:N0} cumulative waiter(s)", topTypes[i].Count)]));
            compactTables.Add(STCompact("Top contested lock types", new[] { CH("Type"), CH("Waiters") }, ctRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var contestedDetails = d.ContestedLockDetails ?? [];
        if (contestedDetails.Count > 0)
        {
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
            compactTables.Add(STCompact("Contested lock objects",
                new[] { CH("Type"), CH("Address"), CH("Waiters","number"), CH("Owner Thread"), CH("Recursion","number") },
                clRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

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
            compactTables.Add(STCompact("Deadlock candidate threads",
                new[] { CH("Managed ID","number"), CH("OS Thread ID","number"), CH("Held Lock Types"), CH("Held Lock Addresses"), CH("Summary") },
                dcRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                    blocks.Add(T("Contested locks owned by threads that already participate in a deadlock candidate."));
                    compactTables.Add(STCompact("Suspected deadlock locks",
                        new[] { CH("Type"), CH("Address"), CH("Owner Thread","number"), CH("Waiters","number"), CH("Recursion","number") },
                        suspectedRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
                }
        }

        SectionLeadFinding? leadFinding = null;
        if (d.DeadlockCandidateCount > 0)
            leadFinding = new SectionLeadFinding(
                Severity: "Critical",
                Title: $"Deadlock detected \u2014 {d.DeadlockCandidateCount} cycle(s) identified",
                Summary: $"{d.DeadlockCandidateCount} deadlock candidate(s) with {d.TotalHeldLocks:N0} held lock(s) and {d.ContestedLockCount:N0} contested lock object(s).",
                Recommendation: "Enforce a consistent lock acquisition order across all threads. Use lock timeouts or restructure code to eliminate nested locking.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.85,
                Caveats: ["Detection is based on recorded BlockingObjects; cooperative waits (e.g. SemaphoreSlim) may not appear."]);

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

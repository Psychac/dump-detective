using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class HangSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Hang Analysis";
    public int SortOrder => 15;

    public bool CanHandle(AnalyzerDomainResult result) => result is HangDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (HangDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("HANG INDICATORS"));
        blocks.Add(Divider());
        blocks.Add(M("Total Alive Threads", $"{d.TotalAliveThreads:N0}", d.TotalAliveThreads));
        blocks.Add(M("Waiting/Blocked Threads", $"{d.WaitingThreadCount:N0}", d.WaitingThreadCount));
        blocks.Add(M("Threads Holding Locks", $"{d.ThreadsHoldingLocks:N0}", d.ThreadsHoldingLocks));
        blocks.Add(M("Waiting Thread Percentage", $"{d.WaitingPercent:F1}%", d.WaitingPercent));
        blocks.Add(M("Pending Tasks", $"{d.PendingTasks:N0}", d.PendingTasks));
        blocks.Add(M("Faulted Tasks", $"{d.FaultedTasks:N0}", d.FaultedTasks));
        blocks.Add(M("Canceled Tasks", $"{d.CanceledTasks:N0}", d.CanceledTasks));
        if (d.TaskScanLimited)
            blocks.Add(T("Task scan limited due to heap size; totals may be partial."));

        if (d.WaitingPercent >= 80)
        {
            blocks.Add(Blank());
            blocks.Add(T("SEVERE HANG risk detected."));
        }
        else if (d.WaitingPercent >= 50)
        {
            blocks.Add(Blank());
            blocks.Add(T("POSSIBLE HANG risk detected."));
        }

        if (d.WaitCategoryBreakdown.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("WAIT CATEGORY BREAKDOWN"));
            blocks.Add(Divider());

            var catRows = new List<TableRow>(d.WaitCategoryBreakdown.Count);
            foreach (var kvp in d.WaitCategoryBreakdown)
                catRows.Add(new TableRow([Cell(kvp.Key), Cell($"{kvp.Value:N0}", kvp.Value)]));
            blocks.Add(new TableBlock("Wait category breakdown", ["Category", "Count"], catRows));
        }

        var waitingThreads = d.TopWaitingThreads ?? [];
        if (waitingThreads.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("WAITING THREADS"));
            blocks.Add(Divider());

            var wtRows = new List<TableRow>(waitingThreads.Count);
            for (int i = 0; i < waitingThreads.Count; i++)
            {
                var wt = waitingThreads[i];
                wtRows.Add(new TableRow([
                    Cell($"{wt.ThreadId}"),
                    Cell($"{wt.OSThreadId}"),
                    Cell(wt.WaitType),
                    Cell(wt.WaitReason),
                    Cell($"{wt.LockCount:N0}", wt.LockCount),
                    Cell(FormatHelper.TruncateString(wt.TopStackFrame, 80))]));
            }
            blocks.Add(new TableBlock("Blocking threads", ["ThreadId", "OSThreadId", "Category", "Reason", "Locks", "Top Frame"], wtRows));
        }

        var continuationTypes = d.TopContinuationTypes ?? [];
        if (continuationTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP CONTINUATION TYPES"));
            blocks.Add(Divider());

            var ctRows = new List<TableRow>(continuationTypes.Count);
            for (int i = 0; i < continuationTypes.Count; i++)
                ctRows.Add(new TableRow([Cell(continuationTypes[i].Name), Cell($"{continuationTypes[i].Count:N0}", continuationTypes[i].Count)]));
            blocks.Add(new TableBlock("Top continuation types", ["Type", "Count"], ctRows));
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}

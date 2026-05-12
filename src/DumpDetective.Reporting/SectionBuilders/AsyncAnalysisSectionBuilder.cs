using DumpDetective.Analysis.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AsyncAnalysisSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.async-analysis";
    public string DisplayTitle => "Async & Task";
    public int SortOrder => 1350;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<AsyncTaskDomainResult>() is not null
        || results.Get<HangDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        AsyncTaskDomainResult? asyncTasks = results.Get<AsyncTaskDomainResult>();
        HangDomainResult? hang = results.Get<HangDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("TASK SUMMARY"),
            T("Task counts, orphaned tasks, and continuation pressure are summarized here."),
        };

        if (asyncTasks is null)
        {
            blocks.Add(T("No async task result was available."));
            return new AnalyzerDetailSection("Async & Task", DisplayTitle, SortOrder, blocks);
        }

        blocks.Add(M("Total tasks", asyncTasks.TotalTasks.ToString("N0"), asyncTasks.TotalTasks));

        blocks.Add(new TableBlock(
            Caption: "Task status summary",
            Headers: ["Status", "Count"],
            Rows:
            [
                Row(Cell("Pending"), Cell(asyncTasks.PendingTasks.ToString("N0"), asyncTasks.PendingTasks)),
                Row(Cell("Running"), Cell(asyncTasks.RunningTasks.ToString("N0"), asyncTasks.RunningTasks)),
                Row(Cell("Faulted"), Cell(asyncTasks.FaultedTasks.ToString("N0"), asyncTasks.FaultedTasks)),
                Row(Cell("Canceled"), Cell(asyncTasks.CanceledTasks.ToString("N0"), asyncTasks.CanceledTasks)),
                Row(Cell("RanToCompletion"), Cell(asyncTasks.CompletedTasks.ToString("N0"), asyncTasks.CompletedTasks)),
                Row(Cell("Orphaned"), Cell(asyncTasks.OrphanedTasks.ToString("N0"), asyncTasks.OrphanedTasks)),
            ]));

        if (hang is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("THREAD POOL CONTEXT"));
            blocks.Add(M("Queued work items", hang.QueuedWorkItems.ToString("N0"), hang.QueuedWorkItems));
            blocks.Add(M("Runtime TP data", hang.RuntimeThreadPoolDataAvailable ? "Available" : "Unavailable", hang.RuntimeThreadPoolDataAvailable ? 1.0 : 0.0));
            blocks.Add(M("Task scan limited", hang.TaskScanLimited ? "Yes" : "No", hang.TaskScanLimited ? 1.0 : 0.0));
        }

        blocks.Add(M("Total task continuations", asyncTasks.TotalTaskContinuations.ToString("N0"), asyncTasks.TotalTaskContinuations));

        blocks.Add(Blank());
        blocks.Add(H("CONTINUATION CHAINS"));
        blocks.Add(M("Max continuation depth", asyncTasks.MaxContinuationDepth.ToString("N0"), asyncTasks.MaxContinuationDepth));
        blocks.Add(M("Average continuation depth", asyncTasks.AvgContinuationDepth.ToString("F1"), asyncTasks.AvgContinuationDepth));
        if (asyncTasks.MaxContinuationDepth > 50)
            blocks.Add(T("Deep continuation chains exceed 50 hops."));

        if (asyncTasks.TopContinuationTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopContinuationTypes.Count);
            for (int i = 0; i < asyncTasks.TopContinuationTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopContinuationTypes[i].Name), Cell(asyncTasks.TopContinuationTypes[i].Count.ToString("N0"), asyncTasks.TopContinuationTypes[i].Count)));

            blocks.Add(new TableBlock("Continuation types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopDeepestChains.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("DEEPEST CONTINUATION CHAINS"));
            var rows = new List<TableRow>(asyncTasks.TopDeepestChains.Count);
            for (int i = 0; i < asyncTasks.TopDeepestChains.Count; i++)
            {
                ContinuationChainSnapshot chain = asyncTasks.TopDeepestChains[i];
                string chainText = chain.ChainTypes.Count > 0 ? string.Join(" -> ", chain.ChainTypes) : chain.RootType;
                rows.Add(Row(
                    Cell($"0x{chain.RootAddress:X}"),
                    Cell(chain.RootType),
                    Cell(chain.Depth.ToString("N0"), chain.Depth),
                    Cell(FormatHelper.TruncateString(chainText, 80))));
            }

            blocks.Add(new TableBlock("Deepest continuation chains", ["Root Address", "Root Type", "Depth", "Chain"], rows));
        }

        if (asyncTasks.TopPendingTaskTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP PENDING TASK TYPES"));
            var rows = new List<TableRow>(asyncTasks.TopPendingTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopPendingTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopPendingTaskTypes[i].Name), Cell(asyncTasks.TopPendingTaskTypes[i].Count.ToString("N0"), asyncTasks.TopPendingTaskTypes[i].Count)));

            blocks.Add(new TableBlock("Pending task types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopFaultedTaskTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP FAULTED TASK TYPES"));
            var rows = new List<TableRow>(asyncTasks.TopFaultedTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopFaultedTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopFaultedTaskTypes[i].Name), Cell(asyncTasks.TopFaultedTaskTypes[i].Count.ToString("N0"), asyncTasks.TopFaultedTaskTypes[i].Count)));

            blocks.Add(new TableBlock("Faulted task types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopOrphanedTasks.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("ORPHANED TASKS"));
            var rows = new List<TableRow>(asyncTasks.TopOrphanedTasks.Count);
            for (int i = 0; i < asyncTasks.TopOrphanedTasks.Count; i++)
            {
                OrphanedTaskSnapshot snapshot = asyncTasks.TopOrphanedTasks[i];
                rows.Add(Row(
                    Cell($"0x{snapshot.Address:X}"),
                    Cell(snapshot.TaskType),
                    Cell(snapshot.ResultType ?? "—"),
                    Cell(FormatBytes(snapshot.Size), (long)Math.Min(snapshot.Size, long.MaxValue)),
                    Cell(snapshot.ExceptionType ?? "—"),
                    Cell(snapshot.ExceptionMessage is null ? "—" : FormatHelper.TruncateString(snapshot.ExceptionMessage, 80))));
            }

            blocks.Add(new TableBlock("Orphaned tasks", ["Address", "Task Type", "Result Type", "Size", "Exception Type", "Exception Message"], rows));
        }

        if (asyncTasks.TaskScanLimited)
            blocks.Add(T("Task scanning was limited; orphan and continuation totals may be partial."));

        return new AnalyzerDetailSection("Async & Task", DisplayTitle, SortOrder, blocks);
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
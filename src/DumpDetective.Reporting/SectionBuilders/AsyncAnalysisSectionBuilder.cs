using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AsyncAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Async Task Analysis";
    public string DisplayTitle => "Task Overview";
    public int SortOrder => 100;

    public bool CanHandle(AnalyzerDomainResult result) => result is AsyncTaskDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var asyncTasks = (AsyncTaskDomainResult)result;

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            T("Task counts, orphaned tasks, and continuation pressure are summarized here."),
        };

        SectionLeadFinding? leadFinding = null;
        if (asyncTasks.MaxContinuationDepth > 50)
        {
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Deep continuation chain detected (depth {asyncTasks.MaxContinuationDepth:N0})",
                Evidence: $"Max continuation chain depth is {asyncTasks.MaxContinuationDepth:N0}, exceeding the 50-hop warning threshold.",
                Recommendation: "Inspect the deepest chain table below. Deep chains can indicate async deadlocks or unbounded recursive continuations.",
                ConfidenceSymbol: "●●●●",
                ConfidenceScore: 0.85,
                Caveats: asyncTasks.TaskScanLimited ? ["Task scan was limited; chain depth may be underestimated."] : []);
        }

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total tasks",               asyncTasks.TotalTasks.ToString("N0"),               asyncTasks.TotalTasks),
            KM("Pending tasks",             asyncTasks.PendingTasks.ToString("N0"),              asyncTasks.PendingTasks),
            KM("Running tasks",             asyncTasks.RunningTasks.ToString("N0"),              asyncTasks.RunningTasks),
            KM("Faulted tasks",             asyncTasks.FaultedTasks.ToString("N0"),              asyncTasks.FaultedTasks),
            KM("Canceled tasks",            asyncTasks.CanceledTasks.ToString("N0"),             asyncTasks.CanceledTasks),
            KM("Completed tasks",           asyncTasks.CompletedTasks.ToString("N0"),            asyncTasks.CompletedTasks),
            KM("Orphaned tasks",            asyncTasks.OrphanedTasks.ToString("N0"),             asyncTasks.OrphanedTasks),
            KM("Total task continuations",  asyncTasks.TotalTaskContinuations.ToString("N0"),    asyncTasks.TotalTaskContinuations),
            KM("Max continuation depth",    asyncTasks.MaxContinuationDepth.ToString("N0"),      asyncTasks.MaxContinuationDepth),
            KM("Avg continuation depth",    asyncTasks.AvgContinuationDepth.ToString("F1"),      asyncTasks.AvgContinuationDepth),
        };

        tables.Add(ST(
            "Task status summary",
            ["Status", "Count"],
            [
                Row(Cell("Pending"),          Cell(asyncTasks.PendingTasks.ToString("N0"),     asyncTasks.PendingTasks)),
                Row(Cell("Running"),           Cell(asyncTasks.RunningTasks.ToString("N0"),     asyncTasks.RunningTasks)),
                Row(Cell("Faulted"),           Cell(asyncTasks.FaultedTasks.ToString("N0"),     asyncTasks.FaultedTasks)),
                Row(Cell("Canceled"),          Cell(asyncTasks.CanceledTasks.ToString("N0"),    asyncTasks.CanceledTasks)),
                Row(Cell("RanToCompletion"),   Cell(asyncTasks.CompletedTasks.ToString("N0"),   asyncTasks.CompletedTasks)),
                Row(Cell("Orphaned"),          Cell(asyncTasks.OrphanedTasks.ToString("N0"),    asyncTasks.OrphanedTasks)),
            ]));

        if (asyncTasks.TopContinuationTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopContinuationTypes.Count);
            for (int i = 0; i < asyncTasks.TopContinuationTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopContinuationTypes[i].Name), Cell(asyncTasks.TopContinuationTypes[i].Count.ToString("N0"), asyncTasks.TopContinuationTypes[i].Count)));
            tables.Add(ST("Continuation types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopDeepestChains.Count > 0)
        {
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
            tables.Add(ST("Deepest continuation chains", ["Root Address", "Root Type", "Depth", "Chain"], rows));
        }

        if (asyncTasks.TopPendingTaskTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopPendingTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopPendingTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopPendingTaskTypes[i].Name), Cell(asyncTasks.TopPendingTaskTypes[i].Count.ToString("N0"), asyncTasks.TopPendingTaskTypes[i].Count)));
            tables.Add(ST("Pending task types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopFaultedTaskTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopFaultedTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopFaultedTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopFaultedTaskTypes[i].Name), Cell(asyncTasks.TopFaultedTaskTypes[i].Count.ToString("N0"), asyncTasks.TopFaultedTaskTypes[i].Count)));
            tables.Add(ST("Faulted task types", ["Type", "Count"], rows));
        }

        if (asyncTasks.TopOrphanedTasks.Count > 0)
        {
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
            tables.Add(ST("Orphaned tasks",
                ["Address", "Task Type", "Result Type", "Size", "Exception Type", "Exception Message"],
                rows));
        }

        if (asyncTasks.TaskScanLimited)
            blocks.Add(T("Task scanning was limited; orphan and continuation totals may be partial."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
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
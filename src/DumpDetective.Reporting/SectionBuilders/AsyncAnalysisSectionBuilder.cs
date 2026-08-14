using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            T("Task counts, orphaned tasks, and continuation pressure are summarized here."),
        };

        SectionLeadFinding? leadFinding = null;
        if (asyncTasks.MaxContinuationDepth >= 15)
        {
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Deep continuation chain detected (depth {asyncTasks.MaxContinuationDepth:N0})",
                Summary: $"Max continuation chain depth is {asyncTasks.MaxContinuationDepth:N0}, exceeding the 15-hop warning threshold.",
                Recommendation: "Inspect the deepest chain table below. Deep chains can indicate async deadlocks or unbounded recursive continuations.",
                ConfidenceSymbol: "●●●●",
                ConfidenceScore: 0.85,
                Caveats: asyncTasks.TaskScanLimited ? ["Task scan was limited; chain depth may be underestimated."] : []);
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_tasks"] = new NumericMetricValue(asyncTasks.TotalTasks, MetricUnit.Count),
            ["pending_tasks"] = new NumericMetricValue(asyncTasks.PendingTasks, MetricUnit.Count),
            ["running_tasks"] = new NumericMetricValue(asyncTasks.RunningTasks, MetricUnit.Count),
            ["faulted_tasks"] = new NumericMetricValue(asyncTasks.FaultedTasks, MetricUnit.Count),
            ["canceled_tasks"] = new NumericMetricValue(asyncTasks.CanceledTasks, MetricUnit.Count),
            ["completed_tasks"] = new NumericMetricValue(asyncTasks.CompletedTasks, MetricUnit.Count),
            ["orphaned_tasks"] = new NumericMetricValue(asyncTasks.OrphanedTasks, MetricUnit.Count),
            ["total_task_continuations"] = new NumericMetricValue(asyncTasks.TotalTaskContinuations, MetricUnit.Count),
            ["max_continuation_depth"] = new NumericMetricValue(asyncTasks.MaxContinuationDepth, MetricUnit.Count),
            ["avg_continuation_depth"] = new NumericMetricValue(asyncTasks.AvgContinuationDepth, MetricUnit.Custom, asyncTasks.AvgContinuationDepth.ToString("F1")),
        };

        compactTables.Add(STCompact(
            "Task status summary",
            new[] { CH("Status"), CH("Count","number") },
            new[] {
                R("Pending", asyncTasks.PendingTasks),
                R("Running", asyncTasks.RunningTasks),
                R("Faulted", asyncTasks.FaultedTasks),
                R("Canceled", asyncTasks.CanceledTasks),
                R("RanToCompletion", asyncTasks.CompletedTasks),
                R("Orphaned", asyncTasks.OrphanedTasks),
            }));

        if (asyncTasks.TopContinuationTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopContinuationTypes.Count);
            for (int i = 0; i < asyncTasks.TopContinuationTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopContinuationTypes[i].Name), Cell(asyncTasks.TopContinuationTypes[i].Count.ToString("N0"), asyncTasks.TopContinuationTypes[i].Count)));
            compactTables.Add(STCompact("Continuation types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Deepest continuation chains", new[] { CH("Root Address"), CH("Root Type"), CH("Depth","number"), CH("Chain") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (asyncTasks.TopPendingTaskTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopPendingTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopPendingTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopPendingTaskTypes[i].Name), Cell(asyncTasks.TopPendingTaskTypes[i].Count.ToString("N0"), asyncTasks.TopPendingTaskTypes[i].Count)));
            compactTables.Add(STCompact("Pending task types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (asyncTasks.TopFaultedTaskTypes.Count > 0)
        {
            var rows = new List<TableRow>(asyncTasks.TopFaultedTaskTypes.Count);
            for (int i = 0; i < asyncTasks.TopFaultedTaskTypes.Count; i++)
                rows.Add(Row(Cell(asyncTasks.TopFaultedTaskTypes[i].Name), Cell(asyncTasks.TopFaultedTaskTypes[i].Count.ToString("N0"), asyncTasks.TopFaultedTaskTypes[i].Count)));
            compactTables.Add(STCompact("Faulted task types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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

            string orphanedTableTitle = "Orphaned tasks";
            if (asyncTasks.TopOrphanedTasks.Count < asyncTasks.OrphanedTasks)
                orphanedTableTitle += $" (showing {asyncTasks.TopOrphanedTasks.Count} of {asyncTasks.OrphanedTasks})";

            compactTables.Add(STCompact(orphanedTableTitle,
                new[] { CH("Address"), CH("Task Type"), CH("Result Type"), CH("Size","bytes"), CH("Exception Type"), CH("Exception Message") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (asyncTasks.TaskScanLimited)
            blocks.Add(T("Task scanning was limited; orphan and continuation totals may be partial."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
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
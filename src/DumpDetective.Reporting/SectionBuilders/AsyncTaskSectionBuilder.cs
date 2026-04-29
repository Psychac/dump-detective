using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AsyncTaskSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Async Task Analysis";
    public string DisplayTitle => "Async & Task Analysis";
    public int    SortOrder    => 28;

    public bool CanHandle(AnalyzerDomainResult result) => result is AsyncTaskDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AsyncTaskDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── §8.1 Task Summary ─────────────────────────────────────────────────
        blocks.Add(H("TASK SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Tasks",           $"{d.TotalTasks:N0}",                                      d.TotalTasks));
        blocks.Add(M("Pending",               $"{d.PendingTasks:N0}",                                    d.PendingTasks));
        blocks.Add(M("Running",               $"{d.RunningTasks:N0}",                                    d.RunningTasks));
        blocks.Add(M("Faulted",               $"{d.FaultedTasks:N0}",                                    d.FaultedTasks));
        blocks.Add(M("Canceled",              $"{d.CanceledTasks:N0}",                                   d.CanceledTasks));
        blocks.Add(M("Completed",             $"{d.CompletedTasks:N0}",                                  d.CompletedTasks));
        blocks.Add(M("Orphaned",              $"{d.OrphanedTasks:N0}",                                   d.OrphanedTasks));
        if (d.TaskScanLimited)
            blocks.Add(M("Scan Limited",      "Yes — results may be incomplete",                         0));

        // ── §8.3 Continuation Chains ─────────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("CONTINUATION CHAINS"));
        blocks.Add(Divider());
        blocks.Add(M("Max Chain Depth",       $"{d.MaxContinuationDepth}",                               d.MaxContinuationDepth));
        blocks.Add(M("Avg Chain Depth",       $"{d.AvgContinuationDepth:F1}",                            d.AvgContinuationDepth));

        if (d.TopContinuationTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP CONTINUATION TYPES"));
            blocks.Add(CollapseBegin("Top continuation types by count"));
            var rows = new List<TableRow>(d.TopContinuationTypes.Count);
            for (int i = 0; i < d.TopContinuationTypes.Count; i++)
            {
                var entry = d.TopContinuationTypes[i];
                rows.Add(Row(Cell(entry.Name), Cell($"{entry.Count:N0}", entry.Count)));
            }
            blocks.Add(new TableBlock("Continuation types", ["Type", "Count"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── §8.1 Top Pending Task Types ───────────────────────────────────────
        if (d.TopPendingTaskTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP PENDING TASK TYPES"));
            blocks.Add(CollapseBegin("Pending task types ranked by count"));
            var rows = new List<TableRow>(d.TopPendingTaskTypes.Count);
            for (int i = 0; i < d.TopPendingTaskTypes.Count; i++)
            {
                var entry = d.TopPendingTaskTypes[i];
                rows.Add(Row(Cell(entry.Name), Cell($"{entry.Count:N0}", entry.Count)));
            }
            blocks.Add(new TableBlock("Pending task types", ["Type", "Count"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── §8.1 Top Faulted Task Types ───────────────────────────────────────
        if (d.TopFaultedTaskTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP FAULTED TASK TYPES"));
            blocks.Add(CollapseBegin("Faulted task types ranked by count"));
            var rows = new List<TableRow>(d.TopFaultedTaskTypes.Count);
            for (int i = 0; i < d.TopFaultedTaskTypes.Count; i++)
            {
                var entry = d.TopFaultedTaskTypes[i];
                rows.Add(Row(Cell(entry.Name), Cell($"{entry.Count:N0}", entry.Count)));
            }
            blocks.Add(new TableBlock("Faulted task types", ["Type", "Count"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── §8.2 Orphaned Tasks ───────────────────────────────────────────────
        if (d.TopOrphanedTasks.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("ORPHANED TASKS (NO CONTINUATION)"));
            blocks.Add(CollapseBegin("Orphaned tasks — never awaited or missing continuation"));
            var rows = new List<TableRow>(d.TopOrphanedTasks.Count);
            for (int i = 0; i < d.TopOrphanedTasks.Count; i++)
            {
                var t = d.TopOrphanedTasks[i];
                rows.Add(Row(
                    Cell($"0x{t.Address:X}"),
                    Cell(t.TaskType),
                    Cell(t.ResultType ?? "—"),
                    Cell(FormatHelper.FormatBytes(t.Size), (long)t.Size)));
            }
            blocks.Add(new TableBlock("Orphaned tasks", ["Address", "Task Type", "Result Type", "Size"], rows));
            blocks.Add(CollapseEnd());
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}

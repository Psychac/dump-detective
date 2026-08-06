using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class TimerLeakSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Timer Leak Analysis";
    public string DisplayTitle => "Timer Leak Analysis";
    public int SortOrder => 740;

    public bool CanHandle(AnalyzerDomainResult result) => result is TimerLeakDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (TimerLeakDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["logical_timers"] = new NumericMetricValue(d.LogicalTimerCount, MetricUnit.Count),
            ["total_timer_objects"] = new NumericMetricValue(d.TotalTimers, MetricUnit.Count),
            ["threading_timer"] = new NumericMetricValue(d.ThreadingTimerCount, MetricUnit.Count),
            ["timers_timer"] = new NumericMetricValue(d.TimersTimerCount, MetricUnit.Count),
            ["timer_queue_timer"] = new NumericMetricValue(d.TimerQueueTimerCount, MetricUnit.Count),
            ["timer_holder"] = new NumericMetricValue(d.TimerHolderCount, MetricUnit.Count),
            ["periodic_timer"] = new NumericMetricValue(d.PeriodicTimerCount, MetricUnit.Count),
            ["total_heap_size"] = new NumericMetricValue((double)d.TotalBytes, MetricUnit.Bytes, FormatBytes(d.TotalBytes)),
        };

        if (!d.TimersFound)
        {
            blocks.Add(T("No timer-related framework objects were detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks, KeyMetrics: keyMetrics);
        }

        if (d.ByType.Count > 0)
        {
            var rows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                TimerObjectTypeSummary t = d.ByType[i];
                rows.Add(Row(
                    Cell(t.TypeName),
                    Cell($"{t.Count:N0}", t.Count),
                    Cell(FormatBytes(t.TotalBytes))));
            }

            compactTables.Add(STCompact("Timer-related objects by type", new[] { CH("Type"), CH("Count","number"), CH("Heap Size","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if ((d.TimerHolderCount + d.TimerQueueTimerCount) >= 50)
        {
            blocks.Add(T("Timer queue pressure is elevated. This usually means timers outlive their intended scope and are not disposed promptly."));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

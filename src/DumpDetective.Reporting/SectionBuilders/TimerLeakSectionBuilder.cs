using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
            var hasTruncatedSearch = false;

            for (int i = 0; i < d.ByType.Count; i++)
            {
                TimerObjectTypeSummary t = d.ByType[i];
                string displayName = t.TypeName;

                if (t.Evidence?.RootPathSearchTruncated == true)
                {
                    displayName += " ⚠";
                    hasTruncatedSearch = true;
                }

                rows.Add(Row(
                    Cell(displayName),
                    Cell($"{t.Count:N0}", t.Count),
                    Cell(FormatBytes(t.TotalBytes))));
            }

            var compactRows = new List<CompactRow>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var cells = rows[i].Cells;
                var values = new object?[cells.Count];
                for (int j = 0; j < cells.Count; j++)
                {
                    values[j] = cells[j].RawValue ?? (object?)cells[j].Display;
                }
                compactRows.Add(R(values));
            }

            compactTables.Add(STCompact("Timer-related objects by type", new[] { CH("Type"), CH("Count","number"), CH("Heap Size","bytes") }, compactRows));

            if (hasTruncatedSearch)
            {
                blocks.Add(T("⚠ Some timer types have truncated root path searches. Evidence confidence may be partial."));
            }

            var hasEvidence = false;
            for (int i = 0; i < d.ByType.Count; i++)
            {
                if (d.ByType[i].Evidence?.SampleRootPath != null)
                {
                    hasEvidence = true;
                    break;
                }
            }

            if (hasEvidence)
            {
                blocks.Add(H("Retention evidence"));
                for (int i = 0; i < d.ByType.Count; i++)
                {
                    TimerObjectTypeSummary t = d.ByType[i];
                    if (t.Evidence?.SampleRootPath != null)
                    {
                        blocks.Add(T($"**{t.TypeName}**", 1));
                        blocks.Add(T(t.Evidence.SampleRootPath, 2));
                        blocks.Add(Blank());
                    }
                }
            }
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

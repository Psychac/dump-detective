using DumpDetective.Analysis.Models;
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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Timer Objects",     $"{d.TotalTimers:N0}", d.TotalTimers),
            KM("Threading.Timer",         $"{d.ThreadingTimerCount:N0}", d.ThreadingTimerCount),
            KM("Timers.Timer",            $"{d.TimersTimerCount:N0}", d.TimersTimerCount),
            KM("TimerQueueTimer",         $"{d.TimerQueueTimerCount:N0}", d.TimerQueueTimerCount),
            KM("TimerHolder",             $"{d.TimerHolderCount:N0}", d.TimerHolderCount),
            KM("Total Heap Size",         FormatBytes(d.TotalBytes)),
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

            tables.Add(ST("Timer-related objects by type", ["Type", "Count", "Heap Size"], rows));
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
            Tables: tables.Count > 0 ? tables : null);
    }
}

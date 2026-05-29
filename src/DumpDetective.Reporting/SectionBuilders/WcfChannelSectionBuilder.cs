using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class WcfChannelSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "WCF Channel Analysis";
    public string DisplayTitle => "WCF Channel Pool Analysis";
    public int SortOrder => 720;

    public bool CanHandle(AnalyzerDomainResult result) => result is WcfChannelDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (WcfChannelDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Channels", $"{d.TotalChannels:N0}",   d.TotalChannels),
            KM("Opened",         $"{d.OpenedChannels:N0}",  d.OpenedChannels),
            KM("Faulted",        $"{d.FaultedChannels:N0}", d.FaultedChannels),
            KM("Closed",         $"{d.ClosedChannels:N0}",  d.ClosedChannels),
        };

        if (!d.WcfPresent)
        {
            blocks.Add(new TextBlock("No WCF channel or service model objects detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        // Per-type summary table
        if (d.ByType.Count > 0)
        {
            var typeRows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                WcfChannelTypeSummary t = d.ByType[i];
                typeRows.Add(new TableRow([
                    Cell(t.TypeName),
                    Cell($"{t.TotalCount:N0}",   t.TotalCount),
                    Cell($"{t.OpenedCount:N0}",  t.OpenedCount),
                    Cell($"{t.FaultedCount:N0}", t.FaultedCount),
                    Cell($"{t.ClosedCount:N0}",  t.ClosedCount),
                    Cell($"{t.OtherCount:N0}",   t.OtherCount),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            tables.Add(ST("Channel objects by type",
                ["Type", "Total", "Opened", "Faulted", "Closed", "Other", "Heap Size"],
                typeRows));
        }

        // Top faulted channels
        if (d.TopFaultedChannels.Count > 0)
        {
            var faultRows = new List<TableRow>(d.TopFaultedChannels.Count);
            for (int i = 0; i < d.TopFaultedChannels.Count; i++)
            {
                WcfChannelSnapshot s = d.TopFaultedChannels[i];
                string shortType = s.TypeName.Contains('.') ? s.TypeName.Split('.')[^1] : s.TypeName;
                faultRows.Add(new TableRow([
                    Cell(shortType),
                    Cell($"0x{s.Address:X}"),
                    Cell(s.StateLabel),
                ]));
            }
            tables.Add(ST("Faulted channel instances",
                ["Type", "Address", "State"],
                faultRows));
        }

        if (d.StateScanCapped)
            blocks.Add(new TextBlock("Note: state sampling was capped. State-based counts may be lower than actual totals."));

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables);
    }
}

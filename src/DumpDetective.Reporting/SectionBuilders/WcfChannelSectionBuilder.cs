using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_channels"] = new NumericMetricValue(d.TotalChannels, MetricUnit.Count),
            ["opened"] = new NumericMetricValue(d.OpenedChannels, MetricUnit.Count),
            ["faulted"] = new NumericMetricValue(d.FaultedChannels, MetricUnit.Count),
            ["closed"] = new NumericMetricValue(d.ClosedChannels, MetricUnit.Count),
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
            compactTables.Add(STCompact("Channel objects by type", new[] { CH("Type"), CH("Total","number"), CH("Opened","number"), CH("Faulted","number"), CH("Closed","number"), CH("Other","number"), CH("Heap Size","bytes") }, typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Faulted channel instances", new[] { CH("Type"), CH("Address"), CH("State") }, faultRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.StateScanCapped)
            blocks.Add(new TextBlock("Note: state sampling was capped. State-based counts may be lower than actual totals."));

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class DbConnectionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "DB Connection Analysis";
    public string DisplayTitle => "DB Connection Pool Analysis";
    public int SortOrder => 710;

    public bool CanHandle(AnalyzerDomainResult result) => result is DbConnectionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DbConnectionDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        // Key metrics strip
        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Connections",  $"{d.TotalConnections:N0}",  d.TotalConnections),
            KM("Open",               $"{d.OpenConnections:N0}",   d.OpenConnections),
            KM("Closed",             $"{d.ClosedConnections:N0}", d.ClosedConnections),
            KM("Other",              $"{d.OtherConnections:N0}",  d.OtherConnections),
        };

        if (!d.ConnectionsFound)
        {
            blocks.Add(new TextBlock("No ADO.NET or third-party DB connection objects detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        // Per-type table
        if (d.ByType.Count > 0)
        {
            var typeRows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                DbConnectionTypeSummary t = d.ByType[i];
                typeRows.Add(new TableRow([
                    Cell(t.TypeName),
                    Cell($"{t.TotalCount:N0}", t.TotalCount),
                    Cell($"{t.OpenCount:N0}",   t.OpenCount),
                    Cell($"{t.ClosedCount:N0}", t.ClosedCount),
                    Cell($"{t.OtherCount:N0}",  t.OtherCount),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            tables.Add(ST("Connection objects by type",
                ["Type", "Total", "Open", "Closed", "Other", "Heap Size"],
                typeRows));
        }

        // Top open connections table
        if (d.TopOpenConnections.Count > 0)
        {
            var openRows = new List<TableRow>(d.TopOpenConnections.Count);
            for (int i = 0; i < d.TopOpenConnections.Count; i++)
            {
                DbConnectionSnapshot s = d.TopOpenConnections[i];
                string shortType = s.TypeName.Contains('.') ? s.TypeName.Split('.')[^1] : s.TypeName;
                openRows.Add(new TableRow([
                    Cell(shortType),
                    Cell($"0x{s.Address:X}"),
                    Cell(s.StateLabel),
                ]));
            }
            tables.Add(ST("Top open connections",
                ["Type", "Address", "State"],
                openRows));
        }

        if (d.StateScanCapped)
            blocks.Add(new TextBlock("Note: state sampling was capped. Counts above may be lower than actual totals."));

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables);
    }
}

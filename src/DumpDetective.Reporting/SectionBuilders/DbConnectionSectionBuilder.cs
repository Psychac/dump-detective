using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        // Key metrics strip
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_connections"] = new NumericMetricValue(d.TotalConnections, MetricUnit.Count),
            ["open_connections"] = new NumericMetricValue(d.OpenConnections, MetricUnit.Count),
            ["gen2_open_connections"] = new NumericMetricValue(d.Gen2OpenConnections, MetricUnit.Count),
            ["gen0_open_connections"] = new NumericMetricValue(d.Gen0OpenConnections, MetricUnit.Count),
            ["closed_connections"] = new NumericMetricValue(d.ClosedConnections, MetricUnit.Count),
            ["broken_connections"] = new NumericMetricValue(d.BrokenConnections, MetricUnit.Count),
            ["other_connections"] = new NumericMetricValue(d.OtherConnections, MetricUnit.Count),
            ["unknown_state_connections"] = new NumericMetricValue(d.UnknownStateConnections, MetricUnit.Count),
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
                    Cell($"{t.BrokenCount:N0}", t.BrokenCount),
                    Cell($"{t.OtherCount:N0}",  t.OtherCount),
                    Cell($"{t.UnknownStateCount:N0}", t.UnknownStateCount),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            compactTables.Add(STCompact("Connection objects by type",
                new[] { CH("Type"), CH("Total","number"), CH("Open","number"), CH("Closed","number"), CH("Broken","number"), CH("Other","number"), CH("Unknown","number"), CH("Heap Size","bytes") },
                typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Top open connections",
                new[] { CH("Type"), CH("Address"), CH("State") },
                openRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Top exhausted pools by connection string
        if (d.TopPools.Count > 0)
        {
            var poolRows = new List<TableRow>(d.TopPools.Count);
            for (int i = 0; i < d.TopPools.Count; i++)
            {
                PoolSummary p = d.TopPools[i];
                int totalInPool = p.TotalConnections;
                int openInPool = p.OpenConnections;
                double utilisation = totalInPool > 0 ? (100.0 * openInPool / totalInPool) : 0;
                poolRows.Add(new TableRow([
                    Cell(p.PoolIdentifier),
                    Cell($"{openInPool:N0}", openInPool),
                    Cell($"{totalInPool:N0}", totalInPool),
                    Cell($"{utilisation:F1}%"),
                ]));
            }
            compactTables.Add(STCompact("Top exhausted connection pools",
                new[] { CH("Pool (Server/Database)"), CH("Open","number"), CH("Sampled Total","number"), CH("Utilisation %","percent") },
                poolRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Add generation breakdown note
        if (d.OpenConnections > 0)
        {
            string genNote = $"Open connections breakdown by GC generation: Gen2 (long-lived, {d.Gen2OpenConnections:N0}) likely indicates leaks; Gen0 ({d.Gen0OpenConnections:N0}) likely in-flight transactions.";
            blocks.Add(new TextBlock(genNote));
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

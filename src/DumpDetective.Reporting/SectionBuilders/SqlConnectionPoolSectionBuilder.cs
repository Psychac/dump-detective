using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class SqlConnectionPoolSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "SQL Connection Pool Analysis";
    public string DisplayTitle => "SQL Connection Pool Analysis";
    public int SortOrder => 725;

    public bool CanHandle(AnalyzerDomainResult result) => result is SqlConnectionPoolDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SqlConnectionPoolDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new Dictionary<string, MetricValue>
        {
            ["total_pools"] = new NumericMetricValue(d.TotalPools, MetricUnit.Count),
            ["pools_near_capacity"] = new NumericMetricValue(d.PoolsNearCapacity, MetricUnit.Count),
        };

        if (!d.PoolsFound)
        {
            blocks.Add(new TextBlock(
                "No ADO.NET connection-pool manager objects detected on the managed heap " +
                "(SqlClient-family providers only)."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        var poolRows = new List<TableRow>(d.Pools.Count);
        var sorted = d.Pools.OrderByDescending(SqlConnectionPoolAnalyzer.UtilizationPercent).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            SqlConnectionPoolSnapshot p = sorted[i];
            double pct = SqlConnectionPoolAnalyzer.UtilizationPercent(p);
            string pctDisplay = pct >= 0 ? $"{pct:F1}%" : "(unknown)";
            string maxDisplay = p.MaxPoolSize >= 0 ? $"{p.MaxPoolSize:N0}" : "(unknown)";
            string minDisplay = p.MinPoolSize >= 0 ? $"{p.MinPoolSize:N0}" : "(unknown)";
            poolRows.Add(new TableRow([
                Cell($"0x{p.Address:X}"),
                Cell($"{p.CurrentSize:N0}", p.CurrentSize),
                Cell(maxDisplay),
                Cell(minDisplay),
                Cell(pctDisplay, pct),
                Cell(p.AnonymisedConnectionString ?? "(unknown)"),
            ]));
        }
        compactTables.Add(STCompact("Connection pools",
            new[] { CH("Pool Address"), CH("Current Size","number"), CH("Max Pool Size"), CH("Min Pool Size"), CH("Utilisation %","percent"), CH("Connection String (redacted)") },
            poolRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        if (d.PoolsNearCapacity > 0)
        {
            blocks.Add(new TextBlock(
                $"{d.PoolsNearCapacity:N0} of {d.TotalPools:N0} pools are at or above 80% of Max Pool Size. " +
                "These counters are read directly from the pool manager object, not estimated from sampled " +
                "connection state."));
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class SqlTransactionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "SQL Transaction Analysis";
    public string DisplayTitle => "SQL Transaction Analysis";
    public int SortOrder => 715;

    public bool CanHandle(AnalyzerDomainResult result) => result is SqlTransactionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SqlTransactionDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new Dictionary<string, MetricValue>
        {
            ["total_transactions"] = new NumericMetricValue(d.TotalTransactions, MetricUnit.Count),
            ["active_transactions"] = new NumericMetricValue(d.ActiveCount, MetricUnit.Count),
            ["disposed_transactions"] = new NumericMetricValue(d.DisposedCount, MetricUnit.Count),
            ["other_transactions"] = new NumericMetricValue(d.OtherCount, MetricUnit.Count),
        };

        if (!d.TransactionsFound)
        {
            blocks.Add(new TextBlock("No ADO.NET transaction objects detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        if (d.ByType.Count > 0)
        {
            var typeRows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                SqlTransactionTypeSummary t = d.ByType[i];
                typeRows.Add(new TableRow([
                    Cell(t.TypeName),
                    Cell($"{t.TotalCount:N0}",    t.TotalCount),
                    Cell($"{t.ActiveCount:N0}",   t.ActiveCount),
                    Cell($"{t.DisposedCount:N0}", t.DisposedCount),
                    Cell($"{t.OtherCount:N0}",    t.OtherCount),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            compactTables.Add(STCompact("Transaction objects by type",
                new[] { CH("Type"), CH("Total","number"), CH("Active","number"), CH("Disposed","number"), CH("Other","number"), CH("Heap Size","bytes") },
                typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopActiveTransactions.Count > 0)
        {
            var activeRows = new List<TableRow>(d.TopActiveTransactions.Count);
            for (int i = 0; i < d.TopActiveTransactions.Count; i++)
            {
                SqlTransactionSnapshot s = d.TopActiveTransactions[i];
                string shortType = s.TypeName.Contains('.') ? s.TypeName.Split('.')[^1] : s.TypeName;
                activeRows.Add(new TableRow([
                    Cell(shortType),
                    Cell($"0x{s.Address:X}"),
                    Cell(s.ConnectionAddress is ulong ca ? $"0x{ca:X}" : "(unknown)"),
                ]));
            }
            compactTables.Add(STCompact("Active transactions holding a connection open",
                new[] { CH("Type"), CH("Address"), CH("Connection Address") },
                activeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ActiveCount > 0)
        {
            blocks.Add(new TextBlock(
                $"{d.ActiveCount:N0} transaction objects still reference their owning connection. " +
                "These prevent the connection from returning to the pool even when it otherwise looks idle."));
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

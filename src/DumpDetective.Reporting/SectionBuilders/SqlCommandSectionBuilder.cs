using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class SqlCommandSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "SQL Command Analysis";
    public string DisplayTitle => "SQL Command Analysis";
    public int SortOrder => 720;

    public bool CanHandle(AnalyzerDomainResult result) => result is SqlCommandDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SqlCommandDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new Dictionary<string, MetricValue>
        {
            ["total_commands"] = new NumericMetricValue(d.TotalCommands, MetricUnit.Count),
            ["active_commands"] = new NumericMetricValue(d.ActiveCount, MetricUnit.Count),
            ["disposed_commands"] = new NumericMetricValue(d.DisposedCount, MetricUnit.Count),
        };

        if (!d.CommandsFound)
        {
            blocks.Add(new TextBlock("No ADO.NET command objects detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        if (d.ByType.Count > 0)
        {
            var typeRows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                SqlCommandTypeSummary t = d.ByType[i];
                typeRows.Add(new TableRow([
                    Cell(t.TypeName),
                    Cell($"{t.TotalCount:N0}",    t.TotalCount),
                    Cell($"{t.ActiveCount:N0}",   t.ActiveCount),
                    Cell($"{t.DisposedCount:N0}", t.DisposedCount),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            compactTables.Add(STCompact("Command objects by type",
                new[] { CH("Type"), CH("Total","number"), CH("Active","number"), CH("Detached","number"), CH("Heap Size","bytes") },
                typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopActiveCommands.Count > 0)
        {
            var activeRows = new List<TableRow>(d.TopActiveCommands.Count);
            for (int i = 0; i < d.TopActiveCommands.Count; i++)
            {
                SqlCommandSnapshot s = d.TopActiveCommands[i];
                string shortType = s.TypeName.Contains('.') ? s.TypeName.Split('.')[^1] : s.TypeName;
                activeRows.Add(new TableRow([
                    Cell(shortType),
                    Cell($"0x{s.Address:X}"),
                ]));
            }
            compactTables.Add(STCompact("Outstanding commands referencing a connection",
                new[] { CH("Type"), CH("Address") },
                activeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ActiveCount > 0)
        {
            blocks.Add(new TextBlock(
                $"{d.ActiveCount:N0} command objects still reference a connection object. ADO.NET providers do " +
                "not reliably clear this reference on Dispose(), so this reflects commands still wired to a " +
                "connection rather than a strict disposed/not-disposed distinction."));
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

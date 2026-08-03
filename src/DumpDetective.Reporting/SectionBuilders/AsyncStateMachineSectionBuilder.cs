using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AsyncStateMachineSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;
    private const int TopCaptureRows = 10;
    private const int TopSuspendedRows = 20;

    public string AnalyzerName => "Async State Machine Analysis";
    public string DisplayTitle => "Async State Machines";
    public int SortOrder => 200; // §23 async state machines

    public bool CanHandle(AnalyzerDomainResult result) => result is AsyncStateMachineDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AsyncStateMachineDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_state_machines"] = new NumericMetricValue(d.TotalStateMachines, MetricUnit.Count),
            ["total_memory"] = new NumericMetricValue((double)d.TotalStateMachineBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalStateMachineBytes)),
            ["distinct_types"] = new NumericMetricValue(d.TopStateMachineTypes.Count, MetricUnit.Count),
            ["suspended_methods"] = new NumericMetricValue(d.SuspendedMethodMap.Count, MetricUnit.Count),
        };
        if (d.ScanLimited)
            keyMetrics["scan_limit_reached"] = new EnumMetricValue("Yes — type candidate cap hit; results may be partial");

        if (d.TopStateMachineTypes.Count > 0)
        {
            blocks.Add(T("Each entry represents a distinct suspended async method. " +
                          "High counts for the same method indicate fire-and-forget patterns or unbounded parallelism."));
            int limit = Math.Min(d.TopStateMachineTypes.Count, TopTypeRows);
            compactTables.Add(STCompact(
                "Top async state machine types by instance count",
                new[] { CH("Type Name"), CH("Originating Method"), CH("Declaring Type"), CH("Count","number"), CH("Total Size","bytes"), CH("Sample State"), CH("Ref Fields","number"), CH("Gen2 Count","number"), CH("Gen2 %","percent") },
                BuildTypeRows(d.TopStateMachineTypes, limit).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopByCapturedSize.Count > 0)
        {
            blocks.Add(T("Async methods capture all variables referenced across await boundaries. " +
                          "Instances with large captured closures may indicate long-lived objects being retained unintentionally."));
            int limit = Math.Min(d.TopByCapturedSize.Count, TopCaptureRows);
            compactTables.Add(STCompact(
                "Top async state machine instances by captured reference bytes",
                new[] { CH("Address"), CH("Type Name"), CH("Captured Ref Bytes","bytes"), CH("Large Captures") },
                BuildCaptureRows(d.TopByCapturedSize, limit).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.SuspendedMethodMap.Count > 0)
        {
            blocks.Add(T("Methods with the most suspended instances. " +
                          "High counts for a single method typically indicate fire-and-forget usage or long-running awaits."));
            int limit = Math.Min(d.SuspendedMethodMap.Count, TopSuspendedRows);
            compactTables.Add(STCompact(
                "Suspended async methods by instance count",
                new[] { CH("Declaring Type"), CH("Method Name"), CH("Suspended Count","number"), CH("Total Size","bytes") },
                BuildSuspendedRows(d.SuspendedMethodMap, limit).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<StateMachineTypeProfile> types, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            StateMachineTypeProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell(FormatHelper.TruncateString(t.OriginatingMethod, 40)),
                Cell(FormatHelper.TruncateString(t.DeclaringType, 50)),
                Cell($"{t.Count:N0}",                       t.Count),
                Cell(FormatHelper.FormatBytes(t.TotalBytes)),
                Cell(t.SampleStateValue.ToString()),
                Cell($"{t.ReferenceFieldCount:N0}",         t.ReferenceFieldCount),
                Cell($"{t.Gen2Count:N0}",                   t.Gen2Count),
                Cell($"{t.Gen2Fraction * 100:F1}%",         t.Gen2Fraction),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildCaptureRows(IReadOnlyList<HighCaptureStateMachine> captures, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            HighCaptureStateMachine c = captures[i];
            string largeCapturesText = c.LargeCaptures.Count > 0
                ? string.Join("; ", c.LargeCaptures)
                : "none";
            rows.Add(new TableRow([
                Cell($"0x{c.Address:X}"),
                Cell(FormatHelper.TruncateString(c.TypeName, 70)),
                Cell(FormatHelper.FormatBytes(c.TotalCapturedRefBytes)),
                Cell(FormatHelper.TruncateString(largeCapturesText, 80)),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildSuspendedRows(IReadOnlyList<SuspendedMethodEntry> entries, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            SuspendedMethodEntry e = entries[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(e.DeclaringType, 60)),
                Cell(FormatHelper.TruncateString(e.MethodName, 50)),
                Cell($"{e.SuspendedCount:N0}", e.SuspendedCount),
                Cell(FormatHelper.FormatBytes(e.TotalBytes)),
            ]));
        }
        return rows;
    }
}

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

        if (d.TopStateMachineTypes.Count > 0)
        {
            blocks.Add(T("Each entry represents a distinct suspended async method. " +
                          "High counts for the same method indicate fire-and-forget patterns or unbounded parallelism."));
            compactTables.Add(STCompact(
                "Top async state machine types by instance count",
                new[] { CH("Type Name"), CH("Originating Method"), CH("Declaring Type"), CH("Count","number"), CH("Total Size","bytes"), CH("Dominant State"), CH("State Distribution"), CH("Ref Fields","number"), CH("Gen2 Count","number"), CH("Gen2 %","percent"), CH("Async Void") },
                BuildTypeRows(d.TopStateMachineTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            blocks.Add(T("State values indicate the suspend position in the async method: " +
                         "-2 = completed, -1 = not started, 0 = suspended at first await, 1 = suspended at second await, and so on. " +
                         "Dominant State and State Distribution are computed from every instance of each detected type."));
        }

        if (d.TopByCapturedSize.Count > 0)
        {
            blocks.Add(T("Async methods capture all variables referenced across await boundaries. " +
                          "Instances with large captured closures may indicate long-lived objects being retained unintentionally. " +
                          "Note: the captured reference bytes count is shallow (direct references only, not transitive closure) and counts objects even if referenced by multiple state machines; it is an estimate of closure size, not unique waste."));
            compactTables.Add(STCompact(
                "Top async state machine instances by captured reference bytes",
                new[] { CH("Address"), CH("Type Name"), CH("Captured Ref Bytes (shallow)","bytes"), CH("Large Captures") },
                BuildCaptureRows(d.TopByCapturedSize).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.SuspendedMethodMap.Count > 0)
        {
            blocks.Add(T("Methods with the most suspended instances. " +
                          "High counts for a single method typically indicate fire-and-forget usage or long-running awaits."));
            compactTables.Add(STCompact(
                "Suspended async methods by instance count",
                new[] { CH("Declaring Type"), CH("Method Name"), CH("Suspended Count","number"), CH("Total Size","bytes") },
                BuildSuspendedRows(d.SuspendedMethodMap).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<StateMachineTypeProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            StateMachineTypeProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell(FormatHelper.TruncateString(t.OriginatingMethod, 40)),
                Cell(FormatHelper.TruncateString(t.DeclaringType, 50)),
                Cell($"{t.Count:N0}",                       t.Count),
                Cell(FormatHelper.FormatBytes(t.TotalBytes)),
                Cell(t.DominantState.ToString()),
                Cell(FormatStateDistribution(t.StateDistribution)),
                Cell($"{t.ReferenceFieldCount:N0}",         t.ReferenceFieldCount),
                Cell($"{t.Gen2Count:N0}",                   t.Gen2Count),
                Cell($"{t.Gen2Fraction * 100:F1}%",         t.Gen2Fraction),
                Cell(t.IsAsyncVoid ? "Yes" : "No"),
            ]));
        }
        return rows;
    }

    private static string FormatStateDistribution(IReadOnlyList<(int State, int Count)> distribution)
    {
        if (distribution.Count == 0) return "—";
        return string.Join(", ", distribution.Select(d => $"{d.State}: {d.Count:N0}"));
    }

    private static List<TableRow> BuildCaptureRows(IReadOnlyList<HighCaptureStateMachine> captures)
    {
        var rows = new List<TableRow>(captures.Count);
        for (int i = 0; i < captures.Count; i++)
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

    private static List<TableRow> BuildSuspendedRows(IReadOnlyList<SuspendedMethodEntry> entries)
    {
        var rows = new List<TableRow>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
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

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AsyncStateMachineSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;
    private const int TopCaptureRows = 10;
    private const int TopSuspendedRows = 20;

    public string AnalyzerName => "Async State Machine Analysis";
    public int SortOrder => 48; // §23 async state machines

    public bool CanHandle(AnalyzerDomainResult result) => result is AsyncStateMachineDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AsyncStateMachineDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("ASYNC STATE MACHINE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total State Machines", $"{d.TotalStateMachines:N0}", d.TotalStateMachines));
        blocks.Add(M("Total Memory", FormatHelper.FormatBytes(d.TotalStateMachineBytes)));
        blocks.Add(M("Distinct Types", $"{d.TopStateMachineTypes.Count:N0}"));
        blocks.Add(M("Suspended Methods", $"{d.SuspendedMethodMap.Count:N0}"));
        if (d.ScanLimited)
            blocks.Add(M("Scan Limit Reached", "Yes — type candidate cap hit; results may be partial", 1.0));

        // ── Top state machine types ───────────────────────────────────────────
        if (d.TopStateMachineTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ASYNC STATE MACHINE TYPES"));
            blocks.Add(T("Each entry represents a distinct suspended async method. " +
                          "High counts for the same method indicate fire-and-forget patterns or unbounded parallelism."));
            int limit = Math.Min(d.TopStateMachineTypes.Count, TopTypeRows);
            blocks.Add(new TableBlock(
                Caption: "Top async state machine types by instance count",
                Headers: ["Type Name", "Originating Method", "Declaring Type", "Count", "Total Size", "Avg State", "Ref Fields"],
                Rows: BuildTypeRows(d.TopStateMachineTypes, limit)));
            if (d.TopStateMachineTypes.Count > limit)
                blocks.Add(T($"Showing top {limit} async state machine types. {d.TopStateMachineTypes.Count - limit} additional type(s) omitted."));
        }

        // ── High-capture instances ─────────────────────────────────────────────
        if (d.TopByCapturedSize.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP INSTANCES BY CAPTURED REFERENCE SIZE"));
            blocks.Add(T("Async methods capture all variables referenced across await boundaries. " +
                          "Instances with large captured closures may indicate long-lived objects being retained unintentionally."));
            int limit = Math.Min(d.TopByCapturedSize.Count, TopCaptureRows);
            blocks.Add(new TableBlock(
                Caption: "Top async state machine instances by captured reference bytes",
                Headers: ["Address", "Type Name", "Captured Ref Bytes", "Large Captures"],
                Rows: BuildCaptureRows(d.TopByCapturedSize, limit)));
            if (d.TopByCapturedSize.Count > limit)
                blocks.Add(T($"Showing top {limit} captured-state-machine instances. {d.TopByCapturedSize.Count - limit} additional instance(s) omitted."));
        }

        // ── Suspended method map ───────────────────────────────────────────────
        if (d.SuspendedMethodMap.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("SUSPENDED METHOD MAP"));
            blocks.Add(T("Methods with the most suspended instances. " +
                          "High counts for a single method typically indicate fire-and-forget usage or long-running awaits."));
            int limit = Math.Min(d.SuspendedMethodMap.Count, TopSuspendedRows);
            blocks.Add(new TableBlock(
                Caption: "Suspended async methods by instance count",
                Headers: ["Declaring Type", "Method Name", "Suspended Count", "Total Size"],
                Rows: BuildSuspendedRows(d.SuspendedMethodMap, limit)));
            if (d.SuspendedMethodMap.Count > limit)
                blocks.Add(T($"Showing top {limit} suspended methods. {d.SuspendedMethodMap.Count - limit} additional method(s) omitted."));
        }

        return new AnalyzerDetailSection(AnalyzerName, "Async State Machine Analysis", SortOrder, blocks);
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
                Cell(t.AvgStateValue.ToString()),
                Cell($"{t.ReferenceFieldCount:N0}",         t.ReferenceFieldCount),
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

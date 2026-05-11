using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ConfidenceSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.confidence";
    public string DisplayTitle => "Confidence & Limitations";
    public int SortOrder => 1750;

    public bool CanBuild(AnalyzerResultSet results) => results.AllRuns.Count > 0;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        var blocks = new List<SectionBlock>
        {
            H("RUN STATUS SUMMARY"),
            T("Status mapping: Completed, Failed, Skipped (filter), and Skipped (cancelled)."),
        };

        var rows = new List<TableRow>(results.AllRuns.Count);
        int completed = 0;
        int failed = 0;
        int skippedFilter = 0;
        int skippedCancelled = 0;

        foreach (var run in results.AllRuns)
        {
            string status = run.Status switch
            {
                AnalyzerExecutionStatus.Success => "Completed",
                AnalyzerExecutionStatus.Failed => "Failed",
                AnalyzerExecutionStatus.SkippedByFilter => "Skipped (filter)",
                AnalyzerExecutionStatus.SkippedByCancellation => "Skipped (cancelled)",
                _ => run.Status.ToString()
            };

            switch (run.Status)
            {
                case AnalyzerExecutionStatus.Success: completed++; break;
                case AnalyzerExecutionStatus.Failed: failed++; break;
                case AnalyzerExecutionStatus.SkippedByFilter: skippedFilter++; break;
                case AnalyzerExecutionStatus.SkippedByCancellation: skippedCancelled++; break;
            }

            rows.Add(Row(
                Cell(run.AnalyzerName),
                Cell(status),
                Cell(run.Duration.TotalMilliseconds.ToString("N0"), (long)Math.Round(run.Duration.TotalMilliseconds)),
                Cell(run.ObjectScanCount.ToString("N0"), run.ObjectScanCount),
                Cell(string.IsNullOrWhiteSpace(run.SkipReason) ? (string.IsNullOrWhiteSpace(run.ErrorMessage) ? "-" : run.ErrorMessage) : run.SkipReason)));
        }

        blocks.Add(new TableBlock(
            Caption: "Analyzer run status",
            Headers: ["Analyzer", "Status", "Duration (ms)", "Objects Scanned", "Error"],
            Rows: rows));

        blocks.Add(Blank());
        blocks.Add(H("AGGREGATE STATUS"));
        blocks.Add(M("Completed", completed.ToString("N0"), completed));
        blocks.Add(M("Failed", failed.ToString("N0"), failed));
        blocks.Add(M("Skipped by filter", skippedFilter.ToString("N0"), skippedFilter));
        blocks.Add(M("Skipped by cancellation", skippedCancelled.ToString("N0"), skippedCancelled));

        blocks.Add(Blank());
        blocks.Add(H("CONFIDENCE TIERS"));
        blocks.Add(T("Measured = 1.0, High-confidence heuristic = 0.8, Partial/bounded = 0.5, Speculative < 0.5."));

        var limitationRows = new List<TableRow>();
        AddLimitation(limitationRows, "Retention", results.Get<RetentionDomainResult>() is RetentionDomainResult retention && (retention.SkippedReferenceAddresses > 0 || retention.ObjectScanCapped || retention.ReferenceCountingSkipped), BuildRetentionText(results.Get<RetentionDomainResult>()));
        AddLimitation(limitationRows, "GC roots", results.Get<GCRootDomainResult>() is GCRootDomainResult gcRoot && (gcRoot.PathSearchCapped || gcRoot.PathSearchCappedCount > 0), BuildRootText(results.Get<GCRootDomainResult>()));
        AddLimitation(limitationRows, "Hang / task scan", results.Get<HangDomainResult>() is HangDomainResult hang && (!hang.RuntimeThreadPoolDataAvailable || hang.TaskScanLimited), BuildHangText(results.Get<HangDomainResult>()));
        AddLimitation(limitationRows, "Async tasks", results.Get<AsyncTaskDomainResult>() is AsyncTaskDomainResult asyncTasks && asyncTasks.TaskScanLimited, BuildAsyncTaskText(results.Get<AsyncTaskDomainResult>()));
        AddLimitation(limitationRows, "Async state machines", results.Get<AsyncStateMachineDomainResult>() is AsyncStateMachineDomainResult asyncState && asyncState.ScanLimited, BuildAsyncStateText(results.Get<AsyncStateMachineDomainResult>()));
        AddLimitation(limitationRows, "Arrays", results.Get<ArrayDomainResult>() is ArrayDomainResult array && array.ScanLimited, BuildArrayText(results.Get<ArrayDomainResult>()));

        if (limitationRows.Count > 0)
        {
            blocks.Add(H("LIMITATION SIGNALS"));
            blocks.Add(new TableBlock(
                Caption: "Current bounded-scan signals",
                Headers: ["Area", "Flagged", "What it means"],
                Rows: limitationRows));
        }

        blocks.Add(H("LIMITATIONS"));
        blocks.Add(Li("Bounded retained-size BFS still uses a breadth cap and depth cap."));
        blocks.Add(Li("Retention and root-path metrics remain selective, not exhaustive, on large dumps."));
        blocks.Add(Li("Event and finalizer retention numbers are heuristic summaries, not full graph proofs."));

        return new AnalyzerDetailSection(
            AnalyzerName: "Confidence & Limitations",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static void AddLimitation(List<TableRow> rows, string area, bool flagged, string text)
    {
        if (!flagged)
            return;

        rows.Add(Row(
            Cell(area),
            Cell("Yes"),
            Cell(text)));
    }

    private static string BuildRetentionText(RetentionDomainResult? retention)
    {
        if (retention is null)
            return "No retention analyzer result available.";

        if (retention.ReferenceCountingSkipped)
            return "Incoming-reference counting was skipped because the disk-backed index would make full reverse counting too expensive.";

        if (retention.ObjectScanCapped)
            return $"Incoming-reference counting hit the scan cap after {retention.SkippedReferenceAddresses:N0} skipped addresses; highly-referenced-object counts may be partial.";

        if (retention.SkippedReferenceAddresses > 0)
            return $"Reference counting skipped {retention.SkippedReferenceAddresses:N0} addresses; retention counts may be partial.";

        return "Retention metrics are bounded but available.";
    }

    private static string BuildRootText(GCRootDomainResult? gcRoot)
    {
        if (gcRoot is null)
            return "No GC root result available.";

        if (gcRoot.PathSearchCapped)
            return $"GC root path search was capped after {gcRoot.PathSearchCappedCount:N0} targets; deeper paths may be missing.";

        return "GC root paths are bounded and selective.";
    }

    private static string BuildHangText(HangDomainResult? hang)
    {
        if (hang is null)
            return "No hang result available.";

        if (!hang.RuntimeThreadPoolDataAvailable)
            return "Runtime thread-pool data was not available; thread-pool health may be approximate.";

        if (hang.TaskScanLimited)
            return "Task scanning was limited; queued work items and continuation totals may be partial.";

        return "Thread-pool and task metrics are bounded.";
    }

    private static string BuildAsyncTaskText(AsyncTaskDomainResult? asyncTasks)
    {
        if (asyncTasks is null)
            return "No async task result available.";

        return asyncTasks.TaskScanLimited
            ? "Async task scanning hit its cap; orphan and continuation totals may be partial."
            : "Async task scan completed within the configured cap.";
    }

    private static string BuildAsyncStateText(AsyncStateMachineDomainResult? asyncState)
    {
        if (asyncState is null)
            return "No async state-machine result available.";

        return asyncState.ScanLimited
            ? "Async state-machine type scan was capped; top-type and capture summaries may be partial."
            : "Async state-machine scan completed within the configured cap.";
    }

    private static string BuildArrayText(ArrayDomainResult? array)
    {
        if (array is null)
            return "No array result available.";

        return array.ScanLimited
            ? "Array scanning was capped; sparse-array and large-array lists may be partial."
            : "Array scan completed within the configured cap.";
    }
}
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ConfidenceSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => [];

    public string SectionId => "Z3";
    public string DisplayTitle => "Known Limitations";
    public int SortOrder => 1750;

    public bool CanBuild(AnalyzerResultSet results) => results.AllRuns.Count > 0;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
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

        compactTables.Add(STCompact("Analyzer run status",
            new[] { CH("Analyzer"), CH("Status"), CH("Duration (ms)", "number", "milliseconds"), CH("Objects Scanned","number"), CH("Error/Skip Reason") },
            rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["completed"] = new NumericMetricValue(completed, MetricUnit.Count),
            ["failed"] = new NumericMetricValue(failed, MetricUnit.Count),
            ["skipped_by_filter"] = new NumericMetricValue(skippedFilter, MetricUnit.Count),
            ["skipped_by_cancellation"] = new NumericMetricValue(skippedCancelled, MetricUnit.Count),
        };

        var memoryRows = new List<TableRow>();
        foreach (var run in results.AllRuns)
        {
            if (run.MemoryStats is null)
                continue;

            AnalyzerMemoryStats stats = run.MemoryStats;
            // "Allocated" leads and the old "MH Delta" column is gone: a managed-heap delta is a net
            // heap-SIZE change, so an allocation-heavy analyzer whose work triggers a gen2 collection
            // reports a negative number while costing gigabytes. Allocated bytes is monotonic and
            // attributes correctly. See docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 1.
            memoryRows.Add(Row(
                Cell(run.AnalyzerName),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.AllocatedDelta)), stats.AllocatedDelta),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.WorkingSetBefore)), stats.WorkingSetBefore),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.WorkingSetAfter)), stats.WorkingSetAfter),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.WorkingSetDelta)), stats.WorkingSetDelta),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.ManagedHeapBefore)), stats.ManagedHeapBefore),
                Cell(FormatHelper.FormatBytes((ulong)Math.Max(0, stats.ManagedHeapAfter)), stats.ManagedHeapAfter)));
        }

        if (memoryRows.Count > 0)
        {
            compactTables.Add(STCompact("Analyzer memory impact",
                new[] { CH("Analyzer"), CH("Allocated","bytes"), CH("WS Before","bytes"), CH("WS After","bytes"), CH("WS Delta","bytes"), CH("MH Before","bytes"), CH("MH After","bytes") },
                memoryRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        blocks.Add(T("Measured = 1.0, High-confidence heuristic = 0.8, Partial/bounded = 0.5, Speculative < 0.5."));

        var limitationRows = new List<TableRow>();
        AddLimitation(limitationRows, "Retention", results.Get<DominatorDomainResult>() is DominatorDomainResult retention && (retention.SkippedReferenceAddresses > 0 || retention.ObjectScanCapped || retention.ReferenceCountingSkipped), BuildRetentionText(results.Get<DominatorDomainResult>()));
        AddLimitation(limitationRows, "GC roots", results.Get<GCRootDomainResult>() is GCRootDomainResult gcRoot && (gcRoot.PathSearchCapped || gcRoot.PathSearchCappedCount > 0), BuildRootText(results.Get<GCRootDomainResult>()));
        AddLimitation(limitationRows, "Hang / task scan", results.Get<HangDomainResult>() is HangDomainResult hang && !hang.RuntimeThreadPoolDataAvailable, BuildHangText(results.Get<HangDomainResult>()));
        AddLimitation(limitationRows, "Async tasks", results.Get<AsyncTaskDomainResult>() is AsyncTaskDomainResult asyncTasks && asyncTasks.TaskScanLimited, BuildAsyncTaskText(results.Get<AsyncTaskDomainResult>()));

        if (limitationRows.Count > 0)
        {
            compactTables.Add(STCompact("Current bounded-scan signals",
                new[] { CH("Area"), CH("Flagged"), CH("Confidence", "number"), CH("What it means") },
                limitationRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        blocks.Add(Li("Bounded retained-size BFS still uses a breadth cap and depth cap."));
        blocks.Add(Li("Retention and root-path metrics remain selective, not exhaustive, on large dumps."));
        blocks.Add(Li("Event and finalizer retention numbers are heuristic summaries, not full graph proofs."));

        return new AnalyzerDetailSection(
            AnalyzerName: "Known Limitations",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static void AddLimitation(List<TableRow> rows, string area, bool flagged, string text)
    {
        if (!flagged)
            return;

        (double score, _) = ConfidenceScoring.Compute(0.8, ConfidenceScoring.F(flagged, 0.3, text));

        rows.Add(Row(
            Cell(area),
            Cell("Yes"),
            Cell(score.ToString("F2"), score),
            Cell(text)));
    }

    private static string BuildRetentionText(DominatorDomainResult? retention)
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

}
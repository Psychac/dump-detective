using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>D2 — Hang &amp; Blocking. Source: <see cref="HangDomainResult"/>.</summary>
internal sealed class HangSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Hang Analysis";
    public string DisplayTitle => "Hang & Blocking";
    public int SortOrder => 200;

    public bool CanHandle(AnalyzerDomainResult result) => result is HangDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (HangDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        SectionLeadFinding? leadFinding = null;
        if (d.IsStarved || d.HealthScore < 50)
        {
            leadFinding = new SectionLeadFinding(
                Severity: d.IsStarved ? "Warning" : "Warning",
                Title: d.IsStarved
                    ? "Thread pool starvation detected — queue length exceeds max worker threads"
                    : $"Thread pool health degraded (score {d.HealthScore:N0})",
                Summary: d.IsStarved
                    ? $"Queued work items: {d.QueuedWorkItems:N0}, active workers at max ({d.RuntimeMaxThreads:N0})."
                    : $"Health score {d.HealthScore:N0} is below the healthy threshold of 50.",
                Recommendation: "Increase thread pool min/max threads, reduce synchronous blocking on async paths, or profile CPU-bound work.",
                ConfidenceSymbol: "●●●●",
                ConfidenceScore: 0.85,
                Caveats: d.RuntimeThreadPoolDataAvailable
                    ? []
                    : ["Runtime thread-pool data was unavailable; some metrics are estimated."]);
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_alive_threads"] = new NumericMetricValue(d.TotalAliveThreads, MetricUnit.Count),
            ["waiting_threads"] = new NumericMetricValue(d.WaitingThreadCount, MetricUnit.Count),
            ["threads_holding_locks"] = new NumericMetricValue(d.ThreadsHoldingLocks, MetricUnit.Count),
            ["waiting_pct"] = new NumericMetricValue(d.WaitingPercent, MetricUnit.Percent, $"{d.WaitingPercent:F1}%"),
            ["health_score"] = new NumericMetricValue(d.HealthScore, MetricUnit.Count),
            ["is_starved"] = new TextMetricValue(d.IsStarved ? "Yes" : "No"),
            ["queued_work_items"] = new NumericMetricValue(d.QueuedWorkItems, MetricUnit.Count),
            ["runtime_tp_data"] = new TextMetricValue(d.RuntimeThreadPoolDataAvailable ? "Available" : "Unavailable"),
        };
        if (d.RuntimeThreadPoolDataAvailable)
        {
            keyMetrics["min_worker_threads"] = new NumericMetricValue(d.RuntimeMinThreads, MetricUnit.Count);
            keyMetrics["max_worker_threads"] = new NumericMetricValue(d.RuntimeMaxThreads, MetricUnit.Count);
            keyMetrics["active_worker_threads"] = new NumericMetricValue(d.RuntimeActiveWorkerThreads, MetricUnit.Count);
            keyMetrics["idle_worker_threads"] = new NumericMetricValue(d.RuntimeIdleWorkerThreads, MetricUnit.Count);
            keyMetrics["retired_worker_threads"] = new NumericMetricValue(d.RuntimeRetiredWorkerThreads, MetricUnit.Count);
            if (d.RuntimeQueueLength.HasValue)
                keyMetrics["runtime_queue_length"] = new NumericMetricValue(d.RuntimeQueueLength.Value, MetricUnit.Count);
            else
                keyMetrics["runtime_queue_length"] = new TextMetricValue("Unavailable");
            keyMetrics["cpu_utilization"] = new NumericMetricValue(d.RuntimeCpuUtilization, MetricUnit.Percent, $"{d.RuntimeCpuUtilization:N0}%");
        }

        if (d.WaitCategoryBreakdown.Count > 0)
        {
            var rows = new List<TableRow>(d.WaitCategoryBreakdown.Count);
            foreach (KeyValuePair<string, int> kvp in d.WaitCategoryBreakdown.OrderByDescending(kvp => kvp.Value))
                rows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("Wait category breakdown", new[] { CH("Category"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopWaitingThreads is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopWaitingThreads.Count);
            for (int i = 0; i < d.TopWaitingThreads.Count; i++)
            {
                WaitingThreadSnapshot t = d.TopWaitingThreads[i];
                rows.Add(Row(
                    Cell(t.ThreadId.ToString("N0"), t.ThreadId),
                    Cell(t.OSThreadId.ToString("N0"), t.OSThreadId),
                    Cell(t.WaitType ?? "—"),
                    Cell(t.WaitReason ?? "—"),
                    Cell(t.LockCount.ToString("N0"), t.LockCount),
                    Cell(t.TopStackFrame ?? "—")));
            }
            compactTables.Add(STCompact("Top waiting threads", new[] { CH("Thread","number"), CH("OS Thread","number"), CH("Wait Type"), CH("Wait Reason"), CH("Locks","number"), CH("Top Frame") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.RuntimeThreadPoolDataAvailable)
        {
            compactTables.Add(STCompact(
                "Runtime thread-pool metrics",
                new[] { CH("Signal"), CH("Value") },
                new[] {
                    R("Min worker threads", d.RuntimeMinThreads.ToString("N0")),
                    R("Max worker threads", d.RuntimeMaxThreads.ToString("N0")),
                    R("Active worker threads", d.RuntimeActiveWorkerThreads.ToString("N0")),
                    R("Idle worker threads", d.RuntimeIdleWorkerThreads.ToString("N0")),
                    R("Retired worker threads", d.RuntimeRetiredWorkerThreads.ToString("N0")),
                    R("Runtime queue length", d.RuntimeQueueLength.HasValue ? d.RuntimeQueueLength.Value.ToString("N0") : "Unavailable"),
                    R("CPU utilization", $"{d.RuntimeCpuUtilization:N0}%"),
                    R("Starvation flag", d.IsStarved ? "Yes" : "No"),
                }));

            blocks.Add(T(d.RuntimeQueueLength.HasValue
                ? "Runtime queue length is exposed directly by ClrMD; queued work items remain a dump-derived proxy."
                : "Queued work items are the fallback queue-length proxy — ClrMD did not expose a runtime queue length value."));
        }
        else
        {
            blocks.Add(T("Runtime thread-pool metadata was unavailable; this summary is approximate."));
        }

        if (d.TopContinuationTypes is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopContinuationTypes.Count);
            for (int i = 0; i < d.TopContinuationTypes.Count; i++)
                rows.Add(Row(Cell(d.TopContinuationTypes[i].Name), Cell(d.TopContinuationTypes[i].Count.ToString("N0"), d.TopContinuationTypes[i].Count)));
            compactTables.Add(STCompact("Top continuation types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Hang Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}

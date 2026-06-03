using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
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

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total alive threads",    d.TotalAliveThreads.ToString("N0"),            d.TotalAliveThreads),
            KM("Waiting threads",        d.WaitingThreadCount.ToString("N0"),            d.WaitingThreadCount),
            KM("Threads holding locks",  d.ThreadsHoldingLocks.ToString("N0"),           d.ThreadsHoldingLocks),
            KM("Waiting %",              $"{d.WaitingPercent:F1}%",                      d.WaitingPercent),
            KM("Health score",           d.HealthScore.ToString("N0"),                   d.HealthScore),
            KM("Is starved",             d.IsStarved ? "Yes" : "No",                     d.IsStarved ? 1.0 : 0.0),
            KM("Queued work items",      d.QueuedWorkItems.ToString("N0"),               d.QueuedWorkItems),
            KM("Runtime TP data",        d.RuntimeThreadPoolDataAvailable ? "Available" : "Unavailable",
                                         d.RuntimeThreadPoolDataAvailable ? 1.0 : 0.0),
        };
        if (d.RuntimeThreadPoolDataAvailable)
        {
            keyMetrics.Add(KM("Min worker threads",      d.RuntimeMinThreads.ToString("N0"),              d.RuntimeMinThreads));
            keyMetrics.Add(KM("Max worker threads",      d.RuntimeMaxThreads.ToString("N0"),              d.RuntimeMaxThreads));
            keyMetrics.Add(KM("Active worker threads",   d.RuntimeActiveWorkerThreads.ToString("N0"),    d.RuntimeActiveWorkerThreads));
            keyMetrics.Add(KM("Idle worker threads",     d.RuntimeIdleWorkerThreads.ToString("N0"),      d.RuntimeIdleWorkerThreads));
            keyMetrics.Add(KM("Retired worker threads",  d.RuntimeRetiredWorkerThreads.ToString("N0"),   d.RuntimeRetiredWorkerThreads));
            keyMetrics.Add(KM("Runtime queue length",    d.RuntimeQueueLength.HasValue ? d.RuntimeQueueLength.Value.ToString("N0") : "N/A", d.RuntimeQueueLength ?? 0));
            keyMetrics.Add(KM("CPU utilization",         $"{d.RuntimeCpuUtilization:N0}%",               d.RuntimeCpuUtilization));
        }

        if (d.WaitCategoryBreakdown.Count > 0)
        {
            var rows = new List<TableRow>(d.WaitCategoryBreakdown.Count);
            foreach (KeyValuePair<string, int> kvp in d.WaitCategoryBreakdown.OrderByDescending(kvp => kvp.Value))
                rows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            tables.Add(ST("Wait category breakdown", ["Category", "Count"], rows));
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
            tables.Add(ST("Top waiting threads", ["Thread", "OS Thread", "Wait Type", "Wait Reason", "Locks", "Top Frame"], rows));
        }

        if (d.RuntimeThreadPoolDataAvailable)
        {
            tables.Add(ST(
                "Runtime thread-pool metrics",
                ["Signal", "Value"],
                [
                    Row(Cell("Min worker threads"),    Cell(d.RuntimeMinThreads.ToString("N0"),            d.RuntimeMinThreads)),
                    Row(Cell("Max worker threads"),    Cell(d.RuntimeMaxThreads.ToString("N0"),            d.RuntimeMaxThreads)),
                    Row(Cell("Active worker threads"), Cell(d.RuntimeActiveWorkerThreads.ToString("N0"),  d.RuntimeActiveWorkerThreads)),
                    Row(Cell("Idle worker threads"),   Cell(d.RuntimeIdleWorkerThreads.ToString("N0"),    d.RuntimeIdleWorkerThreads)),
                    Row(Cell("Retired worker threads"),Cell(d.RuntimeRetiredWorkerThreads.ToString("N0"), d.RuntimeRetiredWorkerThreads)),
                    Row(Cell("Runtime queue length"),  Cell(d.RuntimeQueueLength.HasValue ? d.RuntimeQueueLength.Value.ToString("N0") : "Unavailable", d.RuntimeQueueLength)),
                    Row(Cell("CPU utilization"),       Cell($"{d.RuntimeCpuUtilization:N0}%",             d.RuntimeCpuUtilization)),
                    Row(Cell("Starvation flag"),       Cell(d.IsStarved ? "Yes" : "No",                   d.IsStarved ? 1L : 0L)),
                ]));

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
            tables.Add(ST("Top continuation types", ["Type", "Count"], rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Hang Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}

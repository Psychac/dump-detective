using System.Diagnostics;

using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Trend;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Renders trend analysis results (per-dump summaries, overview, memory, diagnostics) to the console.
/// </summary>
internal static class TrendConsolePresenter
{
    public static void PrintTrendDumpSummary(int dumpIndex, int totalDumps, TrendDumpExecution execution, TimeSpan cumulativeDumpElapsed, bool diagnosticMode)
    {
        int success = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
        int failed = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
        int skippedByFilter = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter);
        int skippedByCancellation = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation);
        long findings = execution.Runs.Sum(r => r.FindingCount);

        ConsoleUx.Success($"[{dumpIndex}/{totalDumps}] Completed {Path.GetFileName(execution.DumpPath)} in {execution.Elapsed.TotalSeconds:F1}s (cumulative dumps: {cumulativeDumpElapsed.TotalSeconds:F1}s) · success={success}, failed={failed}, skipped_filter={skippedByFilter}, skipped_cancelled={skippedByCancellation}, findings={findings}");

        foreach (AnalyzerRunResult run in execution.Runs.Where(r => r.Status == AnalyzerExecutionStatus.Failed))
        {
            string status = run.Status.ToString().ToLowerInvariant();
            ConsoleUx.Warning($"   - {run.AnalyzerName}: {status}, {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}");
        }

        if (!diagnosticMode)
            return;

        AnalyzerRunResult[] topSlow = execution.Runs
            .OrderByDescending(r => r.Duration)
            .Take(8)
            .ToArray();

        ConsoleUx.Info($"   Top {topSlow.Length} slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
        {
            string status = run.Status.ToString().ToLowerInvariant();
            ConsoleUx.Info($"   - {run.AnalyzerName}: {status}, {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
        }
    }

    public static void PrintTrendOverallSummary(TrendReportData trendData, bool diagnosticMode)
    {
        int totalRegressions = trendData.Overall.Sum(r => r.Regressions.Count);
        int totalImprovements = trendData.Overall.Sum(r => r.Improvements.Count);

        ConsoleUx.Info("Trend overview:");
        ConsoleUx.Info($"   Dumps={trendData.Snapshots.Count}, New={trendData.NewFindings.Count}, Persistent={trendData.PersistentFindings.Count}, Resolved={trendData.ResolvedFindings.Count}");
        ConsoleUx.Info($"   Metric changes: regressions={totalRegressions}, improvements={totalImprovements}");

        var orderedEnum = trendData.Overall
            .OrderByDescending(r => r.Regressions.Count)
            .ThenByDescending(r => r.Improvements.Count)
            .ThenBy(r => r.AnalyzerName, StringComparer.Ordinal);

        AnalyzerTrendResult[] visible = diagnosticMode
            ? orderedEnum.ToArray()
            : orderedEnum.Where(a => a.Regressions.Count > 0 || a.Improvements.Count > 0).Take(8).ToArray();

        if (visible.Length == 0)
        {
            ConsoleUx.Info("   No significant analyzer-level trend deltas to display.");
            return;
        }

        foreach (AnalyzerTrendResult analyzer in visible)
        {
            MetricDelta? topRegression = analyzer.Regressions
                .OrderByDescending(d => Math.Abs(d.DeltaPercent ?? d.Delta))
                .FirstOrDefault();

            string highlight = topRegression is null
                ? "top-regression=n/a"
                : $"top-regression={topRegression.Key} {(topRegression.DeltaPercent.HasValue ? $"{topRegression.DeltaPercent.Value:+0.0;-0.0;0.0}%" : $"{topRegression.Delta:+0.0;-0.0;0.0} {topRegression.Unit}")}";

            ConsoleUx.Info($"   - {analyzer.AnalyzerName}: regressions={analyzer.Regressions.Count}, improvements={analyzer.Improvements.Count}, {highlight}");
        }
    }

    public static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
            return;

        long totalScans = runs.Sum(r => r.ObjectScanCount);
        long totalCacheHits = runs.Sum(r => r.CacheHits);
        long totalCacheMisses = runs.Sum(r => r.CacheMisses);
        long cacheTotal = totalCacheHits + totalCacheMisses;
        double cacheHitRatio = cacheTotal == 0 ? 0 : totalCacheHits * 100.0 / cacheTotal;

        ConsoleUx.Info($"Scan summary: object-scans={totalScans:N0}, cache-hits={totalCacheHits:N0}, cache-misses={totalCacheMisses:N0}, hit-ratio={cacheHitRatio:F1}%");

        AnalyzerRunResult[] topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToArray();

        ConsoleUx.Info("Top slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
            ConsoleUx.Info($"  - {run.AnalyzerName}: {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
    }

    public static void PrintMemorySummary(
        IReadOnlyList<TrendDumpExecution> executions,
        IReadOnlyList<(string StageName, AnalyzerMemoryStats Stats)> trendStageMemoryStats)
    {
        var allStageRows = new List<(string StageName, AnalyzerMemoryStats Stats)>();
        foreach (TrendDumpExecution execution in executions)
        {
            foreach ((string stageName, AnalyzerMemoryStats stats) in execution.StageMemoryStats)
                allStageRows.Add((stageName, stats));
        }

        foreach ((string stageName, AnalyzerMemoryStats stats) in trendStageMemoryStats)
            allStageRows.Add((stageName, stats));

        foreach (TrendDumpExecution execution in executions)
        {
            var dumpStageRows = execution.StageMemoryStats;
            if (dumpStageRows.Count == 0)
                continue;

            ConsoleUx.Info($"Memory diagnostics for dump: {Path.GetFileName(execution.DumpPath)}");
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string stageName, AnalyzerMemoryStats stats) in dumpStageRows)
                ConsoleUx.MemoryTableRow(stageName, stats.WorkingSetDelta, stats.WorkingSetAfter, stats.ManagedHeapDelta);
        }

        if (trendStageMemoryStats.Count > 0)
        {
            ConsoleUx.Info("Memory diagnostics for trend pipeline:");
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string stageName, AnalyzerMemoryStats stats) in trendStageMemoryStats)
                ConsoleUx.MemoryTableRow(stageName, stats.WorkingSetDelta, stats.WorkingSetAfter, stats.ManagedHeapDelta);
        }

        foreach (TrendDumpExecution execution in executions)
        {
            var dumpRuns = execution.Runs.Where(r => r.MemoryStats is not null);

            if (!dumpRuns.Any())
                continue;

            ConsoleUx.Info($"Memory diagnostics for dump: {Path.GetFileName(execution.DumpPath)}");
            ConsoleUx.MemoryTableHeader();
            foreach (AnalyzerRunResult run in dumpRuns)
            {
                AnalyzerMemoryStats s = run.MemoryStats!;
                ConsoleUx.MemoryTableRow(run.AnalyzerName, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
            }
        }

        long baseline = allStageRows.Count > 0
            ? allStageRows[0].Stats.WorkingSetBefore
            : executions.SelectMany(e => e.Runs).FirstOrDefault(r => r.MemoryStats is not null)?.MemoryStats?.WorkingSetBefore ?? 0;

        // Process.PeakWorkingSet64 is the OS-tracked historical maximum for this process's whole
        // lifetime, continuously updated by the kernel — see SingleDumpConsolePresenter.PrintMemorySummary
        // for why the previous "max of WorkingSetAfter across stage/analyzer checkpoints" approach
        // silently missed spikes that rose and fell within a single analyzer's run.
        Process currentProcess = Process.GetCurrentProcess();
        currentProcess.Refresh();
        long peak = currentProcess.PeakWorkingSet64;

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }
}

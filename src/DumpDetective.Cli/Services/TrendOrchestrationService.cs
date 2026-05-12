using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Trend;

using System.Diagnostics;

namespace DumpDetective.Cli.Services;

/// <summary>
/// Orchestrates a multi-dump trend analysis run: per-dump pipelines → trend report → output.
/// </summary>
internal sealed class TrendOrchestrationService(
    IDumpLoader dumpLoader,
    ReportBuilderFacade reportBuilderFacade,
    TrendAnalyzer trendAnalyzer,
    AnalyzerExecutionService analyzerExecutionService)
{
    private readonly IDumpLoader _dumpLoader = dumpLoader;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly TrendAnalyzer _trendAnalyzer = trendAnalyzer;
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;

    private const string TemporaryAdaptiveIndexingNotice =
        "TEMP-ADAPTIVE-INDEXING: Auto mode uses a provisional dump-size threshold; tune memory-vs-disk selection with large-dump profiling.";

    public async Task<int> ExecuteAsync(
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        IReadOnlyList<string> trendDumpPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        const int totalStages = 3;
        Stopwatch stageStopwatch = Stopwatch.StartNew();
        TimeSpan cumulativeDumpElapsed = TimeSpan.Zero;
        TimeSpan analyzeDumpsElapsed = TimeSpan.Zero;
        TimeSpan buildReportElapsed = TimeSpan.Zero;
        TimeSpan writeOutputElapsed = TimeSpan.Zero;

        ConsoleUx.Header("DumpDetective Trend Analysis");
        //ConsoleUx.Warning(TemporaryAdaptiveIndexingNotice);
        ConsoleUx.Info($"Trend dumps ({trendDumpPaths.Count}): {string.Join(" -> ", trendDumpPaths.Select(Path.GetFileName))}");
        ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers per dump...");
        if (resolved.DiagnosticMode)
            ConsoleUx.Info(AnalysisSummaryFormatter.FormatConfigSummary(resolved, activeAnalyzers));

        // ── Stage 1: Analyze each trend dump ─────────────────────────────────
        stageStopwatch.Restart();
        ConsoleUx.StageStart(1, totalStages, $"Analyze trend dumps ({trendDumpPaths.Count})");

        List<TrendDumpExecution> trendExecutions = new(trendDumpPaths.Count);
        for (int i = 0; i < trendDumpPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dumpPath = trendDumpPaths[i];
            string dumpName = Path.GetFileName(dumpPath);
            ConsoleUx.DumpStart(i + 1, trendDumpPaths.Count, dumpName);

            TrendDumpExecution execution = await ExecutePipelineForDumpAsync(dumpPath, resolved, allAnalyzers, activeAnalyzers, cancellationToken);
            trendExecutions.Add(execution);
            cumulativeDumpElapsed += execution.Elapsed;
            ConsoleUx.DumpComplete(i + 1, trendDumpPaths.Count, dumpName, execution.Elapsed);
            PrintTrendDumpSummary(i + 1, trendDumpPaths.Count, execution, cumulativeDumpElapsed, resolved.DiagnosticMode);

            if (execution.Runs.Any(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation))
                throw new OperationCanceledException("Analysis canceled.");
        }

        stageStopwatch.Stop();
        analyzeDumpsElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(1, totalStages, "Analyze trend dumps", stageStopwatch.Elapsed);

        // ── Stage 2: Build trend report ───────────────────────────────────────
        stageStopwatch.Restart();
        ConsoleUx.StageStart(2, totalStages, $"Build {resolved.Report.Format} trend report");

        IReadOnlyList<AnalysisSnapshot> snapshots = trendExecutions
            .Select((execution, index) => BuildSnapshot(index, execution))
            .ToList();

        AnalysisSnapshot baseline = snapshots[0];
        AnalysisSnapshot current = snapshots[^1];
        FindingLifecycleResult lifecycle = FindingLifecycleComparer.Compare(baseline, current);

        TrendReportData trendData = new(
            Steps: _trendAnalyzer.CompareSeries(snapshots),
            Overall: _trendAnalyzer.CompareAll(baseline, current),
            Timeline: _trendAnalyzer.ExtractTimeline(snapshots),
            Snapshots: snapshots,
            NewFindings: lifecycle.NewFindings,
            PersistentFindings: lifecycle.PersistentFindings,
            ResolvedFindings: lifecycle.ResolvedFindings);

        if (resolved.DiagnosticMode)
            PrintTrendOverallSummary(trendData, resolved.DiagnosticMode);

        IReadOnlyList<AnalyzerRunResult> currentRuns = trendExecutions[^1].Runs;
        string renderedReport = _reportBuilderFacade.BuildRenderedTrendReport(
            trendDumpPaths[^1],
            resolved.Report.Format,
            resolved.Report.Audience,
            currentRuns,
            totalStopwatch.Elapsed,
            trendExecutions[^1].IncidentContext,
            trendData,
            cancellationToken);

        stageStopwatch.Stop();
        buildReportElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(2, totalStages, "Build trend report", stageStopwatch.Elapsed);

        // ── Stage 3: Write output ─────────────────────────────────────────────
        stageStopwatch.Restart();
        ConsoleUx.StageStart(3, totalStages, "Write output");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(resolved.OutputPath))
            {
                await File.WriteAllTextAsync(resolved.OutputPath, renderedReport, cancellationToken);
                ConsoleUx.Success($"Report written to: {resolved.OutputPath}");
            }

            IReadOnlyList<AnalyzerRunResult> allRuns = trendExecutions.SelectMany(e => e.Runs).ToList();

            if (resolved.DiagnosticMode)
            {
                ConsoleUx.Info($"Trend pipeline completed in {totalStopwatch.Elapsed.TotalSeconds:F1}s");
                ConsoleUx.RunStatusSummary(
                    allRuns.Count(r => r.Status == AnalyzerExecutionStatus.Success),
                    allRuns.Count(r => r.Status == AnalyzerExecutionStatus.Failed),
                    allRuns.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter),
                    allRuns.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation),
                    allRuns.Sum(r => r.FindingCount));
                PrintDiagnosticsSummary(allRuns);
            }

            if (resolved.Diagnostics.EnableMemoryDiagnostics)
                PrintMemorySummary(trendExecutions);

            stageStopwatch.Stop();
            writeOutputElapsed = stageStopwatch.Elapsed;
            ConsoleUx.StageComplete(3, totalStages, "Write output", stageStopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutputWriteException("Failed while writing analysis output.", ex);
        }

        totalStopwatch.Stop();
        TimeSpan accounted = analyzeDumpsElapsed + buildReportElapsed + writeOutputElapsed;
        TimeSpan overhead = totalStopwatch.Elapsed - accounted;
        if (overhead < TimeSpan.Zero)
            overhead = TimeSpan.Zero;

        ConsoleUx.Info($"Trend time breakdown: dumps={analyzeDumpsElapsed.TotalSeconds:F1}s, report={buildReportElapsed.TotalMilliseconds:F0}ms, output={writeOutputElapsed.TotalMilliseconds:F0}ms, overhead={overhead.TotalMilliseconds:F0}ms");
        ConsoleUx.Success($"Total analysis time: {totalStopwatch.Elapsed.TotalSeconds:F1}s");

        return trendExecutions.Any(e => e.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed))
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private async Task<TrendDumpExecution> ExecutePipelineForDumpAsync(
        string dumpPath,
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnalyzerMemoryStats? memoryStats = null;
        if (resolved.Diagnostics.EnableMemoryDiagnostics)
        {
            using Process currentProcess = Process.GetCurrentProcess();
            currentProcess.Refresh();
            memoryStats = new AnalyzerMemoryStats(
                WorkingSetBefore: currentProcess.WorkingSet64,
                WorkingSetAfter: currentProcess.WorkingSet64,
                ManagedHeapBefore: GC.GetTotalMemory(false),
                ManagedHeapAfter: GC.GetTotalMemory(false));
        }

        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(dumpPath, cancellationToken);
        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;
        var heapIndex = heapBuilder.PrebuildHeapIndex(
            loadContext.Heap,
            dumpPath,
            cancellationToken,
            progress: new Progress<AnalyzerProgressReport>(r =>
                ConsoleUx.ObjectScanProgress($"[{Path.GetFileName(dumpPath)}] Scan + Index heap", r.ScannedCount, r.Elapsed ?? TimeSpan.Zero, "streaming objects to index")),
            mode: resolved.IndexPrebuildMode);
        string indexTarget = heapIndex.StorageKind == Analysis.Indexing.HeapIndexStorageKind.Memory
            ? "in-memory"
            : Path.GetFileName(heapIndex.IndexPath);
        ConsoleUx.ObjectScanComplete($"[{Path.GetFileName(dumpPath)}] Scan + Index heap", heapIndex.ObjectCount, heapIndex.Elapsed, $"{heapIndex.StorageKind} • {indexTarget}");

        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(resolved, loadContext, heapCache, activeAnalyzers);
        IReadOnlyList<AnalyzerRunResult> runs = await _analyzerExecutionService.ExecuteAsync(context, activeAnalyzers, cancellationToken);

        runs = AnalyzerFilterService.BuildSkippedByFilterResults(allAnalyzers, activeAnalyzers)
            .Concat(runs)
            .ToList();

        stopwatch.Stop();
        if (resolved.Diagnostics.EnableMemoryDiagnostics)
        {
            using Process currentProcess = Process.GetCurrentProcess();
            currentProcess.Refresh();
            memoryStats = new AnalyzerMemoryStats(
                WorkingSetBefore: memoryStats?.WorkingSetBefore ?? currentProcess.WorkingSet64,
                WorkingSetAfter: currentProcess.WorkingSet64,
                ManagedHeapBefore: memoryStats?.ManagedHeapBefore ?? GC.GetTotalMemory(false),
                ManagedHeapAfter: GC.GetTotalMemory(false));
        }

        AnalysisIncidentContext incidentContext = IncidentContextFactory.Create(
            mode: "Trend",
            loadContext: loadContext,
            resolved: resolved,
            activeAnalyzers: activeAnalyzers,
            elapsed: stopwatch.Elapsed);

        return new TrendDumpExecution(dumpPath, runs, stopwatch.Elapsed, incidentContext, DateTime.UtcNow, memoryStats);
    }

    private static AnalysisSnapshot BuildSnapshot(int index, TrendDumpExecution execution)
    {
        Dictionary<string, AnalyzerDomainResult> domains = new(StringComparer.Ordinal);
        List<InsightFinding> findings = [];

        foreach (AnalyzerRunResult run in execution.Runs)
        {
            if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
                continue;

            domains[run.AnalyzerName] = run.Result;
            findings.AddRange(run.Findings);
        }

        return new AnalysisSnapshot(
            Index: index,
            DumpPath: execution.DumpPath,
            Findings: findings,
            DomainResults: domains,
            GeneratedAtUtc: execution.GeneratedAtUtc,
            IncidentContext: execution.IncidentContext);
    }

    private sealed record TrendDumpExecution(
        string DumpPath,
        IReadOnlyList<AnalyzerRunResult> Runs,
        TimeSpan Elapsed,
        AnalysisIncidentContext IncidentContext,
        DateTime GeneratedAtUtc,
        AnalyzerMemoryStats? MemoryStats);

    private static void PrintTrendDumpSummary(int dumpIndex, int totalDumps, TrendDumpExecution execution, TimeSpan cumulativeDumpElapsed, bool diagnosticMode)
    {
        int success = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
        int failed = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
        int skippedByFilter = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter);
        int skippedByCancellation = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation);
        long findings = execution.Runs.Sum(r => r.FindingCount);

        ConsoleUx.Success($"[{dumpIndex}/{totalDumps}] Completed {Path.GetFileName(execution.DumpPath)} in {execution.Elapsed.TotalSeconds:F1}s (cumulative dumps: {cumulativeDumpElapsed.TotalSeconds:F1}s) · success={success}, failed={failed}, skipped_filter={skippedByFilter}, skipped_cancelled={skippedByCancellation}, findings={findings}");

        IReadOnlyList<AnalyzerRunResult> failedRuns = execution.Runs
            .Where(r => r.Status == AnalyzerExecutionStatus.Failed)
            .ToList();

        foreach (AnalyzerRunResult run in failedRuns)
        {
            string status = run.Status.ToString().ToLowerInvariant();
            ConsoleUx.Warning($"   - {run.AnalyzerName}: {status}, {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}");
        }

        if (!diagnosticMode)
            return;

        IReadOnlyList<AnalyzerRunResult> topSlow = execution.Runs
            .OrderByDescending(r => r.Duration)
            .Take(8)
            .ToList();

        ConsoleUx.Info($"   Top {topSlow.Count} slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
        {
            string status = run.Status.ToString().ToLowerInvariant();
            ConsoleUx.Info($"   - {run.AnalyzerName}: {status}, {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
        }
    }

    private static void PrintTrendOverallSummary(TrendReportData trendData, bool diagnosticMode)
    {
        int totalRegressions = trendData.Overall.Sum(r => r.Regressions.Count);
        int totalImprovements = trendData.Overall.Sum(r => r.Improvements.Count);

        ConsoleUx.Info("Trend overview:");
        ConsoleUx.Info($"   Dumps={trendData.Snapshots.Count}, New={trendData.NewFindings.Count}, Persistent={trendData.PersistentFindings.Count}, Resolved={trendData.ResolvedFindings.Count}");
        ConsoleUx.Info($"   Metric changes: regressions={totalRegressions}, improvements={totalImprovements}");

        IReadOnlyList<AnalyzerTrendResult> ordered = trendData.Overall
            .OrderByDescending(r => r.Regressions.Count)
            .ThenByDescending(r => r.Improvements.Count)
            .ThenBy(r => r.AnalyzerName, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<AnalyzerTrendResult> visible = diagnosticMode
            ? ordered
            : ordered.Where(a => a.Regressions.Count > 0 || a.Improvements.Count > 0).Take(8).ToList();

        if (visible.Count == 0)
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

    private static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
            return;

        long totalScans = runs.Sum(r => r.ObjectScanCount);
        long totalCacheHits = runs.Sum(r => r.CacheHits);
        long totalCacheMisses = runs.Sum(r => r.CacheMisses);
        long cacheTotal = totalCacheHits + totalCacheMisses;
        double cacheHitRatio = cacheTotal == 0 ? 0 : totalCacheHits * 100.0 / cacheTotal;

        ConsoleUx.Info($"Scan summary: object-scans={totalScans:N0}, cache-hits={totalCacheHits:N0}, cache-misses={totalCacheMisses:N0}, hit-ratio={cacheHitRatio:F1}%");

        IReadOnlyList<AnalyzerRunResult> topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToList();

        ConsoleUx.Info("Top slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
            ConsoleUx.Info($"  - {run.AnalyzerName}: {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
    }

    private static void PrintMemorySummary(IReadOnlyList<TrendDumpExecution> executions)
    {
        List<TrendDumpExecution> withStageStats = executions
            .Where(e => e.MemoryStats is not null)
            .ToList();

        if (withStageStats.Count > 0)
        {
            ConsoleUx.MemoryStageTableHeader();
            foreach (TrendDumpExecution execution in withStageStats)
            {
                AnalyzerMemoryStats s = execution.MemoryStats!;
                ConsoleUx.MemoryTableRow(Path.GetFileName(execution.DumpPath), s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
            }
        }

        foreach (TrendDumpExecution execution in executions)
        {
            List<AnalyzerRunResult> dumpRuns = execution.Runs
                .Where(r => r.MemoryStats is not null)
                .ToList();

            if (dumpRuns.Count == 0)
                continue;

            ConsoleUx.Info($"Memory diagnostics for dump: {Path.GetFileName(execution.DumpPath)}");
            ConsoleUx.MemoryTableHeader();
            foreach (AnalyzerRunResult run in dumpRuns)
            {
                AnalyzerMemoryStats s = run.MemoryStats!;
                ConsoleUx.MemoryTableRow(run.AnalyzerName, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
            }
        }

        long baseline = withStageStats.Count > 0
            ? withStageStats[0].MemoryStats!.WorkingSetBefore
            : executions.SelectMany(e => e.Runs).FirstOrDefault(r => r.MemoryStats is not null)?.MemoryStats?.WorkingSetBefore ?? 0;
        long peakFromStages = withStageStats.Count > 0 ? withStageStats.Max(e => e.MemoryStats!.WorkingSetAfter) : 0;
        IReadOnlyList<AnalyzerRunResult> analyzerRunsWithStats = executions
            .SelectMany(e => e.Runs)
            .Where(r => r.MemoryStats is not null)
            .ToList();
        long peakFromAnalyzers = analyzerRunsWithStats.Count > 0
            ? analyzerRunsWithStats.Max(r => r.MemoryStats!.WorkingSetAfter)
            : 0;
        long peak = Math.Max(peakFromStages, peakFromAnalyzers);

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }
}

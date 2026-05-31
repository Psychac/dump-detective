using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Console;
    using DumpDetective.Cli.Execution;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Trend;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Cli.Output;

using System.Diagnostics;

namespace DumpDetective.Cli.Services;

/// <summary>
/// Orchestrates a multi-dump trend analysis run: per-dump pipelines → trend report → output.
/// </summary>
internal sealed class TrendOrchestrationService(
    ReportBuilderFacade reportBuilderFacade,
    TrendAnalyzer trendAnalyzer,
    ReportOutputWriter outputWriter,
        PerDumpExecutionService perDumpExecutionService)
{
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly TrendAnalyzer _trendAnalyzer = trendAnalyzer;
    private readonly ReportOutputWriter _outputWriter = outputWriter;
    private readonly PerDumpExecutionService _perDumpExecutionService = perDumpExecutionService;

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
        var trendStageMemoryStats = new List<(string StageName, AnalyzerMemoryStats Stats)>();
        AnalyzerMemoryStats? writeOutputMemoryStats = null;

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

        Process currentProcess = Process.GetCurrentProcess();
        AnalyzerMemoryStats? buildReportMemoryBefore = null;
        if (resolved.Diagnostics.EnableMemoryDiagnostics)
        {
            currentProcess.Refresh();
            buildReportMemoryBefore = new AnalyzerMemoryStats(
                WorkingSetBefore: currentProcess.WorkingSet64,
                WorkingSetAfter: currentProcess.WorkingSet64,
                ManagedHeapBefore: GC.GetTotalMemory(false),
                ManagedHeapAfter: GC.GetTotalMemory(false));
        }

        AnalysisSnapshot[] snapshots = trendExecutions
            .Select((execution, index) => BuildSnapshot(index, execution))
            .ToArray();

        AnalysisSnapshot baseline = snapshots[0];
        AnalysisSnapshot current = snapshots[^1];
        FindingLifecycleResult lifecycle = FindingLifecycleComparer.Compare(baseline, current);

        TrendReportData trendData = new(
            Steps: _trendAnalyzer.CompareSeries(snapshots),
            Overall: _trendAnalyzer.CompareAll(baseline, current),
            NewLeakSignalsByAnalyzer: _trendAnalyzer.ComputeNewLeakSignals(baseline, current),
            Timeline: _trendAnalyzer.ExtractTimeline(snapshots),
            ScopedTimeline: _trendAnalyzer.ExtractScopedTimeline(snapshots),
            Snapshots: snapshots,
            NewFindings: lifecycle.NewFindings,
            PersistentFindings: lifecycle.PersistentFindings,
            ResolvedFindings: lifecycle.ResolvedFindings);

        if (resolved.DiagnosticMode)
            PrintTrendOverallSummary(trendData, resolved.DiagnosticMode);

        IReadOnlyList<AnalyzerRunResult> currentRuns = trendExecutions[^1].Runs;
        AnalysisReportDocument trendDoc = _reportBuilderFacade.BuildTrendReportDocument(
            trendDumpPaths[^1],
            resolved.Report.Audience,
            currentRuns,
            totalStopwatch.Elapsed,
            trendExecutions[^1].IncidentContext,
            trendData);
        string renderedReport = _reportBuilderFacade.RenderDocument(
            trendDoc,
            resolved.Report.Format,
            new HtmlRenderSettings(resolved.Report.PreRender, resolved.Report.StyleVersion));

        if (resolved.Diagnostics.EnableMemoryDiagnostics && buildReportMemoryBefore is not null)
        {
            currentProcess.Refresh();
            trendStageMemoryStats.Add(("Build trend report", new AnalyzerMemoryStats(
                WorkingSetBefore: buildReportMemoryBefore.WorkingSetBefore,
                WorkingSetAfter: currentProcess.WorkingSet64,
                ManagedHeapBefore: buildReportMemoryBefore.ManagedHeapBefore,
                ManagedHeapAfter: GC.GetTotalMemory(false))));
        }

        stageStopwatch.Stop();
        buildReportElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(2, totalStages, "Build trend report", stageStopwatch.Elapsed);

        // ── Stage 3: Write output ─────────────────────────────────────────────
        stageStopwatch.Restart();
        ConsoleUx.StageStart(3, totalStages, "Write output");
        if (resolved.Diagnostics.EnableMemoryDiagnostics)
        {
            currentProcess.Refresh();
            writeOutputMemoryStats = new AnalyzerMemoryStats(
                WorkingSetBefore: currentProcess.WorkingSet64,
                WorkingSetAfter: currentProcess.WorkingSet64,
                ManagedHeapBefore: GC.GetTotalMemory(false),
                ManagedHeapAfter: GC.GetTotalMemory(false));
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            AnalyzerRunResult[] allRuns = trendExecutions.SelectMany(e => e.Runs).ToArray();
            ReportArtifact[] artifacts = allRuns.SelectMany(r => r.Artifacts ?? Array.Empty<ReportArtifact>()).ToArray();
            await _outputWriter.WriteAsync(resolved, trendDoc, renderedReport, artifacts, cancellationToken);

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

            if (resolved.Diagnostics.EnableMemoryDiagnostics && writeOutputMemoryStats is not null)
            {
                currentProcess.Refresh();
                trendStageMemoryStats.Add(("Write output", new AnalyzerMemoryStats(
                    WorkingSetBefore: writeOutputMemoryStats.WorkingSetBefore,
                    WorkingSetAfter: currentProcess.WorkingSet64,
                    ManagedHeapBefore: writeOutputMemoryStats.ManagedHeapBefore,
                    ManagedHeapAfter: GC.GetTotalMemory(false))));
            }

            if (resolved.Diagnostics.EnableMemoryDiagnostics)
                PrintMemorySummary(trendExecutions, trendStageMemoryStats, allRuns);

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
        AnalyzerMemoryStats? memoryStats = null;
        string fileLabel = $"[{Path.GetFileName(dumpPath)}]";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastScanned = 0;
        string lastPhase = "loading dump";
        string? lastDetail = null;
        bool loadDone = false;
        bool indexDone = false;
        TimeSpan loadElapsed = TimeSpan.Zero;
        long indexScanned = 0;
        TimeSpan indexElapsed = TimeSpan.Zero;
        string? indexDetail = null;
        var progressLock = new object();

        var progress = new Progress<AnalyzerProgressReport>(r =>
        {
            Interlocked.Exchange(ref lastScanned, r.ScannedCount);
            lock (progressLock)
            {
                lastPhase = r.Phase;
                lastDetail = string.IsNullOrWhiteSpace(r.Detail) ? r.Phase : r.Detail;
                if (!loadDone && r.Phase == "preparing index")
                {
                    loadDone = true;
                    loadElapsed = r.Elapsed ?? sw.Elapsed;
                }
                if (!indexDone && r.Phase == "running analyzers")
                {
                    indexDone = true;
                    indexScanned = r.ScannedCount;
                    indexElapsed = r.Elapsed ?? sw.Elapsed;
                    indexDetail = r.Detail;
                }
            }
        });

        Task<PerDumpExecutionResult> execTask = Task.Run(
            () => _perDumpExecutionService.ExecuteAsync(
                "Trend", resolved, allAnalyzers, activeAnalyzers, dumpPath, progress, cancellationToken),
            cancellationToken);

        bool renderedLoadComplete = false;
        bool renderedIndexComplete = false;
        const int HeartbeatMs = 300;
        while (true)
        {
            Task done = await Task.WhenAny(execTask, Task.Delay(HeartbeatMs, cancellationToken));
            if (done == execTask) break;

            bool isLoadDone, isIndexDone;
            TimeSpan loadEl, idxEl;
            long idxScanned;
            string? idxDtl, details;
            lock (progressLock)
            {
                isLoadDone = loadDone;
                isIndexDone = indexDone;
                loadEl = loadElapsed;
                idxEl = indexElapsed;
                idxScanned = indexScanned;
                idxDtl = indexDetail;
                details = string.IsNullOrWhiteSpace(lastDetail) ? lastPhase : lastDetail;
            }
            long scanned = Interlocked.Read(ref lastScanned);

            if (isLoadDone && !renderedLoadComplete)
            {
                ConsoleUx.ObjectScanComplete($"{fileLabel} Load dump", 0, loadEl, null);
                renderedLoadComplete = true;
            }

            if (isIndexDone && !renderedIndexComplete)
            {
                ConsoleUx.ObjectScanComplete($"{fileLabel} Scan + Index heap", idxScanned, idxEl, idxDtl);
                renderedIndexComplete = true;
                break;
            }

            ConsoleUx.ObjectScanProgress($"{fileLabel} Scan + Index heap", scanned, sw.Elapsed, details);
        }

        PerDumpExecutionResult execution = await execTask;

        if (!renderedLoadComplete)
            ConsoleUx.ObjectScanComplete($"{fileLabel} Load dump", 0, sw.Elapsed, null);
        if (!renderedIndexComplete)
        {
            string indexTarget = execution.HeapIndex.StorageKind == Analysis.Indexing.HeapIndexStorageKind.Memory
                ? "in-memory"
                : Path.GetFileName(execution.HeapIndex.IndexPath);
            ConsoleUx.ObjectScanComplete($"{fileLabel} Scan + Index heap", execution.HeapIndex.ObjectCount, execution.HeapIndex.Elapsed, $"{execution.HeapIndex.StorageKind} • {indexTarget}");
        }

        return new TrendDumpExecution(dumpPath, execution.Runs, execution.Elapsed, execution.IncidentContext, DateTime.UtcNow, execution.StageMemoryStats, memoryStats);
    }

    private static AnalysisSnapshot BuildSnapshot(int index, TrendDumpExecution execution)
    {
        Dictionary<string, AnalyzerDomainResult> domains = new(StringComparer.Ordinal);
        var findings = new List<InsightFinding>();

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
            Runs: execution.Runs,
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
        IReadOnlyList<(string StageName, AnalyzerMemoryStats Stats)> StageMemoryStats,
        AnalyzerMemoryStats? MemoryStats);

    private static void PrintTrendDumpSummary(int dumpIndex, int totalDumps, TrendDumpExecution execution, TimeSpan cumulativeDumpElapsed, bool diagnosticMode)
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

    private static void PrintTrendOverallSummary(TrendReportData trendData, bool diagnosticMode)
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

        AnalyzerRunResult[] topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToArray();

        ConsoleUx.Info("Top slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
            ConsoleUx.Info($"  - {run.AnalyzerName}: {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
    }

    private static void PrintMemorySummary(
        IReadOnlyList<TrendDumpExecution> executions,
        IReadOnlyList<(string StageName, AnalyzerMemoryStats Stats)> trendStageMemoryStats,
        IReadOnlyList<AnalyzerRunResult>? allRuns = null)
    {
        var allStageRows = new List<(string StageName, AnalyzerMemoryStats Stats)>();
        foreach (TrendDumpExecution execution in executions)
        {
            foreach ((string stageName, AnalyzerMemoryStats stats) in execution.StageMemoryStats)
                allStageRows.Add((stageName, stats));
        }

        foreach ((string stageName, AnalyzerMemoryStats stats) in trendStageMemoryStats)
            allStageRows.Add((stageName, stats));

        bool printedStageTable = false;
        foreach (TrendDumpExecution execution in executions)
        {
            var dumpStageRows = execution.StageMemoryStats;
            if (dumpStageRows.Count == 0)
                continue;

            ConsoleUx.Info($"Memory diagnostics for dump: {Path.GetFileName(execution.DumpPath)}");
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string stageName, AnalyzerMemoryStats stats) in dumpStageRows)
                ConsoleUx.MemoryTableRow(stageName, stats.WorkingSetDelta, stats.WorkingSetAfter, stats.ManagedHeapDelta);

            printedStageTable = true;
        }

        if (trendStageMemoryStats.Count > 0)
        {
            ConsoleUx.Info("Memory diagnostics for trend pipeline:");
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string stageName, AnalyzerMemoryStats stats) in trendStageMemoryStats)
                ConsoleUx.MemoryTableRow(stageName, stats.WorkingSetDelta, stats.WorkingSetAfter, stats.ManagedHeapDelta);

            printedStageTable = true;
        }

        if (printedStageTable)
        {
            // Analyzer tables remain grouped per dump below.
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
        long peakFromStages = allStageRows.Count > 0 ? allStageRows.Max(e => e.Stats.WorkingSetAfter) : 0;
        long peakFromAnalyzers = 0;
        if (allRuns is not null)
        {
            foreach (var r in allRuns)
            {
                if (r.MemoryStats is null) continue;
                if (r.MemoryStats.WorkingSetAfter > peakFromAnalyzers) peakFromAnalyzers = r.MemoryStats.WorkingSetAfter;
            }
        }
        else
        {
            foreach (var r in executions.SelectMany(e => e.Runs))
            {
                if (r.MemoryStats is null) continue;
                if (r.MemoryStats.WorkingSetAfter > peakFromAnalyzers) peakFromAnalyzers = r.MemoryStats.WorkingSetAfter;
            }
        }
        long peak = Math.Max(peakFromStages, peakFromAnalyzers);

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }
}

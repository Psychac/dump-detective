using DumpDetective.Analysis.Insight;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Trend;

using System.Diagnostics;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Orchestrates a multi-dump trend analysis run: per-dump pipelines → trend report → output.
/// </summary>
/// TODO: Need to deeply review trend orchestration for potential optimizations, especially around parallelizing per-dump analysis and incremental report building. Initial implementation focuses on correctness and observability, with a simple sequential approach to per-dump analysis to maximize shared indexing benefits and minimize resource contention.
/// Potential future optimizations include: Fixing duplication in AnalysisSnapshot.
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

        // ── Stage 1: Analyze each trend dump ────────────────────────────────
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
            TrendConsolePresenter.PrintTrendDumpSummary(i + 1, trendDumpPaths.Count, execution, cumulativeDumpElapsed, resolved.DiagnosticMode);

            if (execution.Runs.Any(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation))
                throw new OperationCanceledException("Analysis canceled.");
        }

        stageStopwatch.Stop();
        analyzeDumpsElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(1, totalStages, "Analyze trend dumps", stageStopwatch.Elapsed);

        // ── Stage 2: Build trend report ─────────────────────────────────────
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
            TrendConsolePresenter.PrintTrendOverallSummary(trendData, resolved.DiagnosticMode);

        IReadOnlyList<AnalyzerRunResult> currentRuns = trendExecutions[^1].Runs;
        AnalysisReportDocument trendDoc = _reportBuilderFacade.BuildTrendReportDocument(
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

        // ── Stage 3: Write output ────────────────────────────────────────────
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
                TrendConsolePresenter.PrintDiagnosticsSummary(allRuns);
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
                TrendConsolePresenter.PrintMemorySummary(trendExecutions, trendStageMemoryStats, allRuns);

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
            string indexTarget = Path.GetFileName(execution.HeapIndex.IndexPath);
            ConsoleUx.ObjectScanComplete($"{fileLabel} Scan + Index heap", execution.HeapIndex.ObjectCount, execution.HeapIndex.Elapsed, $"{indexTarget}");
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
}

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Pipeline;
using DumpDetective.Reporting.Trend;

using System.Diagnostics;

namespace DumpDetective.Cli.Services;

internal sealed class DumpAnalysisService(
    ConfigurationResolver configurationResolver,
    StartupValidator startupValidator,
    DumpLoader dumpLoader,
    ReportBuilderFacade reportBuilderFacade,
    IAnalyzerFactory analyzerFactory,
    IEnumerable<IFindingGenerator> findingGenerators,
    FindingGenerationPipeline findingGenerationPipeline,
    TrendAnalyzer trendAnalyzer)
{
    private readonly ConfigurationResolver _configurationResolver = configurationResolver;
    private readonly StartupValidator _startupValidator = startupValidator;
    private readonly DumpLoader _dumpLoader = dumpLoader;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly IAnalyzerFactory _analyzerFactory = analyzerFactory;
    private readonly IReadOnlyList<IFindingGenerator> _findingGenerators = findingGenerators.ToList();
    private readonly FindingGenerationPipeline _findingGenerationPipeline = findingGenerationPipeline;
    private readonly TrendAnalyzer _trendAnalyzer = trendAnalyzer;
    private const string TemporaryAdaptiveIndexingNotice = "TEMP-ADAPTIVE-INDEXING: Auto mode uses a provisional dump-size threshold; tune memory-vs-disk selection with large-dump profiling.";

    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        ResolvedExecutionOptions resolved;
        try
        {
            resolved = _configurationResolver.Resolve(request);
            _startupValidator.Validate(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            throw new ConfigurationException(ex.Message, ex);
        }

        IReadOnlyList<IAnalyzer> analyzers = _analyzerFactory.CreateAnalyzers();
        ValidateAnalyzerFilters(resolved, analyzers);
        IReadOnlyList<IAnalyzer> activeAnalyzers = OrderAnalyzersForPipeline(ApplyAnalyzerFilters(resolved, analyzers));

        if (TryResolveTrendSequence(resolved, out IReadOnlyList<string>? trendDumpPaths))
        {
            return await ExecuteTrendAsync(resolved, activeAnalyzers, trendDumpPaths!, cancellationToken);
        }

        ConsoleUx.Header("DumpDetective Analysis");
        ConsoleUx.Warning(TemporaryAdaptiveIndexingNotice);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Config source: {(resolved.UsedConfigFile ? $"file ({resolved.ConfigPath})" : "CLI fallback")}");
            ConsoleUx.Info($"Active analyzers ({activeAnalyzers.Count}): {string.Join(", ", activeAnalyzers.Select(a => a.Name))}");
        }
        else
        {
            ConsoleUx.Info($"Analyzing dump: {Path.GetFileName(resolved.DumpPath)}");
            ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers...");
        }

        IReadOnlyList<IAnalysisStage> stages = BuildSingleDumpStages();

        using SingleDumpPipelineState state = new()
        {
            Resolved = resolved,
            ActiveAnalyzers = activeAnalyzers
        };

        await new StagedPipelineRunner().RunAsync(stages, state, cancellationToken);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Pipeline completed in {state.PipelineStopwatch.Elapsed.TotalSeconds:F1}s");
            ConsoleUx.Info($"Run summary: {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success)} success, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed)} failed, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)} skipped.");
            PrintDiagnosticsSummary(state.Runs);
        }

        totalStopwatch.Stop();
        ConsoleUx.Success($"Total analysis time: {totalStopwatch.Elapsed.TotalSeconds:F1}s");

        return state.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private IReadOnlyList<IAnalysisStage> BuildSingleDumpStages() =>
    [
        new LoadDumpStage(_dumpLoader),
        new BuildHeapIndexStage(),
        new RunAnalyzersPipelineStage(),
        new GenerateFindingsStage(_findingGenerationPipeline),
        new BuildReportStage(_reportBuilderFacade),
        new WriteOutputStage()
    ];

    private async Task<int> ExecuteTrendAsync(
        ResolvedExecutionOptions resolved,
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
        ConsoleUx.Warning(TemporaryAdaptiveIndexingNotice);
        ConsoleUx.Info($"Trend dumps ({trendDumpPaths.Count}): {string.Join(" -> ", trendDumpPaths.Select(Path.GetFileName))}");
        ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers per dump...");

        stageStopwatch.Restart();
        ConsoleUx.StageStart(1, totalStages, $"Analyze trend dumps ({trendDumpPaths.Count})");

        List<TrendDumpExecution> trendExecutions = new(trendDumpPaths.Count);
        for (int i = 0; i < trendDumpPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dumpPath = trendDumpPaths[i];
            string dumpName = Path.GetFileName(dumpPath);
            ConsoleUx.DumpStart(i + 1, trendDumpPaths.Count, dumpName);

            TrendDumpExecution execution = await ExecutePipelineForDumpAsync(dumpPath, resolved, activeAnalyzers, cancellationToken);
            trendExecutions.Add(execution);
            cumulativeDumpElapsed += execution.Elapsed;
            ConsoleUx.DumpComplete(i + 1, trendDumpPaths.Count, dumpName, execution.Elapsed);
            PrintTrendDumpSummary(i + 1, trendDumpPaths.Count, execution, cumulativeDumpElapsed, resolved.DiagnosticMode);

            if (execution.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Canceled))
            {
                throw new OperationCanceledException("Analysis canceled.");
            }
        }

        stageStopwatch.Stop();
        analyzeDumpsElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(1, totalStages, "Analyze trend dumps", stageStopwatch.Elapsed);

        stageStopwatch.Restart();
        ConsoleUx.StageStart(2, totalStages, $"Build {resolved.Report.Format} trend report");

        IReadOnlyList<AnalysisSnapshot> snapshots = trendExecutions
            .Select((execution, index) => BuildSnapshot(index, execution.DumpPath, execution.Runs))
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
        {
            PrintTrendOverallSummary(trendData, resolved.DiagnosticMode);
        }

        IReadOnlyList<AnalyzerRunResult> currentRuns = trendExecutions[^1].Runs;
        string renderedReport = _reportBuilderFacade.BuildRenderedTrendReport(
            trendDumpPaths[^1],
            resolved.Report.Format,
            resolved.Report.Audience,
            currentRuns,
            totalStopwatch.Elapsed,
            trendData,
            cancellationToken);

        stageStopwatch.Stop();
        buildReportElapsed = stageStopwatch.Elapsed;
        ConsoleUx.StageComplete(2, totalStages, "Build trend report", stageStopwatch.Elapsed);

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

            if (resolved.DiagnosticMode)
            {
                IReadOnlyList<AnalyzerRunResult> allRuns = trendExecutions.SelectMany(e => e.Runs).ToList();
                ConsoleUx.Info($"Trend pipeline completed in {totalStopwatch.Elapsed.TotalSeconds:F1}s");
                ConsoleUx.Info($"Run summary: {allRuns.Count(r => r.Status == AnalyzerExecutionStatus.Success)} success, {allRuns.Count(r => r.Status == AnalyzerExecutionStatus.Failed)} failed, {allRuns.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)} skipped.");
                PrintDiagnosticsSummary(allRuns);
            }

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
        {
            overhead = TimeSpan.Zero;
        }

        ConsoleUx.Info($"Trend time breakdown: dumps={analyzeDumpsElapsed.TotalSeconds:F1}s, report={buildReportElapsed.TotalMilliseconds:F0}ms, output={writeOutputElapsed.TotalMilliseconds:F0}ms, overhead={overhead.TotalMilliseconds:F0}ms");
        ConsoleUx.Success($"Total analysis time: {totalStopwatch.Elapsed.TotalSeconds:F1}s");

        return trendExecutions.Any(e => e.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed))
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private async Task<TrendDumpExecution> ExecutePipelineForDumpAsync(
        string dumpPath,
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(dumpPath, cancellationToken);
        HeapAnalysisCache heapCache = new();
        var heapIndex = heapCache.PrebuildHeapIndex(
            loadContext.Heap,
            dumpPath,
            cancellationToken,
            progress: new Progress<AnalyzerProgressReport>(r =>
                ConsoleUx.ObjectScanProgress($"[{Path.GetFileName(dumpPath)}] Scan + Index heap", r.ScannedCount, r.Elapsed ?? TimeSpan.Zero, "streaming objects to index")),
            mode: resolved.IndexPrebuildMode);
        string indexTarget = heapIndex.StorageKind == DumpDetective.Analysis.Indexing.HeapIndexStorageKind.Memory
            ? "in-memory"
            : Path.GetFileName(heapIndex.IndexPath);
        ConsoleUx.ObjectScanComplete($"[{Path.GetFileName(dumpPath)}] Scan + Index heap", heapIndex.ObjectCount, heapIndex.Elapsed, $"{heapIndex.StorageKind} • {indexTarget}");

        RuntimeAnalysisContext context = new()
        {
            Runtime = loadContext.Runtime,
            Heap = loadContext.Heap,
            Cache = heapCache,
            Diagnostics = resolved.Diagnostics,
            Options = new Dictionary<string, object?>
            {
                [nameof(Core.Options.MemoryLeakOptions)] = resolved.MemoryLeak,
                [nameof(Core.Options.ReferenceChainOptions)] = resolved.ReferenceChain,
                [nameof(Core.Options.EventLeakOptions)] = resolved.EventLeak,
                [nameof(Core.Options.DiagnosticsOptions)] = resolved.Diagnostics
            },
            MemoryLeakOptions = resolved.MemoryLeak,
            ReferenceChainOptions = resolved.ReferenceChain,
            EventLeakOptions = resolved.EventLeak,
            DiagnosticsOptions = resolved.Diagnostics,
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, activeAnalyzers)
        };

        AnalysisPipeline pipeline = new(activeAnalyzers);
        IReadOnlyList<AnalyzerRunResult> runs = await pipeline.ExecuteAsync(context, cancellationToken);

        // Generate findings for trend dumps as well so snapshots include interpreted findings
        try
        {
            runs = await _findingGenerationPipeline.GenerateAsync(runs, cancellationToken);
        }
        catch
        {
            // Swallow to avoid failing trend execution; diagnostics will surface elsewhere
        }
        stopwatch.Stop();

        return new TrendDumpExecution(dumpPath, runs, stopwatch.Elapsed);
    }

    private static AnalysisSnapshot BuildSnapshot(int index, string dumpPath, IReadOnlyList<AnalyzerRunResult> runs)
    {
        Dictionary<string, AnalyzerDomainResult> domains = new(StringComparer.Ordinal);
        List<InsightFinding> findings = [];

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
            {
                continue;
            }

            domains[run.AnalyzerName] = run.Result;
            findings.AddRange(run.Findings);
        }

        return new AnalysisSnapshot(
            Index: index,
            DumpPath: dumpPath,
            Findings: findings,
            DomainResults: domains,
            GeneratedAtUtc: DateTime.UtcNow);
    }

    private static bool TryResolveTrendSequence(ResolvedExecutionOptions resolved, out IReadOnlyList<string>? trendDumpPaths)
    {
        if (resolved.TrendDumpPaths is { Count: > 0 })
        {
            trendDumpPaths = resolved.TrendDumpPaths;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resolved.BaselineDumpPath))
        {
            trendDumpPaths = [resolved.BaselineDumpPath!, resolved.DumpPath];
            return true;
        }

        trendDumpPaths = null;
        return false;
    }

    private sealed record TrendDumpExecution(string DumpPath, IReadOnlyList<AnalyzerRunResult> Runs, TimeSpan Elapsed);

    private static void PrintTrendDumpSummary(int dumpIndex, int totalDumps, TrendDumpExecution execution, TimeSpan cumulativeDumpElapsed, bool diagnosticMode)
    {
        int success = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
        int failed = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
        int skipped = execution.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Skipped);
        long findings = execution.Runs.Sum(r => r.FindingCount);

        ConsoleUx.Success($"[{dumpIndex}/{totalDumps}] Completed {Path.GetFileName(execution.DumpPath)} in {execution.Elapsed.TotalSeconds:F1}s (cumulative dumps: {cumulativeDumpElapsed.TotalSeconds:F1}s) · success={success}, failed={failed}, skipped={skipped}, findings={findings}");

        IReadOnlyList<AnalyzerRunResult> failedRuns = execution.Runs
            .Where(r => r.Status == AnalyzerExecutionStatus.Failed)
            .ToList();

        if (failedRuns.Count > 0)
        {
            foreach (AnalyzerRunResult run in failedRuns)
            {
                string status = run.Status.ToString().ToLowerInvariant();
                ConsoleUx.Warning($"   - {run.AnalyzerName}: {status}, {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}");
            }
        }

        if (!diagnosticMode)
        {
            return;
        }

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
            : ordered.Where(a => a.Regressions.Count > 0 || a.Improvements.Count > 0)
                .Take(8)
                .ToList();

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

    private static void ValidateAnalyzerFilters(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
    {
        HashSet<string> known = analyzers.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> unknownIncludes = resolved.IncludeAnalyzers.Where(name => !known.Contains(name)).ToList();
        List<string> unknownExcludes = resolved.ExcludeAnalyzers.Where(name => !known.Contains(name)).ToList();

        if (unknownIncludes.Count > 0 || unknownExcludes.Count > 0)
        {
            List<string> messages = [];
            if (unknownIncludes.Count > 0)
            {
                messages.Add($"Unknown include analyzers: {string.Join(", ", unknownIncludes)}");
            }
            if (unknownExcludes.Count > 0)
            {
                messages.Add($"Unknown exclude analyzers: {string.Join(", ", unknownExcludes)}");
            }

            throw new ConfigurationException(string.Join(Environment.NewLine, messages));
        }
    }

    private static IReadOnlyList<IAnalyzer> ApplyAnalyzerFilters(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
    {
        IEnumerable<IAnalyzer> filtered = analyzers;

        if (resolved.IncludeAnalyzers.Count > 0)
        {
            HashSet<string> include = resolved.IncludeAnalyzers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(a => include.Contains(a.Name));
        }

        if (resolved.ExcludeAnalyzers.Count > 0)
        {
            HashSet<string> exclude = resolved.ExcludeAnalyzers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(a => !exclude.Contains(a.Name));
        }

        return filtered.ToList();
    }

    private static IReadOnlyList<IAnalyzer> OrderAnalyzersForPipeline(IReadOnlyList<IAnalyzer> analyzers)
    {
        return analyzers
            .OrderBy(GetStageRank)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetStageRank(IAnalyzer analyzer)
    {
        string typeName = analyzer.GetType().Name;
        return typeName switch
        {
            nameof(DumpDetective.Analysis.Analyzers.MemoryAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.GCGenerationAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.ModuleAnalyzer)
                => 0,

            nameof(DumpDetective.Analysis.Analyzers.CrashAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.HangAnalyzer)
                => 1,

            nameof(DumpDetective.Analysis.Analyzers.MemoryLeakAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.CollectionAnalyzer)
                => 2,

            nameof(DumpDetective.Analysis.Analyzers.StaticRootLeakDetector)
            or nameof(DumpDetective.Analysis.Analyzers.ReferenceChainAnalyzer)
                => 3,

            nameof(DumpDetective.Analysis.Analyzers.GCHandleAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.DependentHandleAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.LohFragmentationAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.ThreadStackClusterAnalyzer)
                => 4,

            nameof(DumpDetective.Analysis.Analyzers.ThreadAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.LockGraphAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.EventLeakAnalyzer)
                => 5,

            _ => 99
        };
    }

    private static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
        {
            return;
        }

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
        {
            ConsoleUx.Info($"  - {run.AnalyzerName}: {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
        }
    }
}

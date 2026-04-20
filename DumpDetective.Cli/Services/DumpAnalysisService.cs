using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using System.Diagnostics;

namespace DumpDetective.Cli.Services;

using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;

internal sealed class DumpAnalysisService(
    ConfigurationResolver configurationResolver,
    StartupValidator startupValidator,
    DumpLoader dumpLoader,
    ReportBuilderFacade reportBuilderFacade,
    IAnalyzerFactory analyzerFactory)
{
    private readonly ConfigurationResolver _configurationResolver = configurationResolver;
    private readonly StartupValidator _startupValidator = startupValidator;
    private readonly DumpLoader _dumpLoader = dumpLoader;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly IAnalyzerFactory _analyzerFactory = analyzerFactory;

    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        IReadOnlyList<IAnalyzer> analyzers = _analyzerFactory.CreateAnalyzers(resolved);
        ValidateAnalyzerFilters(resolved, analyzers);
        IReadOnlyList<IAnalyzer> activeAnalyzers = ApplyAnalyzerFilters(resolved, analyzers);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Config source: {(resolved.UsedConfigFile ? $"file ({resolved.ConfigPath})" : "CLI fallback")}");
            ConsoleUx.Info($"Active analyzers ({activeAnalyzers.Count}): {string.Join(", ", activeAnalyzers.Select(a => a.Name))}");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(resolved.DumpPath, cancellationToken);

        PipelineAnalysisContext context = new()
        {
            Runtime = loadContext.Runtime,
            Heap = loadContext.Heap,
            Cache = new HeapAnalysisCache(),
            Diagnostics = resolved.Diagnostics,
            MemoryLeakOptions = resolved.MemoryLeak,
            ReferenceChainOptions = resolved.ReferenceChain,
            EventLeakOptions = resolved.EventLeak,
            DiagnosticsOptions = resolved.Diagnostics,
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode)
        };

        AnalysisPipeline pipeline = new(activeAnalyzers);

        IReadOnlyList<AnalyzerRunResult> runs;
        try
        {
            runs = await pipeline.ExecuteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AnalysisPipelineException("Analysis pipeline failed unexpectedly.", ex);
        }

        if (runs.Any(r => r.Status == AnalyzerExecutionStatus.Canceled))
        {
            throw new OperationCanceledException("Analysis canceled.");
        }

        stopwatch.Stop();

        string renderedReport = _reportBuilderFacade.BuildRenderedReport(
            resolved.DumpPath,
            resolved.Report.Format,
            runs,
            stopwatch.Elapsed);

        try
        {
            if (!string.IsNullOrWhiteSpace(resolved.OutputPath))
            {
                File.WriteAllText(resolved.OutputPath, renderedReport);
                ConsoleUx.Success($"Report written to: {resolved.OutputPath}");
            }

            if (resolved.DiagnosticMode)
            {
                ConsoleUx.Info($"Pipeline completed in {stopwatch.Elapsed.TotalSeconds:F1}s");
                ConsoleUx.Info($"Run summary: {runs.Count(r => r.Status == AnalyzerExecutionStatus.Success)} success, {runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed)} failed, {runs.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)} skipped.");
                PrintDiagnosticsSummary(runs);
            }
        }
        catch (Exception ex)
        {
            throw new OutputWriteException("Failed while writing analysis output.", ex);
        }

        return runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
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

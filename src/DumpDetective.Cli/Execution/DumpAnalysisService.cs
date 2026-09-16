using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Discovery;
using DumpDetective.Orchestration.Session;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Execution;

internal sealed class DumpAnalysisService
{
    private readonly ConfigurationResolver _configurationResolver;
    private readonly StartupValidator _startupValidator;
    private readonly IAnalyzerFactory _analyzerFactory;
    private readonly IEnumerable<IFindingGenerator> _findingGenerators;
    private readonly IEnumerable<IAnalyzerTrendComparer> _trendComparers;
    private readonly ISectionBuilderFactory _sectionBuilderFactory;
    private readonly SingleDumpOrchestrationService _singleDumpOrchestration;
    private readonly TrendOrchestrationService _trendOrchestration;
    private readonly TraceOrchestrationService _traceOrchestration;

    // Interim, extension-sniffed routing — the debt docs/refactor/modularity-plan.md § 8 explicitly
    // accepts ("Accept an interim router... not a permanent design") in place of a real session
    // model (Phase 4). A trace file must be routed before any of the dump-specific setup below runs
    // (config resolution assumes a dump path; StartupValidator would reject a non-dump file), not
    // handled as a special case inside the dump pipeline.
    private static readonly string[] TraceFileExtensions = [".etl", ".nettrace"];

    internal static bool IsTraceFile(string? path) =>
        path is not null && TraceFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public DumpAnalysisService(
        ConfigurationResolver configurationResolver,
        StartupValidator startupValidator,
        IAnalyzerFactory analyzerFactory,
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory,
        SingleDumpOrchestrationService singleDumpOrchestration,
        TrendOrchestrationService trendOrchestration,
        TraceOrchestrationService traceOrchestration)
    {
        _configurationResolver = configurationResolver;
        _startupValidator = startupValidator;
        _analyzerFactory = analyzerFactory;
        _findingGenerators = findingGenerators;
        _trendComparers = trendComparers;
        _sectionBuilderFactory = sectionBuilderFactory;
        _singleDumpOrchestration = singleDumpOrchestration;
        _trendOrchestration = trendOrchestration;
        _traceOrchestration = traceOrchestration;
    }

    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsTraceFile(request.DumpPath))
            return await _traceOrchestration.ExecuteAsync(request.DumpPath!, request.OutputPath, cancellationToken);

        ResolvedExecutionOptions resolved;

        resolved = _configurationResolver.Resolve(request);
        _startupValidator.Validate(resolved);

        IReadOnlyList<IAnalyzer> analyzers = _analyzerFactory.CreateAnalyzers();

        _startupValidator.ValidateRegistrations(analyzers, _findingGenerators, _trendComparers, _sectionBuilderFactory);

        bool isTrend = TryResolveTrendSequence(resolved, out IReadOnlyList<string>? trendDumpPaths);
        IReadOnlyList<string> sessionDumpPaths = isTrend ? trendDumpPaths! : [resolved.DumpPath];

        // Capability-derived selection (docs/refactor/modularity/phase-4-session-orchestration.md
        // migration step 2, generalized to N artifacts in step 4) sits ahead of the existing
        // name-based include/exclude filter below. Every dump analyzer's *required* capabilities
        // are met by a dump-only session today (step 3 declared real per-analyzer requirements, but
        // none of the 36 require anything a dump can't provide), so nothing is ever skipped yet —
        // the real effect visible today is most analyzers running degraded (see CapabilityReport
        // below) for a missing optional trace capability. Skipping a whole analyzer needs the
        // dump+trace catalogs to be one catalog (step 5).
        IReadOnlyList<ArtifactDescriptor> artifacts = sessionDumpPaths
            .Select(SingleDumpArtifactFactory.Create)
            .ToArray();
        SessionValidator.Validate(artifacts);
        AnalysisSession session = SessionBuilder.Build(artifacts);
        CapabilityResolution resolution = CapabilityResolver.Resolve(analyzers, session.AvailableCapabilities);
        analyzers = resolution.Satisfied;

        if (resolved.DiagnosticMode)
            ConsoleUx.CapabilityReport(resolution);

        // combine filter validation, application and ordering into one local helper for clarity
        IReadOnlyList<IAnalyzer> GetActiveAnalyzers(ResolvedExecutionOptions opts, IReadOnlyList<IAnalyzer> all)
        {
            AnalyzerFilterService.Validate(opts, all);
            return AnalyzerFilterService.Order(AnalyzerFilterService.Apply(opts, all));
        }

        IReadOnlyList<IAnalyzer> activeAnalyzers = GetActiveAnalyzers(resolved, analyzers);

        if (isTrend)
            return await _trendOrchestration.ExecuteAsync(resolved, analyzers, activeAnalyzers, trendDumpPaths!, cancellationToken);
        return await _singleDumpOrchestration.ExecuteAsync(resolved, analyzers, activeAnalyzers, cancellationToken);
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
            trendDumpPaths = new[] { resolved.BaselineDumpPath!, resolved.DumpPath };
            return true;
        }

        trendDumpPaths = null;
        return false;
    }
}

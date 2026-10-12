using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Discovery;
using DumpDetective.Orchestration.Session;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Services;
using DumpDetective.Sources.NetTrace;

namespace DumpDetective.Cli.Execution;

internal sealed class DumpAnalysisService
{
    private readonly ConfigurationResolver _configurationResolver;
    private readonly StartupValidator _startupValidator;
    private readonly IAnalyzerFactory _analyzerFactory;
    private readonly IAnalyzerFeatureModuleCatalog _moduleCatalog;
    private readonly IEnumerable<IAnalyzerTrendComparer> _trendComparers;
    private readonly ISectionBuilderFactory _sectionBuilderFactory;
    private readonly DumpOrchestrationService _dumpOrchestration;
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
        IAnalyzerFeatureModuleCatalog moduleCatalog,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory,
        DumpOrchestrationService dumpOrchestration,
        TraceOrchestrationService traceOrchestration)
    {
        _configurationResolver = configurationResolver;
        _startupValidator = startupValidator;
        _analyzerFactory = analyzerFactory;
        _moduleCatalog = moduleCatalog;
        _trendComparers = trendComparers;
        _sectionBuilderFactory = sectionBuilderFactory;
        _dumpOrchestration = dumpOrchestration;
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

        _startupValidator.ValidateRegistrations(analyzers, _moduleCatalog, _trendComparers, _sectionBuilderFactory);

        bool isTrend = TryResolveTrendSequence(resolved, out IReadOnlyList<string>? trendDumpPaths);
        IReadOnlyList<string> sessionDumpPaths = isTrend ? trendDumpPaths! : [resolved.DumpPath];

        bool hasTrace = !string.IsNullOrWhiteSpace(resolved.TracePath);
        if (hasTrace && isTrend)
        {
            throw new ArgumentException(
                "--trace cannot be combined with --trend — a session accepts either N dumps or " +
                "exactly one dump plus one trace, not both at once.");
        }

        // Capability-derived selection (docs/refactor/modularity/phase-4-session-orchestration.md
        // migration step 2, generalized to N artifacts in step 4) sits ahead of the existing
        // name-based include/exclude filter below.
        IReadOnlyList<ArtifactDescriptor> dumpArtifacts = sessionDumpPaths
            .Select(SingleDumpArtifactFactory.Create)
            .ToArray();
        IReadOnlyList<ArtifactDescriptor> artifacts = hasTrace
            ? [.. dumpArtifacts, TraceArtifactFactory.Create(resolved.TracePath!)]
            : dumpArtifacts;
        SessionValidator.Validate(artifacts);
        AnalysisSession session = SessionBuilder.Build(artifacts);

        // Unified catalog (dump's own analyzers + the 3 trace analyzers, step 5b) purely so the
        // capability-satisfaction report below reflects the *whole* session, closing Phase 3's
        // half-open "a dump-only session correctly lists Plugins.Cpu as skipped" exit criterion
        // (see capability-mapping.md) — a dump-only session's AvailableCapabilities lacks every
        // trace.* capability, so the 3 trace analyzers naturally resolve as Unsatisfied here, not
        // because they're filtered out by name. Execution below still runs the trace half through
        // its own node triple (step 5f), not AnalysisPipeline — a trace analyzer handed to
        // AnalysisPipeline would just silently no-op (AnalysisContext never gets its Trace*
        // surfaces populated dump-side), which is correct-but-misleading ("ran, found nothing" vs.
        // "didn't run"), so trace analyzers are kept out of that pipeline entirely.
        IReadOnlyList<IAnalyzer> unifiedCatalog = [.. analyzers, new CpuHotspotAnalyzer(), new GcPauseAnalyzer(), new ContentionAnalyzer()];
        CapabilityResolution resolution = CapabilityResolver.Resolve(unifiedCatalog, session.AvailableCapabilities);

        if (resolved.DiagnosticMode)
            ConsoleUx.CapabilityReport(resolution);

        analyzers = FilterToDumpCatalog(resolution.Satisfied, analyzers);

        // combine filter validation, application and ordering into one local helper for clarity
        IReadOnlyList<IAnalyzer> GetActiveAnalyzers(ResolvedExecutionOptions opts, IReadOnlyList<IAnalyzer> all)
        {
            AnalyzerFilterService.Validate(opts, all);
            return AnalyzerFilterService.Order(AnalyzerFilterService.Apply(opts, all));
        }

        IReadOnlyList<IAnalyzer> activeAnalyzers = GetActiveAnalyzers(resolved, analyzers);

        return await _dumpOrchestration.ExecuteAsync(
            resolved, analyzers, activeAnalyzers, sessionDumpPaths, cancellationToken, tracePath: hasTrace ? resolved.TracePath : null,
            coverage: CoverageRecordBuilder.Build(session, resolution));
    }

    /// <summary>
    /// Narrows a capability resolution computed against the unified dump+trace catalog back down
    /// to just the dump-catalog instances, by reference — see the resolution's own remarks above
    /// for why a trace analyzer must never reach <c>AnalysisPipeline</c>. Reference equality (not
    /// name/type comparison) because <paramref name="satisfied"/> and <paramref name="dumpCatalog"/>
    /// are built from the exact same <see cref="IAnalyzer"/> instances by construction.
    /// </summary>
    internal static IReadOnlyList<IAnalyzer> FilterToDumpCatalog(
        IReadOnlyList<IAnalyzer> satisfied, IReadOnlyList<IAnalyzer> dumpCatalog)
    {
        var dumpCatalogIdentities = new HashSet<IAnalyzer>(dumpCatalog, ReferenceEqualityComparer.Instance);
        return satisfied.Where(dumpCatalogIdentities.Contains).ToArray();
    }

    private static bool TryResolveTrendSequence(ResolvedExecutionOptions resolved, out IReadOnlyList<string>? trendDumpPaths)
    {
        if (resolved.TrendDumpPaths is { Count: > 0 })
        {
            trendDumpPaths = resolved.TrendDumpPaths;
            return true;
        }

        trendDumpPaths = null;
        return false;
    }
}

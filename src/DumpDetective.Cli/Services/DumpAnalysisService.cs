using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services.Capabilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Services;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Services;

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

    public DumpAnalysisService(
        ConfigurationResolver configurationResolver,
        StartupValidator startupValidator,
        IAnalyzerFactory analyzerFactory,
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory,
        SingleDumpOrchestrationService singleDumpOrchestration,
        TrendOrchestrationService trendOrchestration)
    {
        _configurationResolver = configurationResolver;
        _startupValidator = startupValidator;
        _analyzerFactory = analyzerFactory;
        _findingGenerators = findingGenerators;
        _trendComparers = trendComparers;
        _sectionBuilderFactory = sectionBuilderFactory;
        _singleDumpOrchestration = singleDumpOrchestration;
        _trendOrchestration = trendOrchestration;
    }

    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        ResolvedExecutionOptions resolved;
        
        resolved = _configurationResolver.Resolve(request);
        _startupValidator.Validate(resolved);

        IReadOnlyList<IAnalyzer> analyzers = _analyzerFactory.CreateAnalyzers();
        
        _startupValidator.ValidateRegistrations(analyzers, _findingGenerators, _trendComparers, _sectionBuilderFactory);

        IEnumerable<IFindingGenerator> findingGeneratorList = _findingGenerators;
        IEnumerable<IAnalyzerTrendComparer> trendComparerList = _trendComparers;
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders = _sectionBuilderFactory.CreateAnalyzerBuilders();

        IReadOnlyList<AnalyzerFeatureModule> resolvedModules = AnalyzerFeatureModuleAdapter.CreateResolvedModules(
            analyzers,
            findingGeneratorList,
            trendComparerList,
            analyzerSectionBuilders);

        AnalyzerFeatureModuleCoverage resolvedCoverage = AnalyzerFeatureModuleAdapter.ComputeCoverage(
            resolvedModules,
            analyzers,
            findingGeneratorList,
            trendComparerList,
            analyzerSectionBuilders);
        // compute and validate coverage for both resolved and spike modules
        AnalyzerFeatureModuleCoverage ComputeAndValidateCoverage(IEnumerable<AnalyzerFeatureModule> modules, string description, bool requireFull)
        {
            var cov = AnalyzerFeatureModuleAdapter.ComputeCoverage(
                modules,
                analyzers,
                findingGeneratorList,
                trendComparerList,
                analyzerSectionBuilders);
            _startupValidator.ValidateFeatureModuleCoverage(cov, requireFullCoverage: requireFull, description);
            return cov;
        }

        AnalyzerFeatureModuleCoverage resolvedCoverageValidated = ComputeAndValidateCoverage(resolvedModules, "resolved capability modules", requireFull: true);
        AnalyzerFeatureModuleCoverage spikeCoverageValidated = ComputeAndValidateCoverage(AnalyzerFeatureModuleSpikeCatalog.CreateSpikeModules(), "spike capability modules", requireFull: false);

        // combine filter validation, application and ordering into one local helper for clarity
        IReadOnlyList<IAnalyzer> GetActiveAnalyzers(ResolvedExecutionOptions opts, IReadOnlyList<IAnalyzer> all)
        {
            AnalyzerFilterService.Validate(opts, all);
            return AnalyzerFilterService.Order(AnalyzerFilterService.Apply(opts, all));
        }

        IReadOnlyList<IAnalyzer> activeAnalyzers = GetActiveAnalyzers(resolved, analyzers);
    
        if (TryResolveTrendSequence(resolved, out IReadOnlyList<string>? trendDumpPaths))
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

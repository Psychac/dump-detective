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

internal sealed class DumpAnalysisService(
    ConfigurationResolver configurationResolver,
    StartupValidator startupValidator,
    IAnalyzerFactory analyzerFactory,
    IEnumerable<IFindingGenerator> findingGenerators,
    IEnumerable<IAnalyzerTrendComparer> trendComparers,
    ISectionBuilderFactory sectionBuilderFactory,
    SingleDumpOrchestrationService singleDumpOrchestration,
    TrendOrchestrationService trendOrchestration)
{
    private readonly ConfigurationResolver _configurationResolver = configurationResolver;
    private readonly StartupValidator _startupValidator = startupValidator;
    private readonly IAnalyzerFactory _analyzerFactory = analyzerFactory;
    private readonly IEnumerable<IFindingGenerator> _findingGenerators = findingGenerators;
    private readonly IEnumerable<IAnalyzerTrendComparer> _trendComparers = trendComparers;
    private readonly ISectionBuilderFactory _sectionBuilderFactory = sectionBuilderFactory;
    private readonly SingleDumpOrchestrationService _singleDumpOrchestration = singleDumpOrchestration;
    private readonly TrendOrchestrationService _trendOrchestration = trendOrchestration;

    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolvedExecutionOptions resolved;
        try { resolved = _configurationResolver.Resolve(request); _startupValidator.Validate(resolved); }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException) { throw new ConfigurationException(ex.Message, ex); }
        IReadOnlyList<IAnalyzer> analyzers = _analyzerFactory.CreateAnalyzers();
        _startupValidator.ValidateRegistrations(analyzers, _findingGenerators, _trendComparers, _sectionBuilderFactory);

        IReadOnlyList<IFindingGenerator> findingGeneratorList = _findingGenerators.ToList();
        IReadOnlyList<IAnalyzerTrendComparer> trendComparerList = _trendComparers.ToList();
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

        _startupValidator.ValidateFeatureModuleCoverage(resolvedCoverage, requireFullCoverage: true, "resolved capability modules");

        AnalyzerFeatureModuleCoverage spikeCoverage = AnalyzerFeatureModuleAdapter.ComputeCoverage(
            AnalyzerFeatureModuleSpikeCatalog.CreateSpikeModules(),
            analyzers,
            findingGeneratorList,
            trendComparerList,
            analyzerSectionBuilders);

        _startupValidator.ValidateFeatureModuleCoverage(spikeCoverage, requireFullCoverage: false, "spike capability modules");

        AnalyzerFilterService.Validate(resolved, analyzers);
        IReadOnlyList<IAnalyzer> activeAnalyzers = AnalyzerFilterService.Order(AnalyzerFilterService.Apply(resolved, analyzers));
        if (TryResolveTrendSequence(resolved, out IReadOnlyList<string>? trendDumpPaths))
            return await _trendOrchestration.ExecuteAsync(resolved, analyzers, activeAnalyzers, trendDumpPaths!, cancellationToken);
        return await _singleDumpOrchestration.ExecuteAsync(resolved, analyzers, activeAnalyzers, cancellationToken);
    }
    private static bool TryResolveTrendSequence(ResolvedExecutionOptions resolved, out IReadOnlyList<string>? trendDumpPaths)
    {
        if (resolved.TrendDumpPaths is { Count: > 0 }) { trendDumpPaths = resolved.TrendDumpPaths; return true; }
        if (!string.IsNullOrWhiteSpace(resolved.BaselineDumpPath)) { trendDumpPaths = [resolved.BaselineDumpPath!, resolved.DumpPath]; return true; }
        trendDumpPaths = null; return false;
    }
}

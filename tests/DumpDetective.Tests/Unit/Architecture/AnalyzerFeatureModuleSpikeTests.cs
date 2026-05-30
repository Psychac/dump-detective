using DumpDetective.Cli.Services.Capabilities;
using DumpDetective.Cli.Hosting;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

public sealed class AnalyzerFeatureModuleSpikeTests
{
    [Fact]
    public void SpikeCatalog_ShouldProvideThreeValidModules_WithUniqueKeys()
    {
        IReadOnlyList<AnalyzerFeatureModule> modules = AnalyzerFeatureModuleSpikeCatalog.CreateSpikeModules();

        modules.Should().HaveCount(3);
        modules.Select(m => m.Key).Should().OnlyHaveUniqueItems();
        modules.Should().OnlyContain(m => m.IsShapeValid());

        modules.Should().Contain(m => m.Key == "memory");
        modules.Should().Contain(m => m.Key == "thread");
        modules.Should().Contain(m => m.Key == "dominator");
    }

    [Fact]
    public void SpikeCatalog_TypesShouldMatchExpectedContracts()
    {
        IReadOnlyList<AnalyzerFeatureModule> modules = AnalyzerFeatureModuleSpikeCatalog.CreateSpikeModules();

        foreach (AnalyzerFeatureModule module in modules)
        {
            typeof(IAnalyzer).IsAssignableFrom(module.AnalyzerType).Should().BeTrue();
            typeof(IFindingGenerator).IsAssignableFrom(module.FindingGeneratorType).Should().BeTrue();
            typeof(IAnalyzerTrendComparer).IsAssignableFrom(module.TrendComparerType).Should().BeTrue();
            typeof(IAnalyzerSectionBuilder).IsAssignableFrom(module.AnalyzerSectionBuilderType).Should().BeTrue();
        }
    }

    [Fact]
    public void ResolvedModules_ShouldFullyCoverActiveRuntimeCatalog()
    {
        using var host = ServiceRegistration.BuildHost([]);

        IAnalyzerFactory analyzerFactory = host.Services.GetRequiredService<IAnalyzerFactory>();
        ISectionBuilderFactory sectionBuilderFactory = host.Services.GetRequiredService<ISectionBuilderFactory>();
        IReadOnlyList<IFindingGenerator> findingGenerators = host.Services.GetServices<IFindingGenerator>().ToList();
        IReadOnlyList<IAnalyzerTrendComparer> trendComparers = host.Services.GetServices<IAnalyzerTrendComparer>().ToList();
        IReadOnlyList<IAnalyzer> analyzers = analyzerFactory.CreateAnalyzers();
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders = sectionBuilderFactory.CreateAnalyzerBuilders();

        IReadOnlyList<AnalyzerFeatureModule> resolvedModules = AnalyzerFeatureModuleAdapter.CreateResolvedModules(
            analyzers,
            findingGenerators,
            trendComparers,
            analyzerSectionBuilders);

        AnalyzerFeatureModuleCoverage coverage = AnalyzerFeatureModuleAdapter.ComputeCoverage(
            resolvedModules,
            analyzers,
            findingGenerators,
            trendComparers,
            analyzerSectionBuilders);

        coverage.HasFullCoverage.Should().BeTrue();
        coverage.MissingAnalyzerModules.Should().BeEmpty();
        coverage.MissingFindingGenerators.Should().BeEmpty();
        coverage.MissingTrendComparers.Should().BeEmpty();
        coverage.MissingAnalyzerSectionBuilders.Should().BeEmpty();
    }
}

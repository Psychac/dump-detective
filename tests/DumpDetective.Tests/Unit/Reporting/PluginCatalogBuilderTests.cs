using DumpDetective.Reporting.Capabilities;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// The "before/after catalog snapshot diff" docs/refactor/modularity/phase-3-plugin-packaging.md's
/// migration step 4 asks for, run now instead of at swap-in time so a convention/attribute-porting
/// mistake is caught while <see cref="DefaultAnalyzerFeatureModuleCatalog"/> still exists to diff
/// against.
/// </summary>
public sealed class PluginCatalogBuilderTests
{
    private static IAnalyzerFeatureModuleCatalog BuildScannedCatalog()
    {
        // DbConnectionAnalyzer anchors the still-in-Analysis assembly now that LeakCandidateAnalyzer
        // (this test's anchor before the Plugins.Leaks migration) has moved into its own package —
        // see docs/refactor/modularity/phase-3-plugin-packaging.md.
        return new PluginCatalogBuilder().Build(
            typeof(DumpDetective.Analysis.Analyzers.Infra.DbConnectionAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Gc.GCRootAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Memory.MemoryAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Threads.ThreadAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Async.AsyncTaskAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Types.ArrayAnalyzer).Assembly,
            typeof(DumpDetective.Analysis.Analyzers.Leaks.LeakCandidateAnalyzer).Assembly);
    }

    [Fact]
    public void Build_ProducesSameModuleCountAsDefaultCatalog()
    {
        IAnalyzerFeatureModuleCatalog expected = new DefaultAnalyzerFeatureModuleCatalog();
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().HaveCount(expected.Modules.Count);
    }

    [Fact]
    public void Build_ProducesModulesShapeIdenticalToDefaultCatalog()
    {
        IAnalyzerFeatureModuleCatalog expected = new DefaultAnalyzerFeatureModuleCatalog();
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        var expectedByKey = expected.Modules.ToDictionary(m => m.Key);
        var actualByKey = actual.Modules.ToDictionary(m => m.Key);

        actualByKey.Keys.Should().BeEquivalentTo(expectedByKey.Keys);

        foreach (string key in expectedByKey.Keys)
        {
            AnalyzerFeatureModule expectedModule = expectedByKey[key];
            AnalyzerFeatureModule actualModule = actualByKey[key];

            actualModule.DisplayName.Should().Be(expectedModule.DisplayName, "key {0}", key);
            actualModule.AnalyzerType.Should().Be(expectedModule.AnalyzerType, "key {0}", key);
            actualModule.FindingGeneratorType.Should().Be(expectedModule.FindingGeneratorType, "key {0}", key);
            actualModule.TrendComparerType.Should().Be(expectedModule.TrendComparerType, "key {0}", key);
            actualModule.AnalyzerSectionBuilderType.Should().Be(expectedModule.AnalyzerSectionBuilderType, "key {0}", key);
            actualModule.Order.Should().Be(expectedModule.Order, "key {0}", key);
            actualModule.Tags.Should().BeEquivalentTo(expectedModule.Tags, "key {0}", key);
        }
    }

    [Fact]
    public void Build_ProducesSameGlobalReportSectionBuilderTypesAsDefaultCatalog()
    {
        IAnalyzerFeatureModuleCatalog expected = new DefaultAnalyzerFeatureModuleCatalog();
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.GlobalReportSectionBuilderTypes.Should().BeEquivalentTo(expected.GlobalReportSectionBuilderTypes);
    }

    [Fact]
    public void Build_EveryModuleIsShapeValid()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().OnlyContain(m => m.IsShapeValid());
    }
}

using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Hosting;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace DumpDetective.Tests.Unit.Hosting;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void BuildHost_ResolvesAllRegisteredServices_WithoutThrowing()
    {
        using IHost host = ServiceRegistration.BuildHost(Array.Empty<string>());
        IServiceProvider services = host.Services;

        services.GetRequiredService<RootCommandBuilder>().Should().NotBeNull();
        services.GetRequiredService<IAnalyzerFeatureModuleCatalog>().Should().NotBeNull();
        services.GetRequiredService<ConfigurationResolver>().Should().NotBeNull();
        services.GetRequiredService<StartupValidator>().Should().NotBeNull();
        services.GetRequiredService<IDumpLoader>().Should().NotBeNull();
        services.GetRequiredService<AnalyzerExecutionService>().Should().NotBeNull();
        services.GetRequiredService<ReportOutputWriter>().Should().NotBeNull();
        services.GetRequiredService<DumpAnalysisService>().Should().NotBeNull();
        services.GetRequiredService<DumpOrchestrationService>().Should().NotBeNull();
        services.GetRequiredService<TraceOrchestrationService>().Should().NotBeNull();
        services.GetRequiredService<IAnalyzerFactory>().Should().NotBeNull();
        services.GetRequiredService<ISectionBuilderFactory>().Should().NotBeNull();
        services.GetRequiredService<TrendAnalyzer>().Should().NotBeNull();
        services.GetRequiredService<TrendReportComposer>().Should().NotBeNull();
        services.GetRequiredService<ExecutiveSummaryProjector>().Should().NotBeNull();
        services.GetRequiredService<ReportSerializer>().Should().NotBeNull();
        services.GetRequiredService<ReportBuilderFacade>().Should().NotBeNull();
        services.GetServices<IReportFormatter>().Should().HaveCount(4);
        services.GetServices<IAnalyzerTrendComparer>().Should().NotBeEmpty();
    }

    /// <summary>
    /// StartupValidator.ValidateRegistrations had zero direct test coverage before this — exactly
    /// how a real bug (AnalyzerFeatureModule.DisplayName used as the join key for retired-analyzer
    /// coverage, silently wrong for ThreadStackClusterAnalyzer, whose DisplayName "Thread Stack
    /// Cluster Analysis" differs from its Name "Thread Stack Signature Clustering") went uncaught
    /// until a stricter PluginCatalogBuilderTests check surfaced it. This exercises the real,
    /// production DI graph end to end, not a synthetic scenario, so it would have caught that.
    /// </summary>
    [Fact]
    public void ValidateRegistrations_DoesNotThrow_ForTheRealProductionCatalog()
    {
        using IHost host = ServiceRegistration.BuildHost(Array.Empty<string>());
        IServiceProvider services = host.Services;

        IReadOnlyList<DumpDetective.Sdk.Analysis.IAnalyzer> analyzers = services.GetRequiredService<IAnalyzerFactory>().CreateAnalyzers();
        IAnalyzerFeatureModuleCatalog moduleCatalog = services.GetRequiredService<IAnalyzerFeatureModuleCatalog>();
        IEnumerable<IAnalyzerTrendComparer> trendComparers = services.GetServices<IAnalyzerTrendComparer>();
        ISectionBuilderFactory sectionBuilderFactory = services.GetRequiredService<ISectionBuilderFactory>();
        StartupValidator validator = services.GetRequiredService<StartupValidator>();

        Action act = () => validator.ValidateRegistrations(
            analyzers, moduleCatalog, trendComparers, sectionBuilderFactory);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildHost_RegistersEverythingAsSingleton()
    {
        using IHost host = ServiceRegistration.BuildHost(Array.Empty<string>());
        IServiceProvider services = host.Services;

        services.GetRequiredService<DumpOrchestrationService>()
            .Should().BeSameAs(services.GetRequiredService<DumpOrchestrationService>());
        services.GetRequiredService<TraceOrchestrationService>()
            .Should().BeSameAs(services.GetRequiredService<TraceOrchestrationService>());
    }
}

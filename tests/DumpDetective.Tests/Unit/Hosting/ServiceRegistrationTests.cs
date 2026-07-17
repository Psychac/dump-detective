using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.FindingGenerators;
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
        services.GetRequiredService<PerDumpExecutionService>().Should().NotBeNull();
        services.GetRequiredService<ReportOutputWriter>().Should().NotBeNull();
        services.GetRequiredService<DumpAnalysisService>().Should().NotBeNull();
        services.GetRequiredService<SingleDumpOrchestrationService>().Should().NotBeNull();
        services.GetRequiredService<TrendOrchestrationService>().Should().NotBeNull();
        services.GetRequiredService<IAnalyzerFactory>().Should().NotBeNull();
        services.GetRequiredService<ISectionBuilderFactory>().Should().NotBeNull();
        services.GetRequiredService<FindingGenerationPipeline>().Should().NotBeNull();
        services.GetRequiredService<CanonicalReportDocumentFactory>().Should().NotBeNull();
        services.GetRequiredService<TrendAnalyzer>().Should().NotBeNull();
        services.GetRequiredService<TrendReportComposer>().Should().NotBeNull();
        services.GetRequiredService<ExecutiveSummaryProjector>().Should().NotBeNull();
        services.GetRequiredService<ReportSerializer>().Should().NotBeNull();
        services.GetRequiredService<ReportBuilderFacade>().Should().NotBeNull();
        services.GetServices<IReportFormatter>().Should().HaveCount(4);
        services.GetServices<IFindingGenerator>().Should().NotBeEmpty();
        services.GetServices<IAnalyzerTrendComparer>().Should().NotBeEmpty();
    }

    [Fact]
    public void BuildHost_RegistersEverythingAsSingleton()
    {
        using IHost host = ServiceRegistration.BuildHost(Array.Empty<string>());
        IServiceProvider services = host.Services;

        services.GetRequiredService<SingleDumpOrchestrationService>()
            .Should().BeSameAs(services.GetRequiredService<SingleDumpOrchestrationService>());
        services.GetRequiredService<TrendOrchestrationService>()
            .Should().BeSameAs(services.GetRequiredService<TrendOrchestrationService>());
    }
}

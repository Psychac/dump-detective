using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.Pipeline;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DumpDetective.Cli.Hosting;

internal static class ServiceRegistration
{
    public static IHost BuildHost(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        IServiceCollection services = builder.Services;

        // Scope debug-level logging to DumpDetective namespaces only to avoid framework noise.
        // CollectionAnalyzer diagnostics (field detection, waste scan summary) log at Debug.
        builder.Logging
            .SetMinimumLevel(LogLevel.Warning);
            //.AddFilter("DumpDetective", LogLevel.Debug);

        services.AddSingleton<RootCommandBuilder>();

        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<StartupValidator>();
        services.AddSingleton<DumpLoader>();
        services.AddSingleton<DumpAnalysisService>();
        services.AddSingleton<IAnalyzerFactory, DefaultAnalyzerFactory>();
        services.AddSingleton<IAnalyzerReporterFactory, DefaultAnalyzerReporterFactory>();

        // Finding generators — one per analyzer, registered as IFindingGenerator
        services.AddSingleton<IFindingGenerator, MemoryFindingGenerator>();
        services.AddSingleton<IFindingGenerator, MemoryLeakFindingGenerator>();
        services.AddSingleton<IFindingGenerator, GCGenerationFindingGenerator>();
        services.AddSingleton<IFindingGenerator, CrashFindingGenerator>();
        services.AddSingleton<IFindingGenerator, EventLeakFindingGenerator>();
        services.AddSingleton<IFindingGenerator, GCHandleFindingGenerator>();
        services.AddSingleton<IFindingGenerator, LohFragmentationFindingGenerator>();
        services.AddSingleton<IFindingGenerator, HangFindingGenerator>();
        services.AddSingleton<IFindingGenerator, LockGraphFindingGenerator>();
        services.AddSingleton<IFindingGenerator, StaticRootFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ReferenceChainFindingGenerator>();
        services.AddSingleton<IFindingGenerator, CollectionFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ThreadFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ThreadStackClusterFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ModuleFindingGenerator>();
        services.AddSingleton<IFindingGenerator, DependentHandleFindingGenerator>();

        // Finding generation pipeline (runs after analysis to generate insight findings)
        services.AddSingleton<FindingGenerationPipeline>();

        services.AddSingleton<TrendReportComposer>();
        services.AddSingleton<ReportBuilderFacade>();
        services.AddSingleton<IReportFormatter, TextCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, MarkdownCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, HtmlCanonicalReportFormatter>();

        return builder.Build();
    }
}

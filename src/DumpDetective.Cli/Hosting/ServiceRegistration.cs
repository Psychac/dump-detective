using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.FindingGenerators;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;
using DumpDetective.Analysis.Trend;
using DumpDetective.Analysis.Trend.Comparers;

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

        DefaultAnalyzerFeatureModuleCatalog moduleCatalog = new();
        services.AddSingleton<IAnalyzerFeatureModuleCatalog>(moduleCatalog);

        services.AddSingleton<DumpDetective.Cli.Configuration.ConfigurationResolver>();
        services.AddSingleton<StartupValidator>();
        services.AddSingleton<IDumpLoader, DumpLoader>();
        services.AddSingleton<DumpDetective.Cli.Execution.AnalyzerExecutionService>();
        services.AddSingleton<PerDumpExecutionService>();
        services.AddSingleton<ReportOutputWriter>();
        services.AddSingleton<DumpAnalysisService>();
        services.AddSingleton<SingleDumpStageFactory>();
        services.AddSingleton<SingleDumpOrchestrationService>();
        services.AddSingleton<TrendOrchestrationService>();
        services.AddSingleton<IAnalyzerFactory, DefaultAnalyzerFactory>();
        services.AddSingleton<ISectionBuilderFactory, DefaultSectionBuilderFactory>();

        foreach (AnalyzerFeatureModule module in moduleCatalog.Modules)
        {
            services.AddSingleton(typeof(IFindingGenerator), sp => ActivatorUtilities.CreateInstance(sp, module.FindingGeneratorType));
        }

        services.AddSingleton<FindingGenerationPipeline>();
        services.AddSingleton<CanonicalReportDocumentFactory>();

        foreach (AnalyzerFeatureModule module in moduleCatalog.Modules)
        {
            services.AddSingleton(typeof(IAnalyzerTrendComparer), sp => ActivatorUtilities.CreateInstance(sp, module.TrendComparerType));
        }

        services.AddSingleton<TrendAnalyzer>();

        services.AddSingleton<TrendReportComposer>();
        services.AddSingleton<ExecutiveSummaryProjector>();
        services.AddSingleton<ReportSerializer>();
        services.AddSingleton<ReportBuilderFacade>();
        services.AddSingleton<IReportFormatter, TextCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, MarkdownCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, HtmlReportRenderer>();
        services.AddSingleton<IReportFormatter, JsonCanonicalReportFormatter>();

        return builder.Build();
    }
}

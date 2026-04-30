using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.Pipeline;
using DumpDetective.Analysis.Dump;
using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
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

        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<StartupValidator>();
        services.AddSingleton<IDumpLoader, DumpLoader>();
        services.AddSingleton<DumpAnalysisService>();
        services.AddSingleton<SingleDumpOrchestrationService>();
        services.AddSingleton<TrendOrchestrationService>();
        services.AddSingleton<IAnalyzerFactory, DefaultAnalyzerFactory>();
        services.AddSingleton<ISectionBuilderFactory, DefaultSectionBuilderFactory>();

        // Finding generators — one per analyzer, registered as IFindingGenerator
        services.AddSingleton<IFindingGenerator, MemoryFindingGenerator>();
        services.AddSingleton<IFindingGenerator, MemoryLeakFindingGenerator>();
        services.AddSingleton<IFindingGenerator, StringFindingGenerator>();
        services.AddSingleton<IFindingGenerator, GCGenerationFindingGenerator>();
        services.AddSingleton<IFindingGenerator, CrashFindingGenerator>();
        services.AddSingleton<IFindingGenerator, EventLeakFindingGenerator>();
        services.AddSingleton<IFindingGenerator, GCHandleFindingGenerator>();
        services.AddSingleton<IFindingGenerator, LohFragmentationFindingGenerator>();
        services.AddSingleton<IFindingGenerator, HangFindingGenerator>();
        services.AddSingleton<IFindingGenerator, AsyncTaskFindingGenerator>();
        services.AddSingleton<IFindingGenerator, LockGraphFindingGenerator>();
        services.AddSingleton<IFindingGenerator, StaticRootFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ReferenceChainFindingGenerator>();
        services.AddSingleton<IFindingGenerator, CollectionFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ThreadFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ThreadStackClusterFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ModuleFindingGenerator>();
        services.AddSingleton<IFindingGenerator, DependentHandleFindingGenerator>();
        services.AddSingleton<IFindingGenerator, SegmentFindingGenerator>();
        services.AddSingleton<IFindingGenerator, AllocationPatternFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ObjectShapeFindingGenerator>();
        services.AddSingleton<IFindingGenerator, GCRootFindingGenerator>();
        services.AddSingleton<IFindingGenerator, FinalizableObjectFindingGenerator>();
        services.AddSingleton<IFindingGenerator, AsyncStateMachineFindingGenerator>();
        services.AddSingleton<IFindingGenerator, ArrayFindingGenerator>();

        // Finding generation pipeline (runs after analysis to generate insight findings)
        services.AddSingleton<FindingGenerationPipeline>();

        // Trend comparers — one per analyzer, registered as IAnalyzerTrendComparer.
        // TrendAnalyzer consumes IEnumerable<IAnalyzerTrendComparer> via DI.
        // Keep this list in sync with DefaultAnalyzerFactory and the finding generators above.
        services.AddSingleton<IAnalyzerTrendComparer, MemoryAnalyzerTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, MemoryLeakTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, GCGenerationTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, CrashTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, EventLeakTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, GCHandleTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, LohFragmentationTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, HangTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, AsyncTaskTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, LockGraphTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, StaticRootTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ReferenceChainTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, CollectionTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ThreadTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ThreadStackClusterTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ModuleTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, DependentHandleTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, SegmentTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, StringTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, AllocationPatternTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ObjectShapeTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, GCRootTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, FinalizableObjectTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, AsyncStateMachineTrendComparer>();
        services.AddSingleton<IAnalyzerTrendComparer, ArrayTrendComparer>();
        services.AddSingleton<TrendAnalyzer>();

        services.AddSingleton<TrendReportComposer>();
        services.AddSingleton<ReportSerializer>();
        services.AddSingleton<ReportBuilderFacade>();
        services.AddSingleton<IReportFormatter, TextCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, MarkdownCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, HtmlReportRenderer>();
        services.AddSingleton<IReportFormatter, JsonCanonicalReportFormatter>();

        return builder.Build();
    }
}

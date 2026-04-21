using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DumpDetective.Cli.Hosting;

internal static class ServiceRegistration
{
    public static IHost BuildHost(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        IServiceCollection services = builder.Services;

        services.AddSingleton<RootCommandBuilder>();

        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<StartupValidator>();
        services.AddSingleton<DumpLoader>();
        services.AddSingleton<DumpAnalysisService>();
        services.AddSingleton<IAnalyzerFactory, DefaultAnalyzerFactory>();
        services.AddSingleton<IAnalyzerReporterFactory, DefaultAnalyzerReporterFactory>();

        services.AddSingleton<TrendReportComposer>();
        services.AddSingleton<ReportBuilderFacade>();
        services.AddSingleton<IReportFormatter, TextCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, MarkdownCanonicalReportFormatter>();
        services.AddSingleton<IReportFormatter, HtmlCanonicalReportFormatter>();

        return builder.Build();
    }
}

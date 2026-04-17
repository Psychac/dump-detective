using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services;

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

        services.AddSingleton<ReportBuilderFacade>();

        return builder.Build();
    }
}

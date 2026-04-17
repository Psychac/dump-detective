using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Hosting;

internal static class ServiceRegistration
{
    // TEMP-REFRACTOR-BRIDGE: Replace factory wiring with IHost/DI registrations in Spec 04.
    public static DumpAnalysisService CreateDumpAnalysisService()
    {
        ConfigurationResolver configurationResolver = new();
        StartupValidator startupValidator = new();
        return new DumpAnalysisService(configurationResolver, startupValidator);
    }
}

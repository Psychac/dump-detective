using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;

namespace DumpDetective.Cli.Services;

internal sealed class DumpAnalysisService(
    ConfigurationResolver configurationResolver,
    StartupValidator startupValidator)
{
    private readonly ConfigurationResolver _configurationResolver = configurationResolver;
    private readonly StartupValidator _startupValidator = startupValidator;

    public Task<int> ExecuteAsync(CliArguments cliArguments, CancellationToken cancellationToken)
    {
        ResolvedExecutionOptions resolved = _configurationResolver.Resolve(cliArguments);
        _startupValidator.Validate(resolved);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(3);
        }

        // TEMP-REFRACTOR-BRIDGE: Replace summary output with full analysis orchestration via DI pipeline/reporting services.
        ConsoleUx.Info($"Config source: {(resolved.UsedConfigFile ? $"file ({resolved.ConfigPath})" : "CLI fallback")}");
        ConsoleUx.Info($"DumpPath: {resolved.DumpPath}");
        ConsoleUx.Info($"Report format: {resolved.Report.Format}");
        ConsoleUx.Success("Spec 02 configuration resolution and validation completed.");

        return Task.FromResult(0);
    }
}

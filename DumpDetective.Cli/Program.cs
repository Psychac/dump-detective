using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Hosting;
using DumpDetective.Cli.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DumpDetective.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using IHost host = ServiceRegistration.BuildHost(args);
        using var cts = new CancellationTokenSource();

        System.Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        RootCommandBuilder commandBuilder = host.Services.GetRequiredService<RootCommandBuilder>();
        DumpAnalysisService analysisService = host.Services.GetRequiredService<DumpAnalysisService>();
        var rootCommand = commandBuilder.Build();

        var parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                ConsoleUx.Error(error.Message);
            }

            return 2;
        }

        try
        {
            AnalysisCommandRequest request = commandBuilder.Map(parseResult);
            return await analysisService.ExecuteAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ConsoleUx.Warning("Operation canceled.");
            return ExitCodes.Canceled;
        }
        catch (ConfigurationException ex)
        {
            ConsoleUx.Error(ex.Message);
            return ExitCodes.ConfigurationFailure;
        }
        catch (DumpLoadException ex)
        {
            ConsoleUx.Error(ex.Message);
            return ExitCodes.DumpLoadFailure;
        }
        catch (AnalysisPipelineException ex)
        {
            ConsoleUx.Error(ex.Message);
            return ExitCodes.AnalysisFailure;
        }
        catch (OutputWriteException ex)
        {
            ConsoleUx.Error(ex.Message);
            return ExitCodes.OutputFailure;
        }
        catch (Exception ex)
        {
            ConsoleUx.Error(ex.Message);
            return ExitCodes.ConfigurationFailure;
        }
    }
}

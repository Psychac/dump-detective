using DumpDetective.Analysis.Dump;
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
        // Set UTF-8 output encoding before any console writes.
        // Without this, the Windows console host defaults to the OEM codepage (CP437/CP1252)
        // and substitutes a BEL character (0x07) for Unicode glyphs it cannot represent —
        // specifically the braille spinner frames, ↳, ◆, ▸, and emoji used by ConsoleUx.
        // This is the direct cause of the audible beep heard during analysis runs.
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding  = System.Text.Encoding.UTF8;

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

using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Hosting;

namespace DumpDetective.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RootCommandBuilder commandBuilder = new();
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
            var cliArguments = commandBuilder.Map(parseResult);
            var service = ServiceRegistration.CreateDumpAnalysisService();
            return await service.ExecuteAsync(cliArguments, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ConsoleUx.Error(ex.Message);
            return 1;
        }
    }
}

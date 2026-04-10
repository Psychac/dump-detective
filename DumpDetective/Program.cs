using DumpDetective.Configuration;
using DumpDetective.Services;
using DumpDetective.Utilities;

namespace DumpDetective
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    PrintUsage();
                    return;
                }

                var config = AnalysisConfiguration.FromCommandLineArgs(args);
                config.PrintConfiguration();

                var service = new DumpAnalysisService(config);
                service.Execute();
            }
            catch (FileNotFoundException ex)
            {
                ConsoleUx.Error(ex.Message);
            }
            catch (ArgumentException ex)
            {
                ConsoleUx.Error(ex.Message);
                PrintUsage();
            }
            catch (Exception ex)
            {
                ConsoleUx.Error($"Error analyzing dump: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DumpDetective <dump-file-path> [output-file-path] [options]");
            Console.WriteLine();
            Console.WriteLine("Memory leak analyzer options:");
            Console.WriteLine("  --high-reference-threshold=<int>      Default: 50");
            Console.WriteLine("  --max-duplicate-string-length=<int>   Default: 500");
            Console.WriteLine("  --min-duplicate-string-count=<int>    Default: 10");
            Console.WriteLine("  --max-reference-addresses=<int>       Default: 1000000");
            Console.WriteLine("General analyzer options:");
            Console.WriteLine("  --reference-chain-top-count=<int>     Default: 5");
            Console.WriteLine("  --event-leak-min-subscribers=<int>    Default: 0");
            Console.WriteLine("  --memory-diagnostics                  Enable stage-by-stage memory snapshots/deltas (Default: Off)");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  DumpDetective C:\\dumps\\myapp.dmp C:\\reports\\analysis.txt --max-reference-addresses=300000 --max-duplicate-string-length=200");
        }
    }
}

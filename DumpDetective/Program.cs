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
            Console.WriteLine("  DumpDetective <dump-file-path> [options]");
            Console.WriteLine("  DumpDetective --config=<config-json-path> [options]");
            Console.WriteLine("  Output file is auto-generated from dump path and report format extension (.html/.md/.txt).");
            Console.WriteLine();
            Console.WriteLine("Memory leak analyzer options:");
            Console.WriteLine("  --high-reference-threshold=<int>      Default: 50");
            Console.WriteLine("  --max-duplicate-string-length=<int>   Default: 500");
            Console.WriteLine("  --min-duplicate-string-count=<int>    Default: 10");
            Console.WriteLine("  --max-reference-addresses=<int>       Default: 1000000");
            Console.WriteLine("General analyzer options:");
            Console.WriteLine("  --config=<path-to-json>               Load all settings from JSON file");
            Console.WriteLine("  --baseline=<baseline-dump-path>       Compare current dump against a baseline dump");
            Console.WriteLine("  --trend=<dump1;dump2;...>             Analyze a series of dumps (ordered oldest to newest); last entry is treated as current");
            Console.WriteLine("  --reference-chain-top-count=<int>     Default: 5");
            Console.WriteLine("  --reference-chain-max-path-search-objects=<int>  Default: 5000");
            Console.WriteLine("  --event-leak-min-subscribers=<int>    Default: 0");
            Console.WriteLine("  --memory-diagnostics                  Enable stage-by-stage memory snapshots/deltas (Default: Off)");
            Console.WriteLine("  --performance-diagnostics             Enable timing breakdowns per dump/phase and report normalization (Default: Off)");
            Console.WriteLine("  --report-format=<text|markdown|html> Report format (Default: html)");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  DumpDetective C:\\dumps\\myapp.dmp --report-format=markdown --max-reference-addresses=300000 --max-duplicate-string-length=200");
            Console.WriteLine("  DumpDetective C:\\dumps\\current.dmp --baseline=C:\\dumps\\baseline.dmp --report-format=html");
            Console.WriteLine("  DumpDetective --trend=C:\\dumps\\week1.dmp;C:\\dumps\\week2.dmp;C:\\dumps\\week3.dmp --report-format=html");
            Console.WriteLine("  DumpDetective --config=C:\\config\\dumpdetective.config.json");
        }
    }
}

using DumpDetective.Configuration;
using DumpDetective.Services;

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
                Console.WriteLine($"Error: {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                PrintUsage();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing dump: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: DumpDetective <dump-file-path> [output-file-path]");
            Console.WriteLine("Example: DumpDetective C:\\dumps\\myapp.dmp C:\\reports\\analysis.txt");
        }
    }
}

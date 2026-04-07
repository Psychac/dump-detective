using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Utilities;

namespace DumpDetective
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: DumpDetective <dump-file-path> [output-file-path]");
                Console.WriteLine("Example: DumpDetective C:\\dumps\\myapp.dmp C:\\reports\\analysis.txt");
                return;
            }

            string dumpPath = args[0];
            string? outputPath = args.Length > 1 ? args[1] : null;

            if (!File.Exists(dumpPath))
            {
                Console.WriteLine($"Error: Dump file not found at '{dumpPath}'");
                return;
            }

            Console.WriteLine($"Analyzing dump: {dumpPath}");
            if (outputPath != null)
            {
                Console.WriteLine($"Output will be written to: {outputPath}");
            }
            Console.WriteLine();

            try
            {
                AnalyzeDump(dumpPath, outputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing dump: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void AnalyzeDump(string dumpPath, string? outputPath)
        {
            StreamWriter? fileWriter = null;
            try
            {
                if (outputPath != null)
                {
                    fileWriter = new StreamWriter(outputPath, false);
                }

                using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
                var writer = new OutputWriter(fileWriter);

                writer.WriteLine($"Dump file: {dumpPath}");
                writer.WriteLine(string.Empty);

                ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
                ClrHeap heap = runtime.Heap;

                if (!heap.CanWalkHeap)
                {
                    writer.WriteLine("Cannot walk the heap!");
                    return;
                }

                // Run all analyses
                new MemoryAnalyzer(writer).Analyze(heap);
                new GCGenerationAnalyzer(writer).Analyze(heap);
                new MemoryLeakAnalyzer(writer).Analyze(heap, runtime);
                new ReferenceChainAnalyzer(writer).AnalyzeTopTypes(heap, topCount: 5);
                new ThreadAnalyzer(writer).Analyze(runtime);
                new EventLeakAnalyzer(writer).Analyze(heap, minSubscribers: 0);

                writer.WriteSeparator();
                writer.WriteLine($"Analysis complete. Total objects analyzed");

                if (outputPath != null)
                {
                    Console.WriteLine($"\nReport written to: {outputPath}");
                }
            }
            finally
            {
                fileWriter?.Dispose();
            }
        }
    }
}

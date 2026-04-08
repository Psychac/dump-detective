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
                var cache = new HeapAnalysisCache();

                writer.WriteLine($"Dump file: {dumpPath}");
                writer.WriteLine(string.Empty);

                // Display CLR version information
                writer.WriteLine("CLR VERSION INFORMATION:");
                writer.WriteSeparator();

                if (dataTarget.ClrVersions.Length == 0)
                {
                    writer.WriteLine("No CLR versions found in dump!");
                    return;
                }

                foreach (ClrInfo clrVersion in dataTarget.ClrVersions)
                {
                    writer.WriteLine($"CLR Version: {clrVersion.Version}");
                    writer.WriteLine($"Module: {clrVersion.ModuleInfo.FileName}");
                    writer.WriteLine($"Module Base: 0x{clrVersion.ModuleInfo.ImageBase:X}");
                    writer.WriteLine(string.Empty);
                }

                // Use the first (primary) CLR version for analysis
                ClrInfo primaryClr = dataTarget.ClrVersions[0];
                writer.WriteLine($"Analyzing using CLR Version: {primaryClr.Version}");
                writer.WriteLine(string.Empty);

                ClrRuntime runtime = primaryClr.CreateRuntime();
                ClrHeap heap = runtime.Heap;

                if (!heap.CanWalkHeap)
                {
                    writer.WriteLine("Cannot walk the heap!");
                    return;
                }

                // Run all analyses (with shared cache for performance)
                // Build cache first (single heap enumeration)
                var typeStats = cache.GetOrBuildTypeStatistics(heap);

                // Core memory analysis
                new MemoryAnalyzer(writer).Analyze(heap, cache);
                new GCGenerationAnalyzer(writer).Analyze(heap, cache);

                // Crash/Hang detection (run early for critical issues)
                new CrashAnalyzer(writer).Analyze(runtime, heap);
                new HangAnalyzer(writer).Analyze(runtime, heap);

                // Memory leak detection
                new MemoryLeakAnalyzer(writer).Analyze(heap, runtime);
                new StaticRootLeakDetector(writer).Analyze(heap, cache);
                new EventHandlerLeakDetector(writer).Analyze(heap, cache);
                new ReferenceChainAnalyzer(writer).AnalyzeTopTypes(heap, cache, topCount: 5);

                // Thread and event analysis
                new ThreadAnalyzer(writer).Analyze(runtime);
                new EventLeakAnalyzer(writer).Analyze(heap, cache, minSubscribers: 0);

                writer.WriteSeparator();
                writer.WriteLine($"Analysis complete");

                if (outputPath != null)
                {
                    Console.WriteLine($"\nReport written to: {outputPath}");
                }
            }
            finally
            {
                fileWriter?.Dispose();
            }

            Console.ReadKey();
        }
    }
}

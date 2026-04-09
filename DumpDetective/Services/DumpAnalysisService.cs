using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Configuration;
using DumpDetective.Utilities;

namespace DumpDetective.Services
{
    internal class DumpAnalysisService
    {
        private readonly AnalysisConfiguration _config;

        public DumpAnalysisService(AnalysisConfiguration config)
        {
            _config = config;
        }

        public void Execute()
        {
            StreamWriter? fileWriter = null;
            try
            {
                if (_config.OutputPath != null)
                {
                    fileWriter = new StreamWriter(_config.OutputPath, false);
                }

                // Load the dump file (ClrMD defaults to Microsoft symbol server if DAC is needed)
                using DataTarget dataTarget = DataTarget.LoadDump(_config.DumpPath);

                var writer = new OutputWriter(fileWriter);
                var cache = new HeapAnalysisCache();

                MemorySnapshot previousSnapshot = MemoryDiagnostic.TakeSnapshot("0. Initial");
                MemoryDiagnostic.PrintSnapshotToConsole(previousSnapshot);

                WriteHeader(writer);

                if (!ValidateClrVersions(dataTarget, writer))
                {
                    return;
                }

                using ClrRuntime runtime = InitializeRuntime(dataTarget, ref previousSnapshot);
                var heap = runtime.Heap;

                if (!ValidateHeap(heap, writer))
                {
                    return;
                }

                BuildTypeStatisticsCache(heap, cache, ref previousSnapshot);

                var context = new AnalysisContext
                {
                    Runtime = runtime,
                    Heap = heap,
                    Cache = cache
                };

                RunAnalysisPipeline(writer, context, previousSnapshot);

                WriteFooter(writer);
            }
            finally
            {
                fileWriter?.Dispose();
            }

            if (_config.WaitForKeyPressOnComplete)
            {
                Console.ReadKey();
            }
        }

        private void WriteHeader(OutputWriter writer)
        {
            writer.WriteLine($"Dump file: {_config.DumpPath}");
            writer.WriteLine(string.Empty);
        }

        private bool ValidateClrVersions(DataTarget dataTarget, OutputWriter writer)
        {
            writer.WriteLine("CLR VERSION INFORMATION:");
            writer.WriteSeparator();

            if (dataTarget.ClrVersions.Length == 0)
            {
                writer.WriteLine("No CLR versions found in dump!");
                return false;
            }

            foreach (ClrInfo clrVersion in dataTarget.ClrVersions)
            {
                writer.WriteLine($"CLR Version: {clrVersion.Version}");
                writer.WriteLine($"Module: {clrVersion.ModuleInfo.FileName}");
                writer.WriteLine($"Module Base: 0x{clrVersion.ModuleInfo.ImageBase:X}");
                writer.WriteLine(string.Empty);
            }

            ClrInfo primaryClr = dataTarget.ClrVersions[0];
            writer.WriteLine($"Analyzing using CLR Version: {primaryClr.Version}");
            writer.WriteLine(string.Empty);

            return true;
        }

        private ClrRuntime InitializeRuntime(DataTarget dataTarget, ref MemorySnapshot previousSnapshot)
        {
            Console.WriteLine("Fetching required dlls from Symbol Servers.");
            ClrInfo primaryClr = dataTarget.ClrVersions[0];
            ClrRuntime runtime = primaryClr.CreateRuntime();

            var snapshot = MemoryDiagnostic.TakeSnapshot("1. After runtime creation");
            MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
            previousSnapshot = snapshot;

            return runtime;
        }

        private bool ValidateHeap(ClrHeap heap, OutputWriter writer)
        {
            if (!heap.CanWalkHeap)
            {
                writer.WriteLine("Cannot walk the heap!");
                writer.WriteLine("The process was likely stopped during a GC or heap is corrupted.");
                return false;
            }
            return true;
        }

        private void BuildTypeStatisticsCache(ClrHeap heap, HeapAnalysisCache cache, ref MemorySnapshot previousSnapshot)
        {
            Console.WriteLine("\n▶ Building type statistics cache...");
            var typeStats = cache.GetOrBuildTypeStatistics(heap);
            Console.WriteLine($"   Cached {typeStats.Count:N0} unique types");

            var snapshot = MemoryDiagnostic.TakeSnapshot("2. After cache build");
            MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
            previousSnapshot = snapshot;
        }

        private void RunAnalysisPipeline(OutputWriter writer, AnalysisContext context, MemorySnapshot initialSnapshot)
        {
            var pipeline = new AnalysisPipeline(initialSnapshot)
                .AddStage("Running core memory analyzers",
                    new MemoryAnalyzerAdapter(writer),
                    new GCGenerationAnalyzerAdapter(writer),
                    new ModuleAnalyzerAdapter(writer))
                .AddStage("Analyzing for crashes and hangs",
                    new CrashAnalyzerAdapter(writer),
                    new HangAnalyzerAdapter(writer))
                .AddStage("Detecting memory leaks",
                    new MemoryLeakAnalyzerAdapter(writer, _config),
                    new CollectionAnalyzerAdapter(writer))
                .AddStage("Analyzing static roots and event handlers",
                    new StaticRootLeakDetectorAdapter(writer),
                    new ReferenceChainAnalyzerAdapter(writer, _config))
                .AddStage("Analyzing threads and events",
                    new ThreadAnalyzerAdapter(writer),
                    new EventLeakAnalyzerAdapter(writer, _config));

            pipeline.Execute(context);

            Console.WriteLine("\n✅ Analysis complete!");
        }

        private void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
            writer.WriteLine("Analysis complete");

            if (_config.OutputPath != null)
            {
                Console.WriteLine($"\nReport written to: {_config.OutputPath}");
            }
        }
    }
}

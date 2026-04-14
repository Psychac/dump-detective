using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Diagnostics;
using System.Text;

namespace DumpDetective.Services
{
    internal sealed class DumpLoader(AnalysisConfiguration config)
    {
        public AnalysisRunResult Load(string dumpPath, int snapshotIndex)
        {
            var dumpStopwatch = Stopwatch.StartNew();
            List<(string Name, TimeSpan Duration)>? dumpTimings = config.EnablePerformanceDiagnostics
                ? new List<(string Name, TimeSpan Duration)>(capacity: 10)
                : null;

            var reportBufferBuilder = new StringBuilder(capacity: 64 * 1024);
            using StringWriter reportWriter = new(reportBufferBuilder);

            ConsoleUx.Info("Loading dump file...");
            var loadDumpStopwatch = Stopwatch.StartNew();
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            loadDumpStopwatch.Stop();
            dumpTimings?.Add(("Load dump", loadDumpStopwatch.Elapsed));
            ConsoleUx.Success("Dump loaded.");

            var writer = new OutputWriter(reportWriter, writeToConsoleWhenNoWriter: false);
            var cache = new HeapAnalysisCache();

            MemorySnapshot? previousSnapshot = null;
            if (config.EnableMemoryDiagnostics)
            {
                previousSnapshot = MemoryDiagnostic.TakeSnapshot("0. Initial");
                MemoryDiagnostic.PrintSnapshotToConsole(previousSnapshot);
            }
            else
            {
                ConsoleUx.Info("Memory diagnostics are disabled (use --memory-diagnostics to enable). Focus mode: stage/analyzer progress.");
            }

            WriteHeader(writer, dumpPath);

            if (!ValidateClrVersions(dataTarget, writer))
            {
                dumpStopwatch.Stop();
                if (config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
                    ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);

                return new AnalysisRunResult(
                    reportBufferBuilder.ToString(),
                    new AnalysisSnapshot(snapshotIndex, dumpPath, [], new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow));
            }

            var runtimeStopwatch = Stopwatch.StartNew();
            using ClrRuntime runtime = InitializeRuntime(dataTarget, ref previousSnapshot);
            runtimeStopwatch.Stop();
            dumpTimings?.Add(("Initialize CLR runtime", runtimeStopwatch.Elapsed));

            var heap = runtime.Heap;

            if (!ValidateHeap(heap, writer))
            {
                dumpStopwatch.Stop();
                if (config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
                    ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);

                return new AnalysisRunResult(
                    reportBufferBuilder.ToString(),
                    new AnalysisSnapshot(snapshotIndex, dumpPath, [], new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow));
            }

            var cacheStopwatch = Stopwatch.StartNew();
            BuildTypeStatisticsCache(heap, cache, ref previousSnapshot);
            cacheStopwatch.Stop();
            dumpTimings?.Add(("Type statistics cache build", cacheStopwatch.Elapsed));

            var context = new AnalysisContext { Runtime = runtime, Heap = heap, Cache = cache };

            var pipelineStopwatch = Stopwatch.StartNew();
            var (findings, domainResults) = RunAnalysisPipeline(writer, context, previousSnapshot);
            pipelineStopwatch.Stop();
            dumpTimings?.Add(("Analyzer pipeline", pipelineStopwatch.Elapsed));

            var normalizedFindings = findings.ToList();
            var normalizeFindingsStopwatch = Stopwatch.StartNew();
            FindingTagger.Normalize(normalizedFindings);
            normalizeFindingsStopwatch.Stop();
            dumpTimings?.Add(("Per-dump finding normalization", normalizeFindingsStopwatch.Elapsed));

            WriteFooter(writer);

            dumpStopwatch.Stop();
            if (config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
                ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);

            return new AnalysisRunResult(
                reportBufferBuilder.ToString(),
                new AnalysisSnapshot(snapshotIndex, dumpPath, normalizedFindings, domainResults, DateTime.UtcNow));
        }

        private static void WriteHeader(OutputWriter writer, string dumpPath)
        {
            writer.WriteLine($"Dump file: {dumpPath}");
            writer.WriteLine(string.Empty);
        }

        private static bool ValidateClrVersions(DataTarget dataTarget, OutputWriter writer)
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

        private ClrRuntime InitializeRuntime(DataTarget dataTarget, ref MemorySnapshot? previousSnapshot)
        {
            ConsoleUx.Info("Initializing CLR runtime (fetching required symbol files)...");
            ClrInfo primaryClr = dataTarget.ClrVersions[0];
            ClrRuntime runtime = primaryClr.CreateRuntime();
            ConsoleUx.Success("CLR runtime initialized.");

            if (config.EnableMemoryDiagnostics && previousSnapshot != null)
            {
                var snapshot = MemoryDiagnostic.TakeSnapshot("1. After runtime creation");
                MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
                previousSnapshot = snapshot;
            }

            return runtime;
        }

        private static bool ValidateHeap(ClrHeap heap, OutputWriter writer)
        {
            if (!heap.CanWalkHeap)
            {
                writer.WriteLine("Cannot walk the heap!");
                writer.WriteLine("The process was likely stopped during a GC or heap is corrupted.");
                return false;
            }
            return true;
        }

        private void BuildTypeStatisticsCache(ClrHeap heap, HeapAnalysisCache cache, ref MemorySnapshot? previousSnapshot)
        {
            ConsoleUx.Info("Building type statistics cache...");
            var typeStats = cache.GetOrBuildTypeStatistics(heap);
            ConsoleUx.Success($"Type statistics cache ready ({typeStats.Count:N0} unique types).");

            if (config.EnableMemoryDiagnostics && previousSnapshot != null)
            {
                var snapshot = MemoryDiagnostic.TakeSnapshot("2. After cache build");
                MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
                previousSnapshot = snapshot;
            }
        }

        private (IReadOnlyList<InsightFinding> Findings, IReadOnlyDictionary<string, AnalyzerDomainResult> DomainResults) RunAnalysisPipeline(
            OutputWriter writer, AnalysisContext context, MemorySnapshot? initialSnapshot)
        {
            ConsoleUx.Header("Analysis Pipeline");
            ConsoleUx.Info("Starting analysis pipeline...");

            var pipeline = new AnalysisPipeline(initialSnapshot, config.EnableMemoryDiagnostics)
                .AddStage("Running core memory analyzers",
                    new FunctionalAnalyzer("Memory Analysis",        ctx => new MemoryAnalyzer(writer).Analyze(ctx.Heap, ctx.Cache)),
                    new FunctionalAnalyzer("GC Generation Analysis", ctx => new GCGenerationAnalyzer(writer).Analyze(ctx.Heap, ctx.Cache)),
                    new FunctionalAnalyzer("Module Analysis",        ctx => new ModuleAnalyzer(writer).Analyze(ctx.Runtime)))
                .AddStage("Analyzing for crashes and hangs",
                    new FunctionalAnalyzer("Crash Analysis", ctx => new CrashAnalyzer(writer).Analyze(ctx.Runtime, ctx.Heap)),
                    new FunctionalAnalyzer("Hang Analysis",  ctx => new HangAnalyzer(writer).Analyze(ctx.Runtime, ctx.Heap)))
                .AddStage("Detecting memory leaks",
                    new FunctionalAnalyzer("Memory Leak Analysis", ctx => new MemoryLeakAnalyzer(writer, config).Analyze(ctx.Heap, ctx.Runtime)),
                    new FunctionalAnalyzer("Collection Analysis",  ctx => new CollectionAnalyzer(writer).Analyze(ctx.Heap)))
                .AddStage("Analyzing static roots and event handlers",
                    new FunctionalAnalyzer("Static Root Leak Detection", ctx => new StaticRootLeakDetector(writer).Analyze(ctx.Heap, ctx.Cache)),
                    new FunctionalAnalyzer("Reference Chain Analysis",   ctx => new ReferenceChainAnalyzer(writer, config).AnalyzeTopTypes(ctx.Heap, ctx.Cache)))
                .AddStage("Performing ClrMD deep analysis",
                    new FunctionalAnalyzer("GC Handle Analysis",                ctx => new GCHandleAnalyzer(writer).Analyze(ctx.Runtime)),
                    new FunctionalAnalyzer("Dependent Handle Analysis",         ctx => new DependentHandleAnalyzer(writer).Analyze(ctx.Runtime)),
                    new FunctionalAnalyzer("LOH Fragmentation Analysis",        ctx => new LohFragmentationAnalyzer(writer).Analyze(ctx.Heap)),
                    new FunctionalAnalyzer("Thread Stack Signature Clustering", ctx => new ThreadStackClusterAnalyzer(writer).Analyze(ctx.Runtime)))
                .AddStage("Analyzing threads and events",
                    new FunctionalAnalyzer("Thread Analysis",     ctx => new ThreadAnalyzer(writer).Analyze(ctx.Runtime)),
                    new FunctionalAnalyzer("Lock Graph Analysis", ctx => new LockGraphAnalyzer(writer).Analyze(ctx.Runtime, ctx.Heap)),
                    new FunctionalAnalyzer("Event Leak Analysis", ctx => new EventLeakAnalyzer(writer, config).Analyze(ctx.Heap)));

            var (findings, domainResults) = pipeline.Execute(context);

            ConsoleUx.Success("Analysis pipeline complete.");
            return (findings, domainResults);
        }

        private static void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
        }
    }

    internal sealed record AnalysisRunResult(string DetailedReport, AnalysisSnapshot Snapshot);
}

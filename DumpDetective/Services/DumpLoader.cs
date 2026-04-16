using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Diagnostics;
using System.Text;

namespace DumpDetective.Services
{
    internal sealed class DumpLoader
    {
        private readonly AnalysisConfiguration config;

        public DumpLoader(AnalysisConfiguration config)
        {
            this.config = config;
        }

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
            var (findings, domainResults) = RunAnalysisPipeline(context, previousSnapshot);
            pipelineStopwatch.Stop();
            dumpTimings?.Add(("Analyzer pipeline", pipelineStopwatch.Elapsed));

            var renderStopwatch = Stopwatch.StartNew();
            RenderAnalyzerSections(writer, domainResults);
            RenderCorrelationInsights(writer, domainResults);
            renderStopwatch.Stop();
            dumpTimings?.Add(("Analyzer report rendering", renderStopwatch.Elapsed));

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
            AnalysisContext context, MemorySnapshot? initialSnapshot)
        {
            ConsoleUx.Header("Analysis Pipeline");
            ConsoleUx.Info("Starting analysis pipeline...");

            var pipeline = new AnalysisPipeline(initialSnapshot, config.EnableMemoryDiagnostics, config.ForceGCBetweenStages)
                .AddStage("Running core memory analyzers",
                    new MemoryAnalyzer(),
                    new GCGenerationAnalyzer(),
                    new ModuleAnalyzer())
                .AddStage("Analyzing for crashes and hangs",
                    new CrashAnalyzer(),
                    new HangAnalyzer())
                .AddStage("Detecting memory leaks",
                    new MemoryLeakAnalyzer(config),
                    new CollectionAnalyzer())
                .AddStage("Analyzing static roots and event handlers",
                    new StaticRootLeakDetector(),
                    new ReferenceChainAnalyzer(config))
                .AddStage("Performing ClrMD deep analysis",
                    new GCHandleAnalyzer(),
                    new DependentHandleAnalyzer(),
                    new LohFragmentationAnalyzer(),
                    new ThreadStackClusterAnalyzer())
                .AddStage("Analyzing threads and events",
                    new ThreadAnalyzer(),
                    new LockGraphAnalyzer(),
                    new EventLeakAnalyzer(config));

            var (findings, domainResults) = pipeline.Execute(context);

            ConsoleUx.Success("Analysis pipeline complete.");
            return (findings, domainResults);
        }

        private static void RenderAnalyzerSections(OutputWriter writer, IReadOnlyDictionary<string, AnalyzerDomainResult> domainResults)
        {
            var renderer = new AnalyzerReportRenderer([
                new MemoryPrinter(),
                new GCGenerationPrinter(),
                new ModulePrinter(),
                new CrashPrinter(),
                new HangPrinter(),
                new MemoryLeakPrinter(),
                new CollectionPrinter(),
                new StaticRootPrinter(),
                new ReferenceChainPrinter(),
                new GCHandlePrinter(),
                new DependentHandlePrinter(),
                new LohFragmentationPrinter(),
                new ThreadStackClusterPrinter(),
                new ThreadPrinter(),
                new LockGraphPrinter(),
                new EventLeakPrinter()
            ]);

            renderer.Render(domainResults, writer);
        }

        private static void RenderCorrelationInsights(OutputWriter writer, IReadOnlyDictionary<string, AnalyzerDomainResult> domainResults)
        {
            writer.WriteLine("\n\n💡 OPTIMIZATION TIPS:");
            writer.WriteSeparator();

            var insights = new List<string>(capacity: 8);

            if (domainResults.TryGetValue("Thread Analysis", out var threadResult)
                && threadResult is ThreadDomainResult thread
                && domainResults.TryGetValue("Lock Graph Analysis", out var lockResult)
                && lockResult is LockGraphDomainResult lockGraph)
            {
                if (thread.BlockedThreadCount > 0 && lockGraph.ContestedLockCount > 0)
                {
                    string topLockType = lockGraph.TopContestedLockTypes?.Count > 0
                        ? lockGraph.TopContestedLockTypes[0].Name
                        : "unknown lock type";
                    insights.Add($"[THREAD+LOCK] {thread.BlockedThreadCount:N0} blocked-pattern thread(s) overlap with {lockGraph.ContestedLockCount:N0} contested lock(s). Top hotspot: {FormatHelper.TruncateString(topLockType, 60)}.");
                }

                if (thread.WaitPatternBreakdown.TryGetValue("MonitorContention", out int monitorContention) && monitorContention > 0)
                {
                    insights.Add($"[LOCK CONTENTION SIGNAL] Monitor-contention signatures observed on {monitorContention:N0} thread(s); correlate with lock owners/hotspots first.");
                }
            }

            if (domainResults.TryGetValue("Memory Analysis", out var memoryResult)
                && memoryResult is MemoryDomainResult memory
                && domainResults.TryGetValue("LOH Fragmentation Analysis", out var lohResult)
                && lohResult is LohFragmentationDomainResult loh)
            {
                if (memory.LohPercent >= 35 || loh.FragmentationPercent >= 20)
                {
                    insights.Add($"[LOH PRESSURE] LOH share {memory.LohPercent:F1}% with fragmentation {loh.FragmentationPercent:F1}%. Prioritize large object churn and lifetime reduction.");
                }

                if (domainResults.TryGetValue("GC Handle Analysis", out var gcHandleResult)
                    && gcHandleResult is GCHandleDomainResult gcHandles
                    && gcHandles.PinnedHandleTargets > 0)
                {
                    insights.Add($"[PINNING+LOH] {gcHandles.PinnedHandleTargets:N0} pinned-handle target(s) detected. Combined with LOH stress this may reduce compaction effectiveness.");
                }
            }

            if (domainResults.TryGetValue("Static Root Leak Detection", out var staticRootResult)
                && staticRootResult is StaticRootDomainResult staticRoots
                && domainResults.TryGetValue("Event Leak Analysis", out var eventResult)
                && eventResult is EventLeakDomainResult events)
            {
                if (staticRoots.RootCount > 0 && events.TotalEventLeakInstances > 0)
                {
                    insights.Add($"[STATIC ROOT+EVENT RETENTION] {staticRoots.RootCount:N0} high-impact static root(s) and {events.TotalEventLeakInstances:N0} event-leak group(s) detected. Review publisher lifetime and unsubscribe discipline.");
                }
            }

            if (domainResults.TryGetValue("Reference Chain Analysis", out var referenceResult)
                && referenceResult is ReferenceChainDomainResult referenceChains
                && domainResults.TryGetValue("Dependent Handle Analysis", out var dependentResult)
                && dependentResult is DependentHandleDomainResult dependent)
            {
                if (referenceChains.RetainedPercent >= 60 || dependent.UnresolvedPercent >= 30)
                {
                    insights.Add($"[RETENTION GRAPH] Retained sample coverage {referenceChains.RetainedPercent:F1}% and dependent-handle unresolved ratio {dependent.UnresolvedPercent:F1}%. Validate hidden ownership edges and conditional retention paths.");
                }
            }

            if (insights.Count == 0)
            {
                writer.WriteLine("No strong cross-analyzer correlation signals exceeded report thresholds in this dump.");
                return;
            }

            for (int i = 0; i < insights.Count; i++)
            {
                writer.WriteLine($"{i + 1}. {insights[i]}");
            }
        }

        private static void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
        }
    }

    internal sealed record AnalysisRunResult(string DetailedReport, AnalysisSnapshot Snapshot);
}

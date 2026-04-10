using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Configuration;
using DumpDetective.Utilities;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
            var runStopwatch = Stopwatch.StartNew();
            ConsoleUx.Header("DumpDetective Analysis");

            StringBuilder? reportBufferBuilder = _config.OutputPath != null ? new StringBuilder(capacity: 64 * 1024) : null;
            using StringWriter? reportWriter = reportBufferBuilder != null ? new StringWriter(reportBufferBuilder) : null;

            ConsoleUx.Info("Loading dump file...");
            // Load the dump file (ClrMD defaults to Microsoft symbol server if DAC is needed)
            using DataTarget dataTarget = DataTarget.LoadDump(_config.DumpPath);
            ConsoleUx.Success("Dump loaded.");

            var writer = new OutputWriter(reportWriter, writeToConsoleWhenNoWriter: _config.OutputPath == null);
            var cache = new HeapAnalysisCache();

            MemorySnapshot? previousSnapshot = null;
            if (_config.EnableMemoryDiagnostics)
            {
                previousSnapshot = MemoryDiagnostic.TakeSnapshot("0. Initial");
                MemoryDiagnostic.PrintSnapshotToConsole(previousSnapshot);
            }
            else
            {
                ConsoleUx.Info("Memory diagnostics are disabled (use --memory-diagnostics to enable). Focus mode: stage/analyzer progress.");
            }

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

            if (_config.OutputPath != null && reportBufferBuilder != null)
            {
                string detailedReport = reportBufferBuilder.ToString();
                var insights = BuildReportInsights(runStopwatch.Elapsed, detailedReport);
                string formattedReport = ReportFormatter.Format(_config.ReportFormat, detailedReport, insights, _config.DumpPath);
                File.WriteAllText(_config.OutputPath, formattedReport, Encoding.UTF8);
                ConsoleUx.Success($"Report written to: {_config.OutputPath}");
            }

            runStopwatch.Stop();
            ConsoleUx.Success($"Total analysis time: {runStopwatch.Elapsed.TotalSeconds:F1}s");

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

        private ClrRuntime InitializeRuntime(DataTarget dataTarget, ref MemorySnapshot? previousSnapshot)
        {
            ConsoleUx.Info("Initializing CLR runtime (fetching required symbol files)...");
            ClrInfo primaryClr = dataTarget.ClrVersions[0];
            ClrRuntime runtime = primaryClr.CreateRuntime();
            ConsoleUx.Success("CLR runtime initialized.");

            if (_config.EnableMemoryDiagnostics && previousSnapshot != null)
            {
                var snapshot = MemoryDiagnostic.TakeSnapshot("1. After runtime creation");
                MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
                previousSnapshot = snapshot;
            }

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

        private void BuildTypeStatisticsCache(ClrHeap heap, HeapAnalysisCache cache, ref MemorySnapshot? previousSnapshot)
        {
            ConsoleUx.Info("Building type statistics cache...");
            var typeStats = cache.GetOrBuildTypeStatistics(heap);
            ConsoleUx.Success($"Type statistics cache ready ({typeStats.Count:N0} unique types).");

            if (_config.EnableMemoryDiagnostics && previousSnapshot != null)
            {
                var snapshot = MemoryDiagnostic.TakeSnapshot("2. After cache build");
                MemoryDiagnostic.PrintDeltaToConsole(previousSnapshot, snapshot);
                previousSnapshot = snapshot;
            }
        }

        private void RunAnalysisPipeline(OutputWriter writer, AnalysisContext context, MemorySnapshot? initialSnapshot)
        {
            ConsoleUx.Header("Analysis Pipeline");
            ConsoleUx.Info("Starting analysis pipeline...");

            var pipeline = new AnalysisPipeline(initialSnapshot, _config.EnableMemoryDiagnostics)
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
                .AddStage("Performing ClrMD deep analysis",
                    new GCHandleAnalyzerAdapter(writer),
                    new DependentHandleAnalyzerAdapter(writer),
                    new LohFragmentationAnalyzerAdapter(writer),
                    new ThreadStackClusterAnalyzerAdapter(writer))
                .AddStage("Analyzing threads and events",
                    new ThreadAnalyzerAdapter(writer),
                    new EventLeakAnalyzerAdapter(writer, _config));

            pipeline.Execute(context);

            ConsoleUx.Success("Analysis pipeline complete.");
        }

        private void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
            writer.WriteLine("Analysis complete");
        }

        private List<string> BuildReportInsights(TimeSpan elapsed, string detailedReport)
        {
            var insights = new List<string>(capacity: 8);

            double? lohFragPercent = TryGetDouble(detailedReport, @"LOH Free Size:\s+.+\((?<value>\d+(?:\.\d+)?)% fragmentation\)");
            if (lohFragPercent.HasValue)
            {
                if (lohFragPercent.Value >= 30)
                    insights.Add($"[CRITICAL] LOH fragmentation is high at {lohFragPercent.Value:F1}% - large-object allocations may fail or trigger heavy GC compaction.");
                else if (lohFragPercent.Value >= 15)
                    insights.Add($"[WARNING] LOH fragmentation is {lohFragPercent.Value:F1}% - monitor allocation pressure for large objects.");
                else
                    insights.Add($"[OK] LOH fragmentation is {lohFragPercent.Value:F1}%.");
            }

            int? finalizerQueueCount = TryGetInt(detailedReport, @"Objects waiting for finalization:\s+(?<value>[\d,]+)");
            if (finalizerQueueCount.HasValue)
            {
                if (finalizerQueueCount.Value >= 1000)
                    insights.Add($"[CRITICAL] Finalizer queue backlog is {finalizerQueueCount.Value:N0} objects - potential finalizer bottleneck.");
                else if (finalizerQueueCount.Value > 0)
                    insights.Add($"[WARNING] {finalizerQueueCount.Value:N0} object(s) are waiting in the finalizer queue.");
                else
                    insights.Add("[OK] Finalizer queue is empty.");
            }

            int? eventLeakInstances = TryGetInt(detailedReport, @"Found\s+(?<value>[\d,]+)\s+event instance\(s\)\s+across");
            if (eventLeakInstances.HasValue)
            {
                if (eventLeakInstances.Value >= 25)
                    insights.Add($"[CRITICAL] {eventLeakInstances.Value:N0} event leak instance(s) detected.");
                else if (eventLeakInstances.Value > 0)
                    insights.Add($"[WARNING] {eventLeakInstances.Value:N0} potential event leak instance(s) detected.");
            }
            else if (detailedReport.Contains("No event leaks detected!", StringComparison.OrdinalIgnoreCase))
            {
                insights.Add("[OK] Event leak analyzer did not find suspicious publisher/subscriber patterns.");
            }

            int? staticRootCount = TryGetInt(detailedReport, @"Found\s+(?<value>[\d,]+)\s+static root\(s\) with significant memory impact");
            if (staticRootCount.HasValue)
            {
                if (staticRootCount.Value >= 10)
                    insights.Add($"[CRITICAL] {staticRootCount.Value:N0} static roots are retaining significant memory.");
                else
                    insights.Add($"[WARNING] {staticRootCount.Value:N0} static root leak candidate(s) identified.");
            }

            if (detailedReport.Contains("No objects with more than", StringComparison.OrdinalIgnoreCase))
            {
                insights.Add($"[OK] No heavily-referenced objects exceeded the {_config.HighReferenceThreshold:N0} incoming-reference threshold.");
            }

            insights.Add($"[INFO] Analysis completed in {elapsed.TotalSeconds:F1}s.");

            if (insights.Count == 1)
            {
                insights.Insert(0, "[INFO] No high-confidence issue signals were extracted from analyzer summaries. Review detailed sections for context.");
            }

            return insights;
        }

        private static int? TryGetInt(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return null;

            string value = match.Groups["value"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
            return int.TryParse(value, out int parsed) ? parsed : null;
        }

        private static double? TryGetDouble(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return null;

            return double.TryParse(match.Groups["value"].Value, out double parsed) ? parsed : null;
        }
    }
}

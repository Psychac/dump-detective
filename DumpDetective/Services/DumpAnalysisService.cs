using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analyzers;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Diagnostics;
using System.Text;

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

            var findings = RunAnalysisPipeline(writer, context, previousSnapshot);

            WriteFooter(writer);

            if (_config.OutputPath != null && reportBufferBuilder != null)
            {
                string detailedReport = reportBufferBuilder.ToString();
                var normalizedFindings = findings.ToList();
                FindingTagger.Normalize(normalizedFindings);
                var insights = BuildReportInsights(runStopwatch.Elapsed, normalizedFindings);
                string formattedReport = ReportFormatter.Format(_config.ReportFormat, detailedReport, insights, _config.DumpPath, normalizedFindings);
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

        private IReadOnlyList<InsightFinding> RunAnalysisPipeline(OutputWriter writer, AnalysisContext context, MemorySnapshot? initialSnapshot)
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

            var findings = pipeline.Execute(context);

            ConsoleUx.Success("Analysis pipeline complete.");
            return findings;
        }

        private void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
            writer.WriteLine("Analysis complete");
        }

        private static List<string> BuildReportInsights(TimeSpan elapsed, IReadOnlyList<InsightFinding> findings)
        {
            var insights = new List<string>(capacity: 8);
            int criticalCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warningCount = findings.Count(f => f.Severity == FindingSeverity.Warning);

            if (criticalCount > 0)
            {
                insights.Add($"[CRITICAL] {criticalCount:N0} critical finding(s) detected. Prioritize these first.");
            }

            if (warningCount > 0)
            {
                insights.Add($"[WARNING] {warningCount:N0} warning finding(s) detected. Address these after critical issues.");
            }

            foreach (var finding in findings
                .Where(f => f.Severity != FindingSeverity.Info)
                .OrderByDescending(f => f.Severity)
                .Take(3))
            {
                insights.Add($"[{finding.Severity}] {finding.Title} — {finding.Evidence}");
            }
            insights.Add($"[INFO] Analysis completed in {elapsed.TotalSeconds:F1}s.");

            if (findings.Count == 0)
            {
                insights.Insert(0, "[INFO] No structured findings were emitted by analyzers.");
            }

            return insights;
        }
    }
}

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
        private readonly TrendAnalyzer _trendAnalyzer = new();

        public DumpAnalysisService(AnalysisConfiguration config)
        {
            _config = config;
        }

        public void Execute()
        {
            var runStopwatch = Stopwatch.StartNew();
            ConsoleUx.Header("DumpDetective Analysis");
            List<(string Name, TimeSpan Duration)>? runTimings = _config.EnablePerformanceDiagnostics
                ? new List<(string Name, TimeSpan Duration)>(capacity: 12)
                : null;

            List<string> dumpSequence = BuildDumpSequence();
            var runs = new List<AnalysisRunResult>(dumpSequence.Count);

            for (int i = 0; i < dumpSequence.Count; i++)
            {
                string dumpPath = dumpSequence[i];
                ConsoleUx.Info($"Analyzing dump [{i + 1}/{dumpSequence.Count}]: {Path.GetFileName(dumpPath)}");
                var dumpStopwatch = Stopwatch.StartNew();
                runs.Add(AnalyzeDump(dumpPath, i));
                dumpStopwatch.Stop();
                runTimings?.Add(($"Dump {i + 1}/{dumpSequence.Count} ({Path.GetFileName(dumpPath)})", dumpStopwatch.Elapsed));

                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            }

            AnalysisRunResult currentRun = runs[^1];
            var reportFindings = currentRun.Snapshot.Findings.ToList();
            var reportMergeStopwatch = Stopwatch.StartNew();
            string detailedReport = BuildCombinedDetailedReport(runs);
            reportMergeStopwatch.Stop();
            runTimings?.Add(("Detailed report merge", reportMergeStopwatch.Elapsed));

            var snapshots = runs.Select(r => r.Snapshot).ToList();
            runs.Clear();

            var additionalInsights = new List<string>();

            if (snapshots.Count > 1)
            {
                var trendCompareStopwatch = Stopwatch.StartNew();
                IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps = _trendAnalyzer.CompareSeries(snapshots);
                IReadOnlyList<AnalyzerTrendResult> overall = _trendAnalyzer.CompareAll(snapshots[0], snapshots[^1]);
                FindingLifecycleResult lifecycle = _trendAnalyzer.CompareFindings(snapshots[0], snapshots[^1]);
                IReadOnlyList<AnalyzerMetricTimeline> timeline = _trendAnalyzer.ExtractTimeline(snapshots);
                trendCompareStopwatch.Stop();
                runTimings?.Add(("Trend comparison compute", trendCompareStopwatch.Elapsed));

                var trendComposeStopwatch = Stopwatch.StartNew();
                detailedReport = BuildTrendComparisonSection(steps, overall, lifecycle, timeline, snapshots) + Environment.NewLine + detailedReport;
                reportFindings.AddRange(BuildTrendFindings(overall, lifecycle));
                additionalInsights.AddRange(BuildTrendInsights(overall, lifecycle, snapshots.Count));
                trendComposeStopwatch.Stop();
                runTimings?.Add(("Trend report/findings compose", trendComposeStopwatch.Elapsed));
            }

            var normalizeReportFindingsStopwatch = Stopwatch.StartNew();
            FindingTagger.Normalize(reportFindings);
            normalizeReportFindingsStopwatch.Stop();
            runTimings?.Add(("Final finding normalization", normalizeReportFindingsStopwatch.Elapsed));

            if (_config.OutputPath != null)
            {
                var reportInsightsStopwatch = Stopwatch.StartNew();
                var insights = BuildReportInsights(runStopwatch.Elapsed, reportFindings, additionalInsights);
                reportInsightsStopwatch.Stop();
                runTimings?.Add(("Insights generation", reportInsightsStopwatch.Elapsed));

                var reportFormatStopwatch = Stopwatch.StartNew();
                string formattedReport = ReportFormatter.Format(_config.ReportFormat, detailedReport, insights, _config.DumpPath, reportFindings);
                reportFormatStopwatch.Stop();
                runTimings?.Add(("Report formatting", reportFormatStopwatch.Elapsed));

                var reportWriteStopwatch = Stopwatch.StartNew();
                File.WriteAllText(_config.OutputPath, formattedReport, Encoding.UTF8);
                reportWriteStopwatch.Stop();
                runTimings?.Add(("Report file write", reportWriteStopwatch.Elapsed));

                ConsoleUx.Success($"Report written to: {_config.OutputPath}");
            }

            runStopwatch.Stop();

            if (_config.EnablePerformanceDiagnostics && runTimings is { Count: > 0 })
            {
                ConsoleUx.PerformanceBreakdown("Run timing breakdown", runTimings, runStopwatch.Elapsed);
            }

            ConsoleUx.Success($"Total analysis time: {runStopwatch.Elapsed.TotalSeconds:F1}s");

            if (_config.WaitForKeyPressOnComplete)
            {
                Console.ReadKey();
            }
        }

        private List<string> BuildDumpSequence()
        {
            var sequence = new List<string>();

            if (_config.TrendDumpPaths is { Count: > 0 })
            {
                foreach (string dump in _config.TrendDumpPaths)
                {
                    if (!string.Equals(dump, _config.DumpPath, StringComparison.OrdinalIgnoreCase))
                    {
                        sequence.Add(dump);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(_config.BaselineDumpPath))
            {
                sequence.Add(_config.BaselineDumpPath);
            }

            sequence.Add(_config.DumpPath);

            return sequence
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private AnalysisRunResult AnalyzeDump(string dumpPath, int snapshotIndex)
        {
            var dumpStopwatch = Stopwatch.StartNew();
            List<(string Name, TimeSpan Duration)>? dumpTimings = _config.EnablePerformanceDiagnostics
                ? new List<(string Name, TimeSpan Duration)>(capacity: 10)
                : null;

            var reportBufferBuilder = new StringBuilder(capacity: 64 * 1024);
            using StringWriter reportWriter = new(reportBufferBuilder);

            ConsoleUx.Info("Loading dump file...");
            var loadDumpStopwatch = Stopwatch.StartNew();
            // Load the dump file (ClrMD defaults to Microsoft symbol server if DAC is needed)
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            loadDumpStopwatch.Stop();
            dumpTimings?.Add(("Load dump", loadDumpStopwatch.Elapsed));
            ConsoleUx.Success("Dump loaded.");

            var writer = new OutputWriter(reportWriter, writeToConsoleWhenNoWriter: false);
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

            WriteHeader(writer, dumpPath);

            if (!ValidateClrVersions(dataTarget, writer))
            {
                dumpStopwatch.Stop();
                if (_config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
                {
                    ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);
                }

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
                if (_config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
                {
                    ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);
                }

                return new AnalysisRunResult(
                    reportBufferBuilder.ToString(),
                    new AnalysisSnapshot(snapshotIndex, dumpPath, [], new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow));
            }

            var cacheStopwatch = Stopwatch.StartNew();
            BuildTypeStatisticsCache(heap, cache, ref previousSnapshot);
            cacheStopwatch.Stop();
            dumpTimings?.Add(("Type statistics cache build", cacheStopwatch.Elapsed));

            var context = new AnalysisContext
            {
                Runtime = runtime,
                Heap = heap,
                Cache = cache
            };

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
            if (_config.EnablePerformanceDiagnostics && dumpTimings is { Count: > 0 })
            {
                ConsoleUx.PerformanceBreakdown($"Dump timing breakdown: {Path.GetFileName(dumpPath)}", dumpTimings, dumpStopwatch.Elapsed);
            }

            return new AnalysisRunResult(
                reportBufferBuilder.ToString(),
                new AnalysisSnapshot(snapshotIndex, dumpPath, normalizedFindings, domainResults, DateTime.UtcNow));
        }

        private void WriteHeader(OutputWriter writer, string dumpPath)
        {
            writer.WriteLine($"Dump file: {dumpPath}");
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

        private (IReadOnlyList<InsightFinding> Findings, IReadOnlyDictionary<string, AnalyzerDomainResult> DomainResults) RunAnalysisPipeline(OutputWriter writer, AnalysisContext context, MemorySnapshot? initialSnapshot)
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

            var (findings, domainResults) = pipeline.Execute(context);

            ConsoleUx.Success("Analysis pipeline complete.");
            return (findings, domainResults);
        }

        private void WriteFooter(OutputWriter writer)
        {
            writer.WriteSeparator();
            writer.WriteLine("Analysis complete");
        }

        private static List<string> BuildReportInsights(TimeSpan elapsed, IReadOnlyList<InsightFinding> findings, IReadOnlyList<string>? additionalInsights = null)
        {
            var insights = new List<string>(capacity: 8);

            if (additionalInsights != null && additionalInsights.Count > 0)
            {
                insights.AddRange(additionalInsights);
            }

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

        private static List<string> BuildTrendInsights(
            IReadOnlyList<AnalyzerTrendResult> overall,
            FindingLifecycleResult lifecycle,
            int dumpCount)
        {
            int regressionCount = overall.Sum(r => r.Regressions.Count);
            return
            [
                $"[INFO] Trend comparison across {dumpCount} dumps: +{lifecycle.NewFindings.Count} new, {lifecycle.PersistentFindings.Count} persistent, -{lifecycle.ResolvedFindings.Count} resolved findings.",
                $"[INFO] Metric regressions detected: {regressionCount} across {overall.Count} analyzers."
            ];
        }

        private static List<InsightFinding> BuildTrendFindings(
            IReadOnlyList<AnalyzerTrendResult> overall,
            FindingLifecycleResult lifecycle)
        {
            int topRegressions = overall.Sum(r => r.Regressions.Count);
            FindingSeverity severity = topRegressions >= 5 ? FindingSeverity.Warning : FindingSeverity.Info;

            return
            [
                new(
                    Analyzer: "TrendAnalyzer",
                    Category: "Comparison",
                    Severity: lifecycle.NewFindings.Count > lifecycle.ResolvedFindings.Count ? FindingSeverity.Warning : FindingSeverity.Info,
                    Title: "Trend finding lifecycle summary",
                    Evidence: $"New {lifecycle.NewFindings.Count}, Persistent {lifecycle.PersistentFindings.Count}, Resolved {lifecycle.ResolvedFindings.Count}",
                    Recommendation: "Focus first on new and persistent high-severity findings.",
                    Tags: ["trend", "lifecycle", "comparison"],
                    MetricValue: lifecycle.NewFindings.Count - lifecycle.ResolvedFindings.Count,
                    MetricUnit: "net-findings"),
                new(
                    Analyzer: "TrendAnalyzer",
                    Category: "Comparison",
                    Severity: severity,
                    Title: "Trend metric regression summary",
                    Evidence: $"{topRegressions} metric regression(s) across {overall.Count} analyzer(s) compared.",
                    Recommendation: topRegressions > 0
                        ? "Review per-analyzer metric regressions in the trend comparison section."
                        : "No metric regressions detected across compared analyzers.",
                    Tags: ["trend", "metrics", "comparison"])
            ];
        }

        private static string BuildTrendComparisonSection(
            IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps,
            IReadOnlyList<AnalyzerTrendResult> overall,
            FindingLifecycleResult lifecycle,
            IReadOnlyList<AnalyzerMetricTimeline> timeline,
            IReadOnlyList<AnalysisSnapshot> snapshots)
        {
            var builder = new StringBuilder();
            builder.AppendLine("TREND COMPARISON:");
            builder.AppendLine(StringConstants.Separator80);
            builder.AppendLine($"Dumps analyzed: {snapshots.Count}");
            builder.AppendLine($"New findings: {lifecycle.NewFindings.Count}");
            builder.AppendLine($"Persistent findings: {lifecycle.PersistentFindings.Count}");
            builder.AppendLine($"Resolved findings: {lifecycle.ResolvedFindings.Count}");

            int totalRegressions = overall.Sum(r => r.Regressions.Count);
            int totalImprovements = overall.Sum(r => r.Improvements.Count);
            builder.AppendLine($"Metric regressions: {totalRegressions}");
            builder.AppendLine($"Metric improvements: {totalImprovements}");

            if (timeline.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"PER-ANALYZER METRIC TIMELINE ({snapshots.Count} dumps):");

                // Order: analyzers with regressions first
                var regressionsByAnalyzer = overall.ToDictionary(r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);
                var orderedTimeline = timeline.OrderByDescending(t => regressionsByAnalyzer.GetValueOrDefault(t.AnalyzerName));

                foreach (var analyzerTimeline in orderedTimeline)
                {
                    builder.AppendLine($"  [{analyzerTimeline.AnalyzerName}]");

                    foreach (var point in analyzerTimeline.Points)
                    {
                        // Skip points where no snapshot had a value
                        var validValues = point.Values.Where(v => !double.IsNaN(v)).ToList();
                        if (validValues.Count == 0) continue;

                        // Format each snapshot value
                        string valuesLine = string.Join(" → ", point.Values.Select(v => FormatHelper.FormatMetricValue(v, point.Unit)));

                        // Compute first→last overall delta
                        double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
                        double lastVal = point.Values.Last(v => !double.IsNaN(v));
                        double delta = lastVal - firstVal;
                        double? deltaPercent = Math.Abs(firstVal) > double.Epsilon ? delta * 100.0 / firstVal : null;

                        string deltaStr = FormatHelper.FormatDeltaValue(delta, point.Unit);
                        string pctStr = deltaPercent.HasValue ? $", {(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%" : string.Empty;

                        string icon = (point.Direction, delta > 0) switch
                        {
                            (MetricTrendDirection.HigherIsWorse, true)  => "⚠️ ",
                            (MetricTrendDirection.HigherIsWorse, false) when delta < 0 => "✅ ",
                            (MetricTrendDirection.LowerIsWorse, false) when delta < 0  => "⚠️ ",
                            (MetricTrendDirection.LowerIsWorse, true)   => "✅ ",
                            _ => "ℹ️ "
                        };

                        string deltaLabel = delta == 0 ? "no change" : $"Δ {deltaStr}{pctStr}";
                        builder.AppendLine($"    {icon} {point.Key}: {valuesLine}   ({deltaLabel})");
                    }
                }
            }

            if (lifecycle.NewFindings.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("New findings:");
                foreach (var f in lifecycle.NewFindings.OrderByDescending(f => f.Severity).Take(5))
                    builder.AppendLine($"  - [{f.Severity}] {f.Analyzer}: {f.Title}");
            }

            if (lifecycle.ResolvedFindings.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Resolved findings:");
                foreach (var f in lifecycle.ResolvedFindings.Take(5))
                    builder.AppendLine($"  - [{f.Severity}] {f.Analyzer}: {f.Title}");
            }

            builder.AppendLine();
            return builder.ToString();
        }

        private static string BuildCombinedDetailedReport(IReadOnlyList<AnalysisRunResult> runs)
        {
            if (runs.Count == 1)
            {
                return runs[0].DetailedReport;
            }

            var builder = new StringBuilder(capacity: runs.Sum(r => r.DetailedReport.Length) + 2048);
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                builder.AppendLine($"ANALYSIS SNAPSHOT {i + 1}/{runs.Count}: {run.Snapshot.DumpPath}");
                builder.AppendLine(StringConstants.Separator80);
                builder.AppendLine(run.DetailedReport);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private sealed record AnalysisRunResult(string DetailedReport, AnalysisSnapshot Snapshot);
    }
}

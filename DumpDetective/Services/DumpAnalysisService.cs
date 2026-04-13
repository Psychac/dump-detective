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
            var loader = new DumpLoader(_config);

            for (int i = 0; i < dumpSequence.Count; i++)
            {
                string dumpPath = dumpSequence[i];
                ConsoleUx.Info($"Analyzing dump [{i + 1}/{dumpSequence.Count}]: {Path.GetFileName(dumpPath)}");
                var dumpStopwatch = Stopwatch.StartNew();
                runs.Add(loader.Load(dumpPath, i));
                dumpStopwatch.Stop();
                runTimings?.Add(($"Dump {i + 1}/{dumpSequence.Count} ({Path.GetFileName(dumpPath)})", dumpStopwatch.Elapsed));

                //if (_config.ForceGCBetweenStages)
                    ForceFullCollection();
            }

            AnalysisRunResult currentRun = runs[^1];
            var reportFindings = currentRun.Snapshot.Findings.ToList();
            var reportMergeStopwatch = Stopwatch.StartNew();
            string detailedReport = ReportBuilder.BuildCombinedDetailedReport(runs);
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
                detailedReport = ReportBuilder.BuildTrendComparisonSection(steps, overall, lifecycle, timeline, snapshots) + Environment.NewLine + detailedReport;
                reportFindings.AddRange(ReportBuilder.BuildTrendFindings(overall, lifecycle));
                additionalInsights.AddRange(ReportBuilder.BuildTrendInsights(overall, lifecycle, snapshots.Count));
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
                var insights = ReportBuilder.BuildReportInsights(runStopwatch.Elapsed, reportFindings, additionalInsights);
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

        private static void ForceFullCollection()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        }

        private List<string> BuildDumpSequence()
        {
            var sequence = new List<string>();

            if (_config.TrendDumpPaths is { Count: > 0 })
            {
                foreach (string dump in _config.TrendDumpPaths)
                {
                    if (!string.Equals(dump, _config.DumpPath, StringComparison.OrdinalIgnoreCase))
                        sequence.Add(dump);
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
    }
}

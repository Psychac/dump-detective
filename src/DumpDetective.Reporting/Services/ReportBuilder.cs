using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;
using DumpDetective.Reporting.Models;
using System.Text;

namespace DumpDetective.Reporting.Services
{
    internal static class ReportBuilder
    {
        public static ComposedReport ComposeCanonicalReport(string dumpPath, IReadOnlyList<AnalyzerRunResult> runs, TimeSpan elapsed)
        {
            return ComposeCanonicalReport(dumpPath, runs, elapsed, []);
        }

        public static ComposedReport ComposeCanonicalReport(
            string dumpPath,
            IReadOnlyList<AnalyzerRunResult> runs,
            TimeSpan elapsed,
            IReadOnlyList<IAnalyzerReporter> reporters)
        {
            List<ReportSection> sections = [];
            int evidenceBeforeMerge = 0;
            IReadOnlyList<DetailedAnalyzerSection> detailedAnalyzerSections = BuildDetailedAnalyzerSections(runs, reporters);

            foreach (AnalyzerRunResult run in runs)
            {
                if (run.Result?.Findings is { Count: > 0 })
                {
                    foreach (InsightFinding finding in run.Result.Findings)
                    {
                        evidenceBeforeMerge += 2;
                        sections.Add(new ReportSection(
                            SectionKey: finding.EffectiveFingerprint,
                            Title: finding.Title,
                            Category: finding.Category,
                            Severity: finding.Severity,
                            NarrativeSummary: finding.Evidence,
                            EvidenceRows:
                            [
                                new ReportEvidenceRow("Analyzer", run.AnalyzerName),
                                new ReportEvidenceRow("Evidence", finding.Evidence)
                            ],
                            RemediationHints: [finding.Recommendation],
                            Fingerprints: [finding.EffectiveFingerprint]));
                    }
                }

                if (run.Status == AnalyzerExecutionStatus.Failed)
                {
                    evidenceBeforeMerge += 2;
                    sections.Add(new ReportSection(
                        SectionKey: $"analyzer-failure:{run.AnalyzerName}",
                        Title: $"Analyzer failed: {run.AnalyzerName}",
                        Category: "Pipeline",
                        Severity: FindingSeverity.Warning,
                        NarrativeSummary: run.ErrorMessage ?? "Analyzer failed without error details.",
                        EvidenceRows:
                        [
                            new ReportEvidenceRow("ErrorType", run.ErrorType ?? "Unknown"),
                            new ReportEvidenceRow("ErrorMessage", run.ErrorMessage ?? "Unknown")
                        ],
                        RemediationHints: ["Inspect analyzer failure details and re-run analysis."],
                        Fingerprints: [$"analyzer-failure:{run.AnalyzerName}"]));
                }
            }

            List<ReportSection> deduped = DeduplicateSections(sections, out DedupDiagnostics dedupDiagnostics);

            deduped = deduped
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .ThenBy(s => s.SectionKey, StringComparer.Ordinal)
                .ToList();

            DedupDiagnostics normalizedDiagnostics = dedupDiagnostics with
            {
                EvidenceBeforeMerge = evidenceBeforeMerge
            };

            return new ComposedReport(
                dumpPath,
                DateTime.UtcNow,
                elapsed,
                deduped,
                normalizedDiagnostics,
                ReportContractVersions.ReportSchemaV1,
                ReportContractVersions.SectionSchemaV1,
                detailedAnalyzerSections);
        }

        private static IReadOnlyList<DetailedAnalyzerSection> BuildDetailedAnalyzerSections(IReadOnlyList<AnalyzerRunResult> runs, IReadOnlyList<IAnalyzerReporter> reporters)
        {
            if (reporters.Count == 0)
            {
                return [];
            }

            Dictionary<string, AnalyzerDomainResult> domainResults = new(StringComparer.Ordinal);

            foreach (AnalyzerRunResult run in runs)
            {
                if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
                {
                    continue;
                }

                domainResults[run.AnalyzerName] = run.Result;
            }

            if (domainResults.Count == 0)
            {
                return [];
            }

            List<DetailedAnalyzerSection> sections = [];

            foreach (IAnalyzerReporter reporter in reporters)
            {
                if (!domainResults.TryGetValue(reporter.AnalyzerName, out AnalyzerDomainResult? result))
                {
                    continue;
                }

                if (!reporter.CanHandle(result))
                {
                    continue;
                }

                StructuredCaptureReportWriter writer = new();
                reporter.Render(result, writer);

                string content = writer.GetContent();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                sections.Add(new DetailedAnalyzerSection(
                    reporter.AnalyzerName,
                    content,
                    writer.GetSubmodules()));
            }

            return sections;
        }

        private static List<ReportSection> DeduplicateSections(IReadOnlyList<ReportSection> sections, out DedupDiagnostics diagnostics)
        {
            Dictionary<string, ReportSection> dedupMap = new(StringComparer.Ordinal);
            List<string> mergedKeys = [];
            int duplicateCandidates = 0;
            int evidenceAfter = 0;

            foreach (ReportSection section in sections)
            {
                if (!dedupMap.TryGetValue(section.SectionKey, out ReportSection? existing))
                {
                    dedupMap[section.SectionKey] = section;
                    continue;
                }

                duplicateCandidates++;
                mergedKeys.Add(section.SectionKey);

                dedupMap[section.SectionKey] = existing with
                {
                    Severity = (FindingSeverity)Math.Max((int)existing.Severity, (int)section.Severity),
                    NarrativeSummary = string.Join(Environment.NewLine,
                        new[] { existing.NarrativeSummary, section.NarrativeSummary }
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct(StringComparer.Ordinal)),
                    EvidenceRows = existing.EvidenceRows
                        .Concat(section.EvidenceRows)
                        .DistinctBy(r => (r.Label, r.Value))
                        .ToList(),
                    RemediationHints = existing.RemediationHints
                        .Concat(section.RemediationHints)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    Fingerprints = existing.Fingerprints
                        .Concat(section.Fingerprints)
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
                };
            }

            foreach (ReportSection section in dedupMap.Values)
            {
                evidenceAfter += section.EvidenceRows.Count;
            }

            diagnostics = new DedupDiagnostics(
                DuplicateCandidates: duplicateCandidates,
                MergedSections: mergedKeys.Count,
                EvidenceBeforeMerge: 0,
                EvidenceAfterMerge: evidenceAfter,
                MergedKeys: mergedKeys.Distinct(StringComparer.Ordinal).ToList());

            return dedupMap.Values.ToList();
        }

        public static List<string> BuildReportInsights(
            TimeSpan elapsed,
            IReadOnlyList<InsightFinding> findings,
            IReadOnlyList<string>? additionalInsights = null)
        {
            var insights = new List<string>(capacity: 8);

            if (additionalInsights is { Count: > 0 })
                insights.AddRange(additionalInsights);

            int criticalCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warningCount = findings.Count(f => f.Severity == FindingSeverity.Warning);

            if (criticalCount > 0)
                insights.Add($"[CRITICAL] {criticalCount:N0} critical finding(s) detected. Prioritize these first.");

            if (warningCount > 0)
                insights.Add($"[WARNING] {warningCount:N0} warning finding(s) detected. Address these after critical issues.");

            foreach (var finding in findings
                .Where(f => f.Severity != FindingSeverity.Info)
                .OrderByDescending(f => f.Severity)
                .Take(3))
            {
                insights.Add($"[{finding.Severity}] {finding.Title} â€” {finding.Evidence}");
            }

            insights.Add($"[INFO] Analysis completed in {elapsed.TotalSeconds:F1}s.");

            if (findings.Count == 0)
                insights.Insert(0, "[INFO] No structured findings were emitted by analyzers.");

            return insights;
        }

        public static List<string> BuildTrendInsights(
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

        public static List<InsightFinding> BuildTrendFindings(
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

        public static string BuildTrendComparisonSection(
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

                var regressionsByAnalyzer = overall.ToDictionary(r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);
                var orderedTimeline = timeline.OrderByDescending(t => regressionsByAnalyzer.GetValueOrDefault(t.AnalyzerName));

                foreach (var analyzerTimeline in orderedTimeline)
                {
                    builder.AppendLine($"  [{analyzerTimeline.AnalyzerName}]");

                    foreach (var point in analyzerTimeline.Points)
                    {
                        var validValues = point.Values.Where(v => !double.IsNaN(v)).ToList();
                        if (validValues.Count == 0) continue;

                        string valuesLine = string.Join(" â†’ ", point.Values.Select(v => FormatHelper.FormatMetricValue(v, point.Unit)));

                        double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
                        double lastVal = point.Values.Last(v => !double.IsNaN(v));
                        double delta = lastVal - firstVal;
                        double? deltaPercent = Math.Abs(firstVal) > double.Epsilon ? delta * 100.0 / firstVal : null;

                        string deltaStr = FormatHelper.FormatDeltaValue(delta, point.Unit);
                        string pctStr = deltaPercent.HasValue ? $", {(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%" : string.Empty;

                        string icon = (point.Direction, delta > 0) switch
                        {
                            (MetricTrendDirection.HigherIsWorse, true)  => "âš ï¸ ",
                            (MetricTrendDirection.HigherIsWorse, false) when delta < 0 => "âœ… ",
                            (MetricTrendDirection.LowerIsWorse, false) when delta < 0  => "âš ï¸ ",
                            (MetricTrendDirection.LowerIsWorse, true)   => "âœ… ",
                            _ => "â„¹ï¸ "
                        };

                        string deltaLabel = delta == 0 ? "no change" : $"Î” {deltaStr}{pctStr}";
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

        internal sealed record FindingLifecycleResult(
            IReadOnlyList<InsightFinding> NewFindings,
            IReadOnlyList<InsightFinding> PersistentFindings,
            IReadOnlyList<InsightFinding> ResolvedFindings);
    }
}



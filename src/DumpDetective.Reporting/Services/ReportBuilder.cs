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

            IReadOnlyList<ExecutiveSummaryItem> executiveSummary = BuildExecutiveSummary(deduped);
            IReadOnlyList<DeveloperActionItem> developerActions = BuildDeveloperActionPlan(deduped);

            return new ComposedReport(
                dumpPath,
                DateTime.UtcNow,
                elapsed,
                deduped,
                executiveSummary,
                developerActions,
                normalizedDiagnostics,
                ReportContractVersions.ReportSchemaV1,
                ReportContractVersions.SectionSchemaV1,
                detailedAnalyzerSections);
        }

        private static IReadOnlyList<ExecutiveSummaryItem> BuildExecutiveSummary(IReadOnlyList<ReportSection> sections)
        {
            int critical = sections.Count(s => s.Severity == FindingSeverity.Critical);
            int warning = sections.Count(s => s.Severity == FindingSeverity.Warning);
            int info = sections.Count(s => s.Severity == FindingSeverity.Info);
            int total = sections.Count;

            string overallHealth = critical > 0
                ? "Critical - production risk is currently elevated"
                : warning > 0
                    ? "Watch - degrading signals detected"
                    : "Stable - no immediate high-risk signal";

            ReportSection? topRiskSection = sections
                .Where(s => s.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .FirstOrDefault();

            IReadOnlyList<ReportSection> topRisks = sections
                .Where(s => s.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .Take(3)
                .ToList();

            IReadOnlyList<ReportSection> actionQueue = sections
                .Where(s => s.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            string topRisk = topRiskSection is null
                ? "No high-severity issues were found."
                : $"Most urgent issue: [{topRiskSection.Severity}] {topRiskSection.Title} ({topRiskSection.Category}).";

            string topRiskEvidence = topRiskSection is null
                ? "No critical/warning evidence requires immediate escalation."
                : $"Signal: {topRiskSection.NarrativeSummary}";

            string businessImpact = topRiskSection is null
                ? "Current dump does not show immediate service disruption risks."
                : topRiskSection.Category switch
                {
                    "Leak" or "Memory" or "Fragmentation" => "Potential stability and performance degradation due to memory pressure.",
                    "Crash" or "Stability" => "Potential user-facing failures and service interruptions.",
                    "Hang" or "Threading" or "Retention" => "Potential response-time degradation and request processing delays.",
                    _ => "Potential reliability degradation if highlighted issues are not addressed."
                };

            string primaryRisks = topRisks.Count == 0
                ? "No critical/warning risks identified."
                : string.Join("; ", topRisks.Select(s => $"[{s.Severity}] {s.Title}"));

            var topCategoryGroup = sections
                .GroupBy(s => s.Category, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            string riskConcentration = topCategoryGroup is null
                ? "No risk concentration detected."
                : $"{topCategoryGroup.Key} contributes {topCategoryGroup.Count():N0}/{total:N0} findings ({(topCategoryGroup.Count() * 100.0 / total):F1}%).";

            string urgencyWindow = critical > 0
                ? "Action window: immediate (same day) for critical items."
                : warning >= 3
                    ? "Action window: next sprint to prevent escalation."
                    : warning > 0
                        ? "Action window: planned remediation cycle."
                        : "Action window: monitor via regular health checks.";

            string keyThemes = sections
                .GroupBy(s => s.Category, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(g => $"{g.Key} ({g.Count()})")
                .DefaultIfEmpty("No dominant risk themes")
                .Aggregate((a, b) => $"{a}, {b}");

            string severityMix = total == 0
                ? "No findings generated."
                : $"High-severity share: {critical + warning:N0}/{total:N0} ({((critical + warning) * 100.0 / total):F1}%).";

            string actionQueueSummary = actionQueue.Count == 0
                ? "No immediate remediation queue."
                : string.Join(" | ", actionQueue.Select(s => $"{(s.Severity == FindingSeverity.Critical ? "P0" : "P1")}: {s.Title}"));

            string nextStep = critical > 0
                ? "Address critical items first, then re-run analysis to confirm risk reduction."
                : warning > 0
                    ? "Triage warning items by business impact and schedule remediation."
                    : "Maintain current safeguards and continue periodic monitoring.";

            string leadershipDecision = critical > 0
                ? "Recommended leadership decision: prioritize reliability stabilization over feature throughput until P0 risks are reduced."
                : warning >= 3
                    ? "Recommended leadership decision: allocate focused engineering capacity in the next sprint for risk burn-down."
                    : "Recommended leadership decision: continue planned delivery while monitoring current risk profile.";

            return
            [
                new ExecutiveSummaryItem("Overall health", overallHealth),
                new ExecutiveSummaryItem("What this means", businessImpact),
                new ExecutiveSummaryItem("Risk counts", $"Critical: {critical:N0}, Warning: {warning:N0}, Info: {info:N0}"),
                new ExecutiveSummaryItem("Severity mix", severityMix),
                new ExecutiveSummaryItem("Top risk", topRisk),
                new ExecutiveSummaryItem("Top risk signal", topRiskEvidence),
                new ExecutiveSummaryItem("Primary risks", primaryRisks),
                new ExecutiveSummaryItem("Risk concentration", riskConcentration),
                new ExecutiveSummaryItem("Key themes", keyThemes),
                new ExecutiveSummaryItem("Immediate action queue", actionQueueSummary),
                new ExecutiveSummaryItem("Urgency", urgencyWindow),
                new ExecutiveSummaryItem("Recommended next step", nextStep),
                new ExecutiveSummaryItem("Decision guidance", leadershipDecision)
            ];
        }

        private static IReadOnlyList<DeveloperActionItem> BuildDeveloperActionPlan(IReadOnlyList<ReportSection> sections)
        {
            var prioritized = sections
                .Where(s => s.RemediationHints.Count > 0)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .Take(10)
                .ToList();

            List<DeveloperActionItem> actions = new(prioritized.Count);
            foreach (ReportSection section in prioritized)
            {
                string priority = section.Severity switch
                {
                    FindingSeverity.Critical => "P0",
                    FindingSeverity.Warning => "P1",
                    _ => "P2"
                };

                actions.Add(new DeveloperActionItem(
                    Priority: priority,
                    Title: section.Title,
                    Action: section.RemediationHints[0],
                    Impact: section.NarrativeSummary));
            }

            return actions;
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



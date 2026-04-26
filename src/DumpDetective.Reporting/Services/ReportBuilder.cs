using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
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

        /// <summary>
        /// Composes a report directly from a flat list of findings without requiring a fake
        /// AnalyzerRunResult wrapper. Used by trend composition to avoid fabricated pipeline state.
        /// </summary>
        public static ComposedReport ComposeFromFindings(
            string dumpPath,
            IReadOnlyList<InsightFinding> findings,
            TimeSpan elapsed)
        {
            List<ReportSection> sections = [];
            int evidenceBeforeMerge = 0;

            foreach (InsightFinding finding in findings)
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
                        new ReportEvidenceRow("Analyzer", finding.Analyzer),
                        new ReportEvidenceRow("Evidence", finding.Evidence)
                    ],
                    RemediationHints: [finding.Recommendation],
                    Fingerprints: [finding.EffectiveFingerprint]));
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

            // Executive summary and developer action plan are omitted by default for the
            // default (All) audience to keep the report focused on raw findings. These
            // sections are still available for targeted audiences (Executive/Developer)
            // via the `audience` parameter when composing reports.
            IReadOnlyList<ExecutiveSummaryItem> executiveSummary = [];
            IReadOnlyList<DeveloperActionItem> developerActions = [];

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
                DetailedAnalyzerSections: null);
        }

        public static ComposedReport ComposeCanonicalReport(
            string dumpPath,
            IReadOnlyList<AnalyzerRunResult> runs,
            TimeSpan elapsed,
            IReadOnlyList<IAnalyzerReporter> reporters,
            ReportAudience audience = ReportAudience.All)
        {
            List<ReportSection> sections = [];
            int evidenceBeforeMerge = 0;

            // Detailed sections are only built when the audience requires them, avoiding wasted I/O.
            IReadOnlyList<DetailedAnalyzerSection> detailedAnalyzerSections =
                audience is ReportAudience.Executive or ReportAudience.Developer
                    ? []
                    : BuildDetailedAnalyzerSections(runs, reporters);

            foreach (AnalyzerRunResult run in runs)
            {
                if (run.Findings is { Count: > 0 })
                {
                    foreach (InsightFinding finding in run.Findings)
                    {
                        evidenceBeforeMerge += 2;
                        //sections.Add(new ReportSection(
                        //    SectionKey: finding.EffectiveFingerprint,
                        //    Title: finding.Title,
                        //    Category: finding.Category,
                        //    Severity: finding.Severity,
                        //    NarrativeSummary: finding.Evidence,
                        //    EvidenceRows:
                        //    [
                        //        new ReportEvidenceRow("Analyzer", run.AnalyzerName),
                        //        new ReportEvidenceRow("Evidence", finding.Evidence)
                        //    ],
                        //    RemediationHints: [finding.Recommendation],
                        //    Fingerprints: [finding.EffectiveFingerprint]));
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

            // Only build the executive summary when explicitly requested for Executive
            // audience. Do not include it for the general (All) audience to avoid
            // duplicating priority guidance alongside the detailed findings.
            IReadOnlyList<ExecutiveSummaryItem> executiveSummary =
                audience == ReportAudience.Executive
                    ? BuildExecutiveSummary(deduped)
                    : [];

            // Only build the developer action plan when explicitly requested for
            // Developer audience. Skip for All and Executive audiences to keep the
            // report's main sections focused on findings and diagnostics.
            IReadOnlyList<DeveloperActionItem> developerActions =
                audience == ReportAudience.Developer
                    ? BuildDeveloperActionPlan(deduped)
                    : [];

            // Raw finding sections are stripped for Executive audience.
            IReadOnlyList<ReportSection> outputSections =
                audience == ReportAudience.Executive ? [] : deduped;

            return new ComposedReport(
                dumpPath,
                DateTime.UtcNow,
                elapsed,
                outputSections,
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

            // Overall health status
            string overallHealth = critical > 0
                ? $"Critical — {critical:N0} critical issue(s) require immediate attention"
                : warning > 0
                    ? $"Watch — {warning:N0} warning(s) detected, degrading signals present"
                    : total > 0
                        ? $"Stable — {info:N0} informational finding(s), no high-severity risk"
                        : "Stable — no findings generated";

            // Top risk with its diagnostic signal
            ReportSection? topRiskSection = sections
                .Where(s => s.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .FirstOrDefault();

            string topRisk = topRiskSection is null
                ? "No high-severity issues detected."
                : $"[{topRiskSection.Severity}] {topRiskSection.Title} — {topRiskSection.NarrativeSummary}";

            // Business impact derived from the dominant risk category
            string businessImpact = topRiskSection is null
                ? "No immediate service disruption risk detected in this dump."
                : topRiskSection.Category switch
                {
                    "Leak" or "Memory" or "Fragmentation" =>
                        "Memory pressure risk: potential for OutOfMemoryException, GC pauses, and process recycling under load.",
                    "Crash" or "Stability" =>
                        "Stability risk: active exceptions indicate potential user-facing failures or service crashes.",
                    "Hang" or "Threading" or "Retention" =>
                        "Responsiveness risk: thread contention or retention issues may cause request timeouts.",
                    _ => "Reliability risk: highlighted issues may cause degraded service quality if not addressed."
                };

            // Top 3 actionable findings for leadership
            IReadOnlyList<ReportSection> topActionable = sections
                .Where(s => s.Severity is FindingSeverity.Critical or FindingSeverity.Warning)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .Take(3)
                .ToList();

            string actionableItems = topActionable.Count == 0
                ? "No critical or warning items require escalation."
                : string.Join("; ", topActionable.Select((s, i) =>
                    $"{(s.Severity == FindingSeverity.Critical ? "P0" : "P1")}: {s.Title}"));

            // Urgency window
            string urgency = critical > 0
                ? "Immediate (same-day) action required for critical items."
                : warning >= 3
                    ? "Address in next sprint to prevent escalation."
                    : warning > 0
                        ? "Schedule in planned remediation cycle."
                        : "No urgent action required; continue periodic monitoring.";

            // Recommended next step
            string nextStep = critical > 0
                ? "Review critical findings in the Developer Action Plan, apply fixes, and re-run analysis to confirm risk reduction."
                : warning > 0
                    ? "Triage warning items by business impact using the Developer Action Plan and schedule remediation."
                    : "Maintain current safeguards and continue periodic memory health checks.";

            return
            [
                new ExecutiveSummaryItem("Overall health", overallHealth),
                new ExecutiveSummaryItem("Finding counts", $"Critical: {critical:N0}  Warning: {warning:N0}  Info: {info:N0}  Total: {total:N0}"),
                new ExecutiveSummaryItem("Business impact", businessImpact),
                new ExecutiveSummaryItem("Top risk", topRisk),
                new ExecutiveSummaryItem("Actionable items", actionableItems),
                new ExecutiveSummaryItem("Urgency", urgency),
                new ExecutiveSummaryItem("Recommended next step", nextStep)
            ];
        }

        private static IReadOnlyList<DeveloperActionItem> BuildDeveloperActionPlan(IReadOnlyList<ReportSection> sections)
        {
            // Always include all critical findings; pad with warnings/info up to the cap.
            const int ActionPlanCap = 10;

            IReadOnlyList<ReportSection> criticals = sections
                .Where(s => s.Severity == FindingSeverity.Critical && s.RemediationHints.Count > 0)
                .OrderBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .ToList();

            IReadOnlyList<ReportSection> remainder = sections
                .Where(s => s.Severity != FindingSeverity.Critical && s.RemediationHints.Count > 0)
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.Category, StringComparer.Ordinal)
                .ThenBy(s => s.Title, StringComparer.Ordinal)
                .Take(Math.Max(0, ActionPlanCap - criticals.Count))
                .ToList();

            List<DeveloperActionItem> actions = new(criticals.Count + remainder.Count);
            foreach (ReportSection section in criticals.Concat(remainder))
            {
                string priority = section.Severity switch
                {
                    FindingSeverity.Critical => "P0",
                    FindingSeverity.Warning => "P1",
                    _ => "P2"
                };

                string impact = section.Category switch
                {
                    "Leak" or "Memory" =>
                        "Unchecked growth will increase GC pressure, slow the application, and risk process recycling under load.",
                    "Fragmentation" =>
                        "Heap fragmentation reduces allocation efficiency and can trigger premature LOH collections.",
                    "Crash" or "Stability" =>
                        "Active exceptions can cause request failures and unhandled crashes visible to end-users.",
                    "Hang" or "Threading" =>
                        "Thread contention will increase response latency and can lead to request timeouts.",
                    "Retention" =>
                        "Unnecessary object retention increases memory footprint and delays garbage collection.",
                    "Pipeline" =>
                        "Analyzer failures may have left diagnostic gaps; re-running after the fix ensures complete coverage.",
                    _ => "Leaving this unaddressed risks degraded reliability or performance over time."
                };

                actions.Add(new DeveloperActionItem(
                    Priority: priority,
                    Title: section.Title,
                    Action: section.RemediationHints[0],
                    Impact: impact));
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

            foreach (IAnalyzerReporter reporter in reporters.OrderBy(r => r.SortOrder))
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
                    reporter.DisplayTitle,
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
                insights.Add($"[{finding.Severity}] {finding.Title} — {finding.Evidence}");
            }

            insights.Add($"[INFO] Analysis completed in {elapsed.TotalSeconds:F1}s.");

            if (findings.Count == 0)
                insights.Insert(0, "[INFO] No structured findings were emitted by analyzers.");

            return insights;
        }

    }
}


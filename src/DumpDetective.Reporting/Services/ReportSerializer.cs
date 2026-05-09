using System.Linq;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Maps <see cref="AnalyzerRunResult"/> list → <see cref="AnalysisReportDocument"/>.
/// Pure function — no text formatting, no side effects, no I/O.
/// </summary>
internal sealed class ReportSerializer
{
    public AnalysisReportDocument Serialize(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        ReportAudience audience = ReportAudience.All,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null)
    {
        // ── 1. Build per-analyzer sections ───────────────────────────────────
        List<AnalyzerDetailSection> analyzerSections = BuildAnalyzerSections(runs, builders);

        // If analyzers produced exported artifact files (NDJSON/CSV etc.), append
        // a short informational note to the corresponding analyzer section so
        // users are aware extra artifacts exist alongside the main report.
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Artifacts is null || run.Artifacts.Count == 0)
                continue;

            var dupArtifacts = run.Artifacts.Where(a =>
                (a.FileName is not null && (a.FileName.Contains("ndjson", StringComparison.OrdinalIgnoreCase)
                                           || a.FileName.Contains("duplicates", StringComparison.OrdinalIgnoreCase)
                                           || a.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                || string.Equals(a.ContentType, "application/gzip", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (dupArtifacts.Count == 0)
                continue;

            int idx = analyzerSections.FindIndex(s => string.Equals(s.AnalyzerName, run.AnalyzerName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                continue;

            AnalyzerDetailSection section = analyzerSections[idx];
            var blocks = section.Blocks.ToList();
            blocks.Add(new DividerBlock());
            blocks.Add(new TextBlock($"Note: This analyzer produced {dupArtifacts.Count} artifact file(s) (e.g. NDJSON/CSV) containing exported snapshots or duplicate records. These artifacts are written to the report's artifacts folder and can be downloaded for deeper analysis."));
            analyzerSections[idx] = section with { Blocks = blocks };
        }
        analyzerSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));

        // ── 2. Map all findings to FindingRecord + collect pipeline failures ──
        List<FindingRecord> allFindings = [];
        int evidenceBeforeMerge = 0;

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Findings is { Count: > 0 })
            {
                foreach (InsightFinding finding in run.Findings)
                {
                    evidenceBeforeMerge += 2;
                    allFindings.Add(MapFinding(finding));
                }
            }

            if (run.Status == AnalyzerExecutionStatus.Failed)
            {
                evidenceBeforeMerge += 2;
                allFindings.Add(new FindingRecord(
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Analyzer failed: {run.AnalyzerName}",
                    Evidence: run.ErrorMessage ?? "Analyzer failed without error details.",
                    Recommendation: "Inspect analyzer failure details and re-run analysis.",
                    Tags: [],
                    Fingerprint: $"analyzer-failure:{run.AnalyzerName}"));
            }

            if (!string.IsNullOrWhiteSpace(run.FindingGeneratorError))
            {
                evidenceBeforeMerge += 1;
                allFindings.Add(new FindingRecord(
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Finding generator failed: {run.AnalyzerName}",
                    Evidence: $"The finding generator for '{run.AnalyzerName}' threw an exception. Findings for this analyzer may be incomplete or missing.",
                    Recommendation: "Re-run analysis. If the error persists, report it with the full error details.",
                    Tags: [],
                    Fingerprint: $"finding-generator-error:{run.AnalyzerName}"));
            }
        }

        // ── 3. Deduplicate findings ───────────────────────────────────────────
        List<FindingRecord> deduped = DeduplicateFindings(allFindings, out int duplicateCandidates, out int mergedSections);

        // Sort: Critical → Warning → Info, then by Category, then by Title
        deduped.Sort(static (a, b) =>
        {
            int severityCompare = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (severityCompare != 0) return severityCompare;
            int catCompare = StringComparer.Ordinal.Compare(a.Category, b.Category);
            if (catCompare != 0) return catCompare;
            return StringComparer.Ordinal.Compare(a.Title, b.Title);
        });

        DedupRecord dedupRecord = new(
            MergedSections: mergedSections,
            DuplicateCandidates: duplicateCandidates,
            EvidenceBeforeMerge: evidenceBeforeMerge);

        // ── 4. Confidence notes ───────────────────────────────────────────────
        List<ConfidenceNote> confidence = BuildConfidenceNotes(runs);

        // ── 5. Audience-specific projections ─────────────────────────────────
        // Compute total managed bytes from available analyzer domain results (Memory, GC generation, AppDomain)
        long totalManagedBytes = 0;
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.MemoryDomainResult mem)
            {
                totalManagedBytes = (long)mem.TotalBytes;
                break;
            }
            if (run.Result is DumpDetective.Analysis.Models.GCGenerationDomainResult gc)
            {
                try
                {
                    ulong sum = gc.Gen0Bytes + gc.Gen1Bytes + gc.Gen2Bytes + gc.LohBytes;
                    totalManagedBytes = (long)Math.Min((ulong)long.MaxValue, sum);
                    break;
                }
                catch { /* ignore overflow, continue */ }
            }
            if (run.Result is DumpDetective.Analysis.Models.AppDomainDomainResult app)
            {
                try
                {
                    ulong sum = 0;
                    foreach (var d in app.Domains) sum += d.EstimatedManagedBytes;
                    totalManagedBytes = (long)Math.Min((ulong)long.MaxValue, sum);
                    break;
                }
                catch { }
            }
        }

        // Include Executive summary for explicit Executive audience or when Audience==All
        ExecutiveSummaryRecord? executiveSummary = (audience == ReportAudience.Executive || audience == ReportAudience.All)
            ? BuildExecutiveSummary(deduped, totalManagedBytes)
            : null;

        IReadOnlyList<DeveloperActionRecord> developerActionPlan = audience == ReportAudience.Developer
            ? BuildDeveloperActionPlan(deduped)
            : [];

        // Executive audience strips raw findings (summary replaces them)
        IReadOnlyList<FindingRecord> outputFindings = audience == ReportAudience.Executive ? [] : deduped;

        return new AnalysisReportDocument
        {
            DumpPath         = dumpPath,
            GeneratedAtUtc   = DateTime.UtcNow,
            ElapsedSeconds   = elapsed.TotalSeconds,
            IncidentContext  = incidentContext,
            Findings         = outputFindings,
            AnalyzerSections = analyzerSections,
            ExecutiveSummary = executiveSummary,
            DeveloperActionPlan = developerActionPlan,
            Confidence       = confidence,
            DedupDiagnostics = dedupRecord
        ,
            Artifacts = runs.SelectMany(r => r.Artifacts ?? Array.Empty<DumpDetective.Core.Models.ReportArtifact>()).ToList()
        ,
            AnalyzerRunStatuses = runs.Select(r => new AnalyzerRunStatusRecord(
                AnalyzerName: r.AnalyzerName,
                Status: r.Status.ToString(),
                DurationMs: r.Duration.TotalMilliseconds,
                FindingCount: r.FindingCount,
                WarningCount: r.WarningCount,
                ObjectScanCount: r.ObjectScanCount,
                ErrorMessage: r.ErrorMessage)).ToList()
        };
    }

    // ── Section routing ───────────────────────────────────────────────────────

    private static List<AnalyzerDetailSection> BuildAnalyzerSections(
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> builders)
    {
        var sections = new List<AnalyzerDetailSection>(runs.Count);

        for (int r = 0; r < runs.Count; r++)
        {
            AnalyzerRunResult run = runs[r];
            if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
                continue;

            for (int b = 0; b < builders.Count; b++)
            {
                IAnalyzerSectionBuilder builder = builders[b];
                if (!builder.CanHandle(run.Result))
                    continue;

                sections.Add(builder.Build(run.Result));
                break;  // first matching builder wins
            }
        }

        return sections;
    }

    // ── Finding mapping ───────────────────────────────────────────────────────

    private static FindingRecord MapFinding(InsightFinding f) =>
        new(
            Analyzer:       f.Analyzer,
            Category:       f.Category,
            Severity:       f.Severity.ToString(),
            Title:          f.Title,
            Evidence:       f.Evidence,
            Recommendation: f.Recommendation,
            Tags:           f.Tags,
            Fingerprint:    f.EffectiveFingerprint)
        {
            ConfidenceScore = null,
            EvidenceRefs = null,
            SuggestedOwner = null,
            Effort = null,
            ValidationStep = null,
            TrackingStatus = null
        };

    // ── Deduplication (preserves ReportBuilder.DeduplicateSections logic) ────

    private static List<FindingRecord> DeduplicateFindings(
        List<FindingRecord> findings,
        out int duplicateCandidates,
        out int mergedSections)
    {
        var dedupMap = new Dictionary<string, FindingRecord>(StringComparer.Ordinal);
        var mergedKeys = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord f = findings[i];

            if (!dedupMap.TryGetValue(f.Fingerprint, out FindingRecord? existing))
            {
                dedupMap[f.Fingerprint] = f;
                continue;
            }

            mergedKeys.Add(f.Fingerprint);

            // Merge: take higher severity, combine evidence + recommendation (distinct)
            // Merge evidence/recommendation as lists (preserve order, dedupe exact entries)
            var existingEvidence = existing.EvidenceItems ?? new[] { existing.Evidence };
            var newEvidence = f.EvidenceItems ?? new[] { f.Evidence };
            var mergedEvidenceList = existingEvidence.Concat(newEvidence).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            string mergedEvidence = string.Join(Environment.NewLine, mergedEvidenceList);

            var existingRec = existing.RecommendationItems ?? new[] { existing.Recommendation };
            var newRec = f.RecommendationItems ?? new[] { f.Recommendation };
            var mergedRecList = existingRec.Concat(newRec).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            string mergedRec = string.Join(Environment.NewLine, mergedRecList);
            int    mergedSeverity = Math.Max(SeverityOrdinal(existing.Severity), SeverityOrdinal(f.Severity));
            string mergedSeverityStr = SeverityFromOrdinal(mergedSeverity);

            var mergedTags = new List<string>(existing.Tags.Count + f.Tags.Count);
            foreach (string t in existing.Tags) mergedTags.Add(t);
            foreach (string t in f.Tags) { if (!mergedTags.Contains(t)) mergedTags.Add(t); }

            dedupMap[f.Fingerprint] = new FindingRecord(
                Analyzer:       existing.Analyzer,
                Category:       existing.Category,
                Severity:       mergedSeverityStr,
                Title:          existing.Title,
                Evidence:       mergedEvidence,
                Recommendation: mergedRec,
                Tags:           mergedTags,
                Fingerprint:    existing.Fingerprint)
            {
                EvidenceItems = mergedEvidenceList,
                RecommendationItems = mergedRecList,
                ConfidenceScore = existing.ConfidenceScore ?? f.ConfidenceScore,
                EvidenceRefs = (existing.EvidenceRefs ?? Array.Empty<EvidenceRef>()).Concat(f.EvidenceRefs ?? Array.Empty<EvidenceRef>()).ToList(),
                SuggestedOwner = existing.SuggestedOwner ?? f.SuggestedOwner,
                Effort = existing.Effort ?? f.Effort,
                ValidationStep = existing.ValidationStep ?? f.ValidationStep,
                TrackingStatus = existing.TrackingStatus ?? f.TrackingStatus
            };
        }

        duplicateCandidates = mergedKeys.Count;
        mergedSections      = mergedKeys.Count;
        return new List<FindingRecord>(dedupMap.Values);
    }

    private static string MergeText(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(b))
            return a;
        if (string.IsNullOrWhiteSpace(a))
            return b;
        return $"{a}{Environment.NewLine}{b}";
    }

    // ── Confidence notes ──────────────────────────────────────────────────────

    private static List<ConfidenceNote> BuildConfidenceNotes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        var notes = new List<ConfidenceNote>();

        for (int i = 0; i < runs.Count; i++)
        {
            AnalyzerRunResult run = runs[i];
            if (run.Result is null) continue;

            if (run.Result is RetentionDomainResult ml && ml.SkippedReferenceAddresses > 0)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: $"Reference tracking cap hit; {ml.SkippedReferenceAddresses:N0} addresses skipped — highly-referenced-object counts may be partial."));
            }

            if (run.Result is RetentionDomainResult mlCap && mlCap.ObjectScanCapped)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: "Object scan cap reached; incoming-reference counts are based on a partial heap traversal — increase MaxLeakScanObjects for deeper coverage."));
            }

            if (run.Result is HangDomainResult hang && hang.TaskScanLimited)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: "Task scan limited due to heap size; task totals may be partial."));
            }

            if (run.Result is AsyncTaskDomainResult asyncTask && asyncTask.TaskScanLimited)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: "Async task scan was capped at 50 000 entries; orphan counts and state totals may be partial."));
            }

            if (run.Result is AsyncStateMachineDomainResult asm && asm.ScanLimited)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: "Async state machine type scan was capped at 200 candidate types; total counts and top-type list may be partial."));
            }

            if (run.Result is ArrayDomainResult arr && arr.ScanLimited)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: "Array sparse sampling was capped at 500 candidate arrays; sparse/wasteful array list may be partial."));
            }
        }

        return notes;
    }

    // ── Executive summary ─────────────────────────────────────────────────────

    private static ExecutiveSummaryRecord BuildExecutiveSummary(IReadOnlyList<FindingRecord> findings, long totalManagedBytes)
    {
        // P1.2: Use ExplainableScoringEngine for reproducible, contributor-backed scores.
        var (leak, gcPressure, thread) = ExplainableScoringEngine.ComputeScores(findings);

        // Top 3 Critical or Warning findings
        var top3 = new List<FindingRecord>(3);
        for (int i = 0; i < findings.Count && top3.Count < 3; i++)
        {
            int ord = SeverityOrdinal(findings[i].Severity);
            if (ord >= 1)
                top3.Add(findings[i]);
        }

        return new ExecutiveSummaryRecord(
            TotalManagedBytes:      totalManagedBytes,
            LeakLikelihoodScore:    leak.Score,
            GcPressureScore:        gcPressure.Score,
            ThreadContentionScore:  thread.Score,
            TopRecommendations:     top3)
        {
            ScoreBreakdowns = [leak, gcPressure, thread],
        };
    }

    // ── Developer action plan ─────────────────────────────────────────────────

    private static List<DeveloperActionRecord> BuildDeveloperActionPlan(IReadOnlyList<FindingRecord> findings)
    {
        const int ActionPlanCap = 10;

        // Collect all Critical first, then fill remaining slots with Warning/Info
        var criticals  = new List<FindingRecord>();
        var remainder  = new List<FindingRecord>();

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord f = findings[i];
            if (!string.IsNullOrWhiteSpace(f.Recommendation))
            {
                if (SeverityOrdinal(f.Severity) == 2)
                    criticals.Add(f);
                else
                    remainder.Add(f);
            }
        }

        int remainderCap = Math.Max(0, ActionPlanCap - criticals.Count);
        var actions = new List<DeveloperActionRecord>(criticals.Count + Math.Min(remainder.Count, remainderCap));

        int totalToProcess = criticals.Count + Math.Min(remainder.Count, remainderCap);
        for (int i = 0; i < totalToProcess; i++)
        {
            FindingRecord f = i < criticals.Count ? criticals[i] : remainder[i - criticals.Count];
            int ord = SeverityOrdinal(f.Severity);

            string priority = ord == 2 ? "P0" : ord == 1 ? "P1" : "P2";
            string impact = f.Category switch
            {
                var c when c.Contains("Leak",          StringComparison.OrdinalIgnoreCase) ||
                           c.Contains("Memory",         StringComparison.OrdinalIgnoreCase)
                    => "Unchecked growth will increase GC pressure, slow the application, and risk process recycling under load.",
                var c when c.Contains("Fragmentation",  StringComparison.OrdinalIgnoreCase)
                    => "Heap fragmentation reduces allocation efficiency and can trigger premature LOH collections.",
                var c when c.Contains("Crash",          StringComparison.OrdinalIgnoreCase) ||
                           c.Contains("Stability",       StringComparison.OrdinalIgnoreCase)
                    => "Active exceptions can cause request failures and unhandled crashes visible to end-users.",
                var c when c.Contains("Hang",           StringComparison.OrdinalIgnoreCase) ||
                           c.Contains("Threading",       StringComparison.OrdinalIgnoreCase)
                    => "Thread contention will increase response latency and can lead to request timeouts.",
                var c when c.Contains("Retention",       StringComparison.OrdinalIgnoreCase)
                    => "Unnecessary object retention increases memory footprint and delays garbage collection.",
                var c when c.Contains("Pipeline",        StringComparison.OrdinalIgnoreCase)
                    => "Analyzer failures may have left diagnostic gaps; re-running after the fix ensures complete coverage.",
                _   => "Leaving this unaddressed risks degraded reliability or performance over time."
            };

            actions.Add(new DeveloperActionRecord(
                Priority:   priority,
                Title:      f.Title,
                Action:     f.Recommendation,
                Impact:     impact));
        }

        return actions;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning)  => 1,
        _                                => 0
    };

    private static string SeverityFromOrdinal(int ordinal) => ordinal switch
    {
        2 => nameof(FindingSeverity.Critical),
        1 => nameof(FindingSeverity.Warning),
        _ => nameof(FindingSeverity.Info)
    };
}

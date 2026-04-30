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
        ReportAudience audience = ReportAudience.All)
    {
        // ── 1. Build per-analyzer sections ───────────────────────────────────
        List<AnalyzerDetailSection> analyzerSections = BuildAnalyzerSections(runs, builders);
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
        ExecutiveSummaryRecord? executiveSummary = audience == ReportAudience.Executive
            ? BuildExecutiveSummary(deduped)
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
            Findings         = outputFindings,
            AnalyzerSections = analyzerSections,
            ExecutiveSummary = executiveSummary,
            DeveloperActionPlan = developerActionPlan,
            Confidence       = confidence,
            DedupDiagnostics = dedupRecord
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
            Fingerprint:    f.EffectiveFingerprint);

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
            string mergedEvidence = MergeText(existing.Evidence, f.Evidence);
            string mergedRec      = MergeText(existing.Recommendation, f.Recommendation);
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
                Fingerprint:    existing.Fingerprint);
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

            if (run.Result is MemoryLeakDomainResult ml && ml.SkippedReferenceAddresses > 0)
            {
                notes.Add(new ConfidenceNote(
                    Analyzer: run.AnalyzerName,
                    Capped: true,
                    Reason: $"Reference tracking cap hit; {ml.SkippedReferenceAddresses:N0} addresses skipped — highly-referenced-object counts may be partial."));
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

    private static ExecutiveSummaryRecord BuildExecutiveSummary(IReadOnlyList<FindingRecord> findings)
    {
        long totalManagedBytes = 0;

        int criticalCount = 0, warningCount = 0;
        for (int i = 0; i < findings.Count; i++)
        {
            int ord = SeverityOrdinal(findings[i].Severity);
            if (ord == 2) criticalCount++;
            else if (ord == 1) warningCount++;
        }

        int leakScore     = ComputeCategoryScore(findings, "Leak", "Memory");
        int gcScore       = ComputeCategoryScore(findings, "Fragmentation", "GC");
        int threadScore   = ComputeCategoryScore(findings, "Hang", "Threading", "Retention");

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
            LeakLikelihoodScore:    leakScore,
            GcPressureScore:        gcScore,
            ThreadContentionScore:  threadScore,
            TopRecommendations:     top3);
    }

    private static int ComputeCategoryScore(IReadOnlyList<FindingRecord> findings, params string[] categories)
    {
        int score = 0;
        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord f = findings[i];
            bool matches = false;
            for (int c = 0; c < categories.Length; c++)
            {
                if (f.Category.Contains(categories[c], StringComparison.OrdinalIgnoreCase))
                {
                    matches = true;
                    break;
                }
            }

            if (!matches) continue;
            score += SeverityOrdinal(f.Severity) == 2 ? 40 : 20;
        }

        return Math.Min(score, 100);
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

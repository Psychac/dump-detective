using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds and orders analyzer/spec sections into the merged section list, and
/// promotes typed contract slots (LeadFinding, Provenance) from raw run data.
/// </summary>
internal static class ReportSectionAssembler
{
    public static List<AnalyzerDetailSection> BuildAnalyzerSections(
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> builders)
    {
        var sections = new List<AnalyzerDetailSection>(runs.Count);
        var buildersByName = new Dictionary<string, IAnalyzerSectionBuilder>(builders.Count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < builders.Count; i++)
        {
            IAnalyzerSectionBuilder builder = builders[i];
            buildersByName[builder.AnalyzerName] = builder;
        }

        for (int r = 0; r < runs.Count; r++)
        {
            AnalyzerRunResult run = runs[r];
            if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
                continue;

            if (!buildersByName.TryGetValue(run.AnalyzerName, out IAnalyzerSectionBuilder? builder))
                continue;

            if (!builder.CanHandle(run.Result))
                continue;

            sections.Add(builder.Build(run.Result));
        }

        // Emit stub sections for skipped/failed runs where a builder is registered.
        // Spec: "show ⚪ Skipped — {SkipReason}" / "⚠️ Failed — {ErrorMessage}" in section header.
        for (int r = 0; r < runs.Count; r++)
        {
            AnalyzerRunResult run = runs[r];
            if (run.Status == AnalyzerExecutionStatus.Success)
                continue;

            if (!buildersByName.TryGetValue(run.AnalyzerName, out IAnalyzerSectionBuilder? builder))
                continue;

            bool isSkipped = run.Status is AnalyzerExecutionStatus.SkippedByFilter
                          or AnalyzerExecutionStatus.SkippedByCancellation;
            string statusNote = isSkipped
                ? $"⚪ Skipped — {run.SkipReason ?? "no reason provided"}"
                : $"⚠️ Failed — {run.ErrorMessage ?? "no error details"}";

            sections.Add(new AnalyzerDetailSection(
                AnalyzerName: run.AnalyzerName,
                DisplayTitle: builder.DisplayTitle,
                SortOrder:    builder.SortOrder,
                Blocks:       [new TextBlock(statusNote)],
                Provenance:   new SectionProvenance(
                    Analyzer:        run.AnalyzerName,
                    Status:          NormalizeStatus(run.Status),
                    DurationMs:      run.Duration.TotalMilliseconds,
                    ObjectScanCount: 0,
                    CacheHits:       0,
                    CacheMisses:     0)));
        }

        return sections;
    }

    public static List<AnalyzerDetailSection> BuildSpecSections(
        AnalyzerResultSet results,
        IReadOnlyList<IReportSectionBuilder> builders)
    {
        List<AnalyzerDetailSection> sections = [];

        for (int i = 0; i < builders.Count; i++)
        {
            IReportSectionBuilder builder = builders[i];
            if (!builder.CanBuild(results))
                continue;

            sections.Add(builder.Build(results));
        }

        return sections;
    }

    public static List<AnalyzerDetailSection> MergeSections(
        IReadOnlyList<AnalyzerDetailSection> analyzerSections,
        IReadOnlyList<AnalyzerDetailSection> specSections)
    {
        List<AnalyzerDetailSection> merged = new(analyzerSections.Count + specSections.Count);
        merged.AddRange(specSections);
        merged.AddRange(analyzerSections);
        return merged;
    }

    /// <summary>
    /// Annotates each section with Domain, SectionId, and LeadSeverity using
    /// <see cref="SectionIdDomainMap"/>. Mutates the list in-place.
    /// </summary>
    public static void ApplySectionMetadata(List<AnalyzerDetailSection> sections, IReadOnlyList<AnalyzerRunResult> runs)
    {
        // Build analyzerName → max finding severity
        var severityByAnalyzer = new Dictionary<string, FindingSeverity>(runs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (AnalyzerRunResult run in runs)
        {
            FindingSeverity maxSev = FindingSeverity.Info;
            foreach (InsightFinding f in run.Findings)
            {
                if (f.Severity > maxSev) maxSev = f.Severity;
            }
            severityByAnalyzer[run.AnalyzerName] = maxSev;
        }

        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];
            if (!SectionIdDomainMap.TryGet(section.AnalyzerName, out string domain, out string sectionId))
                continue;

            severityByAnalyzer.TryGetValue(section.AnalyzerName, out FindingSeverity leadSev);
            FindingSeverity? lead = leadSev == FindingSeverity.Info && section.LeadSeverity is null ? null : leadSev;

            sections[i] = section with
            {
                Domain      = domain,
                SectionId   = sectionId,
                LeadSeverity = lead,
            };
        }
    }

    /// <summary>
    /// Re-sorts the merged section list by domain priority then lead severity.
    /// Sections without a domain preserve their relative order at the end.
    /// </summary>
    public static void ApplyDomainOrdering(List<AnalyzerDetailSection> sections)
    {
        sections.Sort(static (a, b) =>
        {
            int domA = ReportDomainProjector.DomainOrder(a.Domain);
            int domB = ReportDomainProjector.DomainOrder(b.Domain);
            if (domA != domB) return domA.CompareTo(domB);

            int sevA = ReportDomainProjector.LeadSeverityOrder(a.LeadSeverity);
            int sevB = ReportDomainProjector.LeadSeverityOrder(b.LeadSeverity);
            if (sevA != sevB) return sevA.CompareTo(sevB);

            return a.SortOrder.CompareTo(b.SortOrder);
        });
    }

    /// <summary>
    /// Extracts typed contract slots from each section's raw block stream and populates
    /// <see cref="AnalyzerDetailSection.LeadFinding"/>, <see cref="AnalyzerDetailSection.KeyMetrics"/>,
    /// <see cref="AnalyzerDetailSection.CompactTables"/>, and <see cref="AnalyzerDetailSection.Provenance"/>.
    /// MetricBlocks and TableBlocks are removed from <c>Blocks</c> once promoted.
    /// Runs that have no matching analyzer run still get metric/table extraction from blocks.
    /// For cross-cutting <see cref="IReportSectionBuilder"/> sections, provenance is built from all
    /// contributing analyzer runs listed in <see cref="IReportSectionBuilder.SourceAnalyzers"/>.
    /// </summary>
    public static void NormalizeSectionContractSlots(
        List<AnalyzerDetailSection> sections,
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IReportSectionBuilder>? reportBuilders = null)
    {
        // Build analyzerName → run for O(1) lookup
        var runMap = new Dictionary<string, AnalyzerRunResult>(runs.Count, StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < runs.Count; r++)
            runMap[runs[r].AnalyzerName] = runs[r];

        // Build sectionAnalyzerName → SourceAnalyzers[] for cross-cutting sections
        var sourceAnalyzerMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (reportBuilders is not null)
        {
            for (int b = 0; b < reportBuilders.Count; b++)
            {
                IReportSectionBuilder rb = reportBuilders[b];
                if (rb.SourceAnalyzers.Count > 0)
                    sourceAnalyzerMap[rb.DisplayTitle] = rb.SourceAnalyzers;
            }
        }

        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];

            // ── Build LeadFinding from the top finding of the matching run ────
            // Blocks are intentionally left unchanged — MetricBlocks and TableBlocks
            // remain in the narrative stream where renderBlocks() handles them in context.
            // KeyMetrics and Tables typed slots are reserved for builders that explicitly
            // populate them; auto-extraction from blocks would destroy narrative ordering.
            SectionLeadFinding? leadFinding = section.LeadFinding;
            AnalyzerRunResult? matchedRun = null;
            runMap.TryGetValue(section.AnalyzerName, out matchedRun);

            if (leadFinding is null && matchedRun is { } run && run.Findings.Count > 0)
            {
                InsightFinding? top = null;
                for (int f = 0; f < run.Findings.Count; f++)
                {
                    InsightFinding finding = run.Findings[f];
                    if (top is null || finding.Severity > top.Severity)
                        top = finding;
                }

                if (top is not null)
                {
                    double score = top.EffectiveConfidenceScore;
                    // Bands: High ≥0.8 ●●●●, Medium-High ≥0.65 ●●●○, Medium ≥0.45 ●●○○, Low <0.45 ●○○○
                    string symbol = score >= 0.85 ? "●●●●"
                                  : score >= 0.65 ? "●●●○"
                                  : score >= 0.45 ? "●●○○"
                                  : "●○○○";

                    leadFinding = new SectionLeadFinding(
                        Severity:          top.Severity.ToString(),
                        Title:             top.Title,
                        Summary:           top.Evidence,
                        Recommendation:    top.Recommendation,
                        ConfidenceSymbol:  symbol,
                        ConfidenceScore:   score,
                        Caveats:           top.EffectiveCaveats);
                }
            }

            // ── Build Provenance from run diagnostics ─────────────────────────
            SectionProvenance? provenance = section.Provenance;
            if (provenance is null && matchedRun is not null)
            {
                provenance = new SectionProvenance(
                    Analyzer:         matchedRun.AnalyzerName,
                    Status:           NormalizeStatus(matchedRun.Status),
                    DurationMs:       matchedRun.Duration.TotalMilliseconds,
                    ObjectScanCount:  matchedRun.Diagnostics?.ObjectScanCount ?? 0,
                    CacheHits:        matchedRun.Diagnostics?.CacheHits ?? 0,
                    CacheMisses:      matchedRun.Diagnostics?.CacheMisses ?? 0);
            }
            else if (provenance is null && sourceAnalyzerMap.TryGetValue(section.AnalyzerName, out IReadOnlyList<string>? sourceNames))
            {
                // Cross-cutting section: aggregate provenance from all contributing runs
                double totalMs = 0;
                long totalScans = 0, totalHits = 0, totalMisses = 0;
                bool allSuccess = true;
                var cappingNotes = new List<string>();
                int matched = 0;

                for (int s = 0; s < sourceNames.Count; s++)
                {
                    if (!runMap.TryGetValue(sourceNames[s], out AnalyzerRunResult? sourceRun))
                        continue;

                    matched++;
                    totalMs     += sourceRun.Duration.TotalMilliseconds;
                    totalScans  += sourceRun.Diagnostics?.ObjectScanCount ?? 0;
                    totalHits   += sourceRun.Diagnostics?.CacheHits ?? 0;
                    totalMisses += sourceRun.Diagnostics?.CacheMisses ?? 0;
                    if (sourceRun.Status != AnalyzerExecutionStatus.Success)
                        allSuccess = false;
                }

                if (matched > 0)
                {
                    provenance = new SectionProvenance(
                        Analyzer:        section.AnalyzerName,
                        Status:          allSuccess ? "Success" : "Partial",
                        DurationMs:      totalMs,
                        ObjectScanCount: totalScans,
                        CacheHits:       totalHits,
                        CacheMisses:     totalMisses,
                        CappingNotes:    cappingNotes.Count > 0 ? cappingNotes : null);
                }
            }

            // ── Write back only if something changed ──────────────────────────
            bool changed = leadFinding is not null && section.LeadFinding is null
                        || provenance is not null && section.Provenance is null;

            if (changed)
            {
                sections[i] = section with
                {
                    LeadFinding = leadFinding,
                    Provenance  = provenance,
                };
            }
        }
    }

    public static ReportAppendix BuildAppendix(IReadOnlyList<AnalyzerRunResult> runs)
    {
        var memoryDiagnostics = new List<AnalyzerMemoryDiagnosticRecord>();
        var limitations = new List<string>
        {
            // Z3 canonical list from SingleDumpReportFormat.md
            "Retained size is bounded BFS, not a true dominator tree (affects A3, A4).",
            "GC root retained bytes are estimated from average type size, not exact measurement (A5).",
            "Allocation sites are unavailable from .dmp files; ETW capture is required (B2).",
            "Gen byte counts are approximated as avg-size × gen count, not measured per-object (B1).",
            "Task orphan detection relies on CLR private field name stability across runtime versions (E1).",
            "FOH/POH sizes include runtime-internal objects that are not application objects (B3).",
            "ClrThread.StackBase/StackLimit may be 0 for GC and finalizer threads (D1).",
            "Deadlock detection is a heuristic based on held sync blocks and top-frame wait patterns, not a verified wait-for graph; ClrMD does not expose per-thread blocking-object data (D3).",
            "String encoding waste (UTF-16 overhead vs ASCII content) is not detected (A7).",
            "Async state machine state-value distribution is unavailable; only averages are reported (E2).",
            "Collection generation field is not yet available from ClrMD; generation breakdown for collections is omitted (C3).",
            "Gen0/Gen1 pinned object generation correlation is not computed (B7).",
            "RuntimeQueueLength is obtained via reflection probe and may be null when inaccessible (D2).",
        };

        var runSummary = new List<AnalyzerRunStatusRecord>(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            AnalyzerRunResult run = runs[i];
            runSummary.Add(new AnalyzerRunStatusRecord(
                AnalyzerName:          run.AnalyzerName,
                Status:                NormalizeStatus(run.Status),
                DurationMs:            run.Duration.TotalMilliseconds,
                FindingCount:          run.FindingCount,
                WarningCount:          run.WarningCount,
                ObjectScanCount:       run.ObjectScanCount,
                CacheHits:             run.CacheHits,
                CacheMisses:           run.CacheMisses,
                ErrorMessage:          run.ErrorMessage,
                FindingGeneratorError: run.FindingGeneratorError,
                SkipReason:            run.SkipReason));

            if (run.MemoryStats is null)
                continue;

            AnalyzerMemoryStats s = run.MemoryStats;
            memoryDiagnostics.Add(new AnalyzerMemoryDiagnosticRecord(
                AnalyzerName:      run.AnalyzerName,
                WorkingSetBefore:  s.WorkingSetBefore,
                WorkingSetAfter:   s.WorkingSetAfter,
                ManagedHeapBefore: s.ManagedHeapBefore,
                ManagedHeapAfter:  s.ManagedHeapAfter));
        }

        return new ReportAppendix(
            AnalyzerRunSummary: runSummary,
            MemoryDiagnostics: memoryDiagnostics.Count > 0 ? memoryDiagnostics : null,
            KnownLimitations: limitations);
    }

    public static string NormalizeStatus(AnalyzerExecutionStatus status) => status switch
    {
        AnalyzerExecutionStatus.Success => "Completed",
        AnalyzerExecutionStatus.Failed => "Failed",
        AnalyzerExecutionStatus.SkippedByFilter => "Skipped",
        AnalyzerExecutionStatus.SkippedByCancellation => "Skipped",
        _ => status.ToString()
    };
}

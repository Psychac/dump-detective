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
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        ReportAudience audience = ReportAudience.All,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<InsightFinding>? additionalFindings = null)
    {
        // ── 1. Build per-analyzer sections ───────────────────────────────────
        List<AnalyzerDetailSection> analyzerSections = BuildAnalyzerSections(runs, analyzerBuilders);
        AnalyzerResultSet resultSet = new(runs, incidentContext, additionalFindings);
        List<AnalyzerDetailSection> specSections = BuildSpecSections(resultSet, reportBuilders);
        analyzerSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        specSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        List<AnalyzerDetailSection> mergedSections = MergeSections(analyzerSections, specSections);

        // Annotate sections with Domain / SectionId / LeadSeverity via SectionIdDomainMap
        ApplySectionMetadata(mergedSections, runs);

        // Extract typed contract slots (LeadFinding, KeyMetrics, Tables, Provenance) from block stream
        NormalizeSectionContractSlots(mergedSections, runs, reportBuilders);

        // Apply domain-priority ordering (Critical domains first, then by domain priority, then SortOrder)
        ApplyDomainOrdering(mergedSections);

        // ── 2. Map all findings to FindingRecord + collect pipeline failures ──
        List<FindingRecord> allFindings = [];
        

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Findings is { Count: > 0 })
            {
                foreach (InsightFinding finding in run.Findings)
                {
                    allFindings.Add(MapFinding(finding, run.Artifacts));
                }
            }

            if (run.Status == AnalyzerExecutionStatus.Failed)
            {
                
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

        if (additionalFindings is { Count: > 0 })
        {
            foreach (InsightFinding finding in additionalFindings)
                allFindings.Add(MapFinding(finding));
        }

        // ── 3. (no dedup) Use collected findings as-is
        List<FindingRecord> deduped = allFindings;

        IReadOnlyList<ReportDomainSection> domains = BuildDomainSections(mergedSections, deduped);
        IReadOnlyList<FindingRecord> crossDomainInsights = BuildCrossDomainInsights(deduped);
        ReportAppendix appendix = BuildAppendix(runs);

        // Sort: Critical → Warning → Info, then by Category, then by Title
        deduped.Sort(static (a, b) =>
        {
            int severityCompare = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (severityCompare != 0) return severityCompare;
            int catCompare = StringComparer.Ordinal.Compare(a.Category, b.Category);
            if (catCompare != 0) return catCompare;
            return StringComparer.Ordinal.Compare(a.Title, b.Title);
        });

        // dedup diagnostics removed

        // ── 4. Audience-specific projections ─────────────────────────────────
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
        HealthScorecard scorecard = HealthScorecardBuilder.Build(runs);

        ExecutiveSummaryRecord? executiveSummary = (audience == ReportAudience.Executive || audience == ReportAudience.All)
            ? BuildExecutiveSummary(deduped, totalManagedBytes, scorecard, runs)
            : null;

        string? analyzerVersion = typeof(ReportSerializer).Assembly.GetName().Version?.ToString(3);

        return new SingleDumpReportDocument
        {
            DumpPath = dumpPath,
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = elapsed.TotalSeconds,
            AnalyzerVersion = analyzerVersion,
            IncidentContext = incidentContext,
            HealthScorecard = scorecard,
            Domains = domains,
            CrossDomainInsights = crossDomainInsights,
            Appendix = appendix,
            ExecutiveSummary = executiveSummary,
        };
    }

    public IReadOnlyList<AnalyzerDetailSection> SerializeSectionsOnly(
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null)
    {
        List<AnalyzerDetailSection> analyzerSections = BuildAnalyzerSections(runs, analyzerBuilders);
        AnalyzerResultSet resultSet = new(runs, incidentContext);
        List<AnalyzerDetailSection> specSections = BuildSpecSections(resultSet, reportBuilders);
        analyzerSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        specSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return MergeSections(analyzerSections, specSections);
    }

    // ── Section routing ───────────────────────────────────────────────────────

    private static List<AnalyzerDetailSection> BuildAnalyzerSections(
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

    private static List<AnalyzerDetailSection> BuildSpecSections(
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

    private static List<AnalyzerDetailSection> MergeSections(
        IReadOnlyList<AnalyzerDetailSection> analyzerSections,
        IReadOnlyList<AnalyzerDetailSection> specSections)
    {
        List<AnalyzerDetailSection> merged = new(analyzerSections.Count + specSections.Count);
        merged.AddRange(specSections);
        merged.AddRange(analyzerSections);
        return merged;
    }

    // ── Section metadata + ordering ───────────────────────────────────────────

    /// <summary>
    /// Annotates each section with Domain, SectionId, and LeadSeverity using
    /// <see cref="SectionIdDomainMap"/>. Mutates the list in-place.
    /// </summary>
    private static void ApplySectionMetadata(List<AnalyzerDetailSection> sections, IReadOnlyList<AnalyzerRunResult> runs)
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
    private static void ApplyDomainOrdering(List<AnalyzerDetailSection> sections)
    {
        sections.Sort(static (a, b) =>
        {
            int domA = DomainOrder(a.Domain);
            int domB = DomainOrder(b.Domain);
            if (domA != domB) return domA.CompareTo(domB);

            int sevA = LeadSeverityOrder(a.LeadSeverity);
            int sevB = LeadSeverityOrder(b.LeadSeverity);
            if (sevA != sevB) return sevA.CompareTo(sevB);

            return a.SortOrder.CompareTo(b.SortOrder);
        });
    }

    private static int DomainOrder(string domain) => domain switch
    {
        "Leaks"      => 0,
        "Memory"     => 1,
        "GC"         => 2,
        "TypeSystem" => 3,   // Domain C — before Threads (D) per spec A/B/C/D/E/F/G order
        "Threads"    => 4,
        "Async"      => 5,
        "Exceptions" => 6,
        "Runtime"    => 7,
        _            => 99   // unmapped / cross-cutting sections go last
    };

    private static int LeadSeverityOrder(FindingSeverity? s) => s switch
    {
        FindingSeverity.Critical => 0,
        FindingSeverity.Warning  => 1,
        FindingSeverity.Info     => 2,
        null                     => 3,
        _                        => 3
    };

    private static IReadOnlyList<ReportDomainSection> BuildDomainSections(
        IReadOnlyList<AnalyzerDetailSection> sections,
        IReadOnlyList<FindingRecord> findings)
    {
        var domainOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupedSections = new Dictionary<string, List<AnalyzerDetailSection>>(StringComparer.OrdinalIgnoreCase);
        var domainInsights = new Dictionary<string, List<FindingRecord>>(StringComparer.OrdinalIgnoreCase);
        var domainSeverity = new Dictionary<string, FindingSeverity?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];
            if (string.IsNullOrWhiteSpace(section.Domain) || string.Equals(section.Domain, "CrossDomain", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!groupedSections.TryGetValue(section.Domain, out List<AnalyzerDetailSection>? list))
            {
                list = [];
                groupedSections[section.Domain] = list;
                domainOrder[section.Domain] = DomainOrder(section.Domain);
            }

            list.Add(section);
            if (!domainSeverity.TryGetValue(section.Domain, out FindingSeverity? current) || LeadSeverityOrder(section.LeadSeverity) < LeadSeverityOrder(current))
                domainSeverity[section.Domain] = section.LeadSeverity;
        }

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            if (finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase)))
                continue;

            string domain = InferFindingDomain(finding);
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            if (!domainInsights.TryGetValue(domain, out List<FindingRecord>? list))
            {
                list = [];
                domainInsights[domain] = list;
                if (!domainOrder.ContainsKey(domain))
                    domainOrder[domain] = DomainOrder(domain);
            }

            list.Add(finding);
        }

        var domains = new List<ReportDomainSection>(groupedSections.Count);
        foreach (var pair in groupedSections)
        {
            string domain = pair.Key;
            domains.Add(new ReportDomainSection(
                Domain: domain,
                LeadSeverity: domainSeverity.TryGetValue(domain, out FindingSeverity? severity) ? severity : null,
                Sections: pair.Value,
                DomainInsights: domainInsights.TryGetValue(domain, out List<FindingRecord>? insights) ? insights : []));
        }

        domains.Sort((a, b) =>
        {
            // Spec: "Domains ordered by MaxSeverityInDomain descending" — severity first
            int sevA = LeadSeverityOrder(a.LeadSeverity);
            int sevB = LeadSeverityOrder(b.LeadSeverity);
            if (sevA != sevB) return sevA.CompareTo(sevB);

            // Within equal severity, use the canonical domain priority order as a tiebreaker
            int orderA = domainOrder.TryGetValue(a.Domain, out int oa) ? oa : 99;
            int orderB = domainOrder.TryGetValue(b.Domain, out int ob) ? ob : 99;
            if (orderA != orderB) return orderA.CompareTo(orderB);

            return StringComparer.OrdinalIgnoreCase.Compare(a.Domain, b.Domain);
        });

        return domains;
    }

    private static IReadOnlyList<FindingRecord> BuildCrossDomainInsights(IReadOnlyList<FindingRecord> findings)
    {
        var cross = new List<FindingRecord>();
        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            if (finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase))
                || string.Equals(finding.Analyzer, "InsightEngine", StringComparison.OrdinalIgnoreCase))
            {
                cross.Add(finding);
            }
        }
        return cross;
    }

    private static string InferFindingDomain(FindingRecord finding)
    {
        string analyzer = finding.Analyzer;
        return analyzer switch
        {
            "LeakCandidateAnalyzer" => "Leaks",
            "MemoryAnalyzer" => "Memory",
            "DominatorAnalyzer" => "Memory",
            "RetentionAnalyzer" => "Memory",
            "GCRootAnalyzer" => "Memory",
            "StaticRootLeakDetector" => "Memory",
            "StringAnalyzer" => "Memory",
            "GCGenerationAnalyzer" => "GC",
            "AllocationPatternAnalyzer" => "GC",
            "SegmentAnalyzer" => "GC",
            "LohFragmentationAnalyzer" => "GC",
            "SegmentReservationAnalyzer" => "GC",
            "FinalizableObjectAnalyzer" => "GC",
            "GCHandleAnalyzer" => "GC",
            "WeakReferenceAnalyzer" => "GC",
            "DependentHandleAnalyzer" => "GC",
            "ObjectShapeAnalyzer" => "TypeSystem",
            "CollectionAnalyzer" => "TypeSystem",
            "ArrayAnalyzer" => "TypeSystem",
            "BoxingAnalyzer" => "TypeSystem",
            "ThreadAnalyzer" => "Threads",
            "HangAnalyzer" => "Threads",
            "LockGraphAnalyzer" => "Threads",
            "ThreadStackClusterAnalyzer" => "Threads",
            "EventLeakAnalyzer" => "Threads",
            "AsyncTaskAnalyzer" => "Async",
            "AsyncStateMachineAnalyzer" => "Async",
            "CrashAnalyzer" => "Exceptions",
            "ModuleAnalyzer" => "Runtime",
            "AppDomainAnalyzer" => "Runtime",
            "JitAnalyzer" => "Runtime",
            _ => string.Empty
        };
    }

    // ── Section contract-slot normalization ───────────────────────────────────

    /// <summary>
    /// Extracts typed contract slots from each section's raw block stream and populates
    /// <see cref="AnalyzerDetailSection.LeadFinding"/>, <see cref="AnalyzerDetailSection.KeyMetrics"/>,
    /// <see cref="AnalyzerDetailSection.Tables"/>, and <see cref="AnalyzerDetailSection.Provenance"/>.
    /// MetricBlocks and TableBlocks are removed from <c>Blocks</c> once promoted.
    /// Runs that have no matching analyzer run still get metric/table extraction from blocks.
    /// For cross-cutting <see cref="IReportSectionBuilder"/> sections, provenance is built from all
    /// contributing analyzer runs listed in <see cref="IReportSectionBuilder.SourceAnalyzers"/>.
    /// </summary>
    private static void NormalizeSectionContractSlots(
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
                        Evidence:          top.Evidence,
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

    private static ReportAppendix BuildAppendix(IReadOnlyList<AnalyzerRunResult> runs)
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
            "Deadlock detection misses cooperative waits that do not appear in BlockingObjects (D3).",
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
                AnalyzerName:        run.AnalyzerName,
                WorkingSetBefore:    s.WorkingSetBefore,
                WorkingSetAfter:     s.WorkingSetAfter,
                WorkingSetDelta:     s.WorkingSetDelta,
                ManagedHeapBefore:   s.ManagedHeapBefore,
                ManagedHeapAfter:    s.ManagedHeapAfter,
                ManagedHeapDelta:    s.ManagedHeapDelta));
        }

        return new ReportAppendix(
            AnalyzerRunSummary: runSummary,
            MemoryDiagnostics: memoryDiagnostics.Count > 0 ? memoryDiagnostics : null,
            KnownLimitations: limitations);
    }

    // ── Finding mapping ───────────────────────────────────────────────────────

    private static FindingRecord MapFinding(InsightFinding f, IReadOnlyList<ReportArtifact>? artifacts = null, int? snapshotIndex = null) =>
        new(
            Analyzer: f.Analyzer,
            Category: f.Category,
            Severity: f.Severity.ToString(),
            Title: f.Title,
            Evidence: f.Evidence,
            Recommendation: f.Recommendation,
            Tags: f.Tags,
            Fingerprint: f.EffectiveFingerprint)
        {
            EvidenceItems = SplitLines(f.Evidence),
            RecommendationItems = SplitLines(f.Recommendation),
            CaveatItems = f.EffectiveCaveats.Count > 0 ? f.EffectiveCaveats : null,
            Cause = BuildCause(f),
            Effect = BuildEffect(f),
            Fix = BuildFix(f),
            ConfidenceScore = BuildConfidenceScore(f),
            EvidenceRefs = BuildEvidenceRefs(f, artifacts, snapshotIndex),
            SuggestedOwner = BuildSuggestedOwner(f),
            Effort = BuildEffort(f),
            ValidationStep = BuildValidationStep(f),
            TrackingStatus = BuildTrackingStatus(f)
        };

    private static IReadOnlyList<EvidenceRef> BuildEvidenceRefs(
        InsightFinding finding,
        IReadOnlyList<ReportArtifact>? artifacts,
        int? snapshotIndex)
    {
        string? metricKey = BuildMetricKey(finding);
        List<ReportArtifact>? matchingArtifacts = artifacts?
            .Where(a => string.Equals(a.Analyzer, finding.Analyzer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingArtifacts is { Count: > 0 })
        {
            return matchingArtifacts
                .Select(a => new EvidenceRef(
                    Analyzer: finding.Analyzer,
                    MetricKey: metricKey,
                    Addresses: null,
                    ArtifactPath: a.FilePath ?? a.FileName,
                    SnapshotIndex: snapshotIndex))
                .ToList();
        }

        return
        [
            new EvidenceRef(
                Analyzer: finding.Analyzer,
                MetricKey: metricKey,
                Addresses: null,
                ArtifactPath: null,
                SnapshotIndex: snapshotIndex)
        ];
    }

    private static string? BuildMetricKey(InsightFinding finding)
    {
        for (int i = 0; i < finding.Tags.Count; i++)
        {
            string tag = finding.Tags[i];
            if (tag.Contains('.', StringComparison.Ordinal) || tag.Contains('_', StringComparison.Ordinal))
                return tag;
        }

        return null;
    }

    // Deduplication removed: findings are not merged at serialization time

    private static string MergeText(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(b))
            return a;
        if (string.IsNullOrWhiteSpace(a))
            return b;
        return $"{a}{Environment.NewLine}{b}";
    }

    // ── Executive summary ─────────────────────────────────────────────────────

    private static ExecutiveSummaryRecord BuildExecutiveSummary(IReadOnlyList<FindingRecord> findings, long totalManagedBytes, HealthScorecard scorecard, IReadOnlyList<AnalyzerRunResult> runs)
    {
        // P1.2: Use ExplainableScoringEngine for reproducible, contributor-backed scores.
        var (leak, gcPressure, thread) = ExplainableScoringEngine.ComputeScores(findings);

        // Top Critical/Warning findings
        var criticalFindings = new List<FindingRecord>(5);
        var warningFindings = new List<FindingRecord>(5);
        var top3 = new List<FindingRecord>(3);
        for (int i = 0; i < findings.Count && top3.Count < 3; i++)
        {
            int ord = SeverityOrdinal(findings[i].Severity);
            if (ord >= 1)
                top3.Add(findings[i]);
        }

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            int ord = SeverityOrdinal(finding.Severity);
            if (ord == 2 && criticalFindings.Count < 5)
                criticalFindings.Add(finding);
            else if (ord == 1 && warningFindings.Count < 5)
                warningFindings.Add(finding);

            if (criticalFindings.Count == 5 && warningFindings.Count == 5)
                break;
        }

        return new ExecutiveSummaryRecord(
            TotalManagedBytes: totalManagedBytes,
            LeakLikelihoodScore: leak.Score,
            GcPressureScore: gcPressure.Score,
            ThreadContentionScore: thread.Score,
            TopRecommendations: top3)
        {
            HealthScorecard = scorecard,
            CriticalFindings = criticalFindings,
            WarningFindings = warningFindings,
            ScoreBreakdowns = [leak, gcPressure, thread],
            LohBytes = ExtractLohBytes(runs),
            LohPercent = ExtractLohPercent(runs),
            Gen2Percent = ExtractGen2Percent(runs),
            LeakCandidateCount = ExtractLeakCandidateCount(runs),
            HangScore = ExtractHangScore(runs),
            BlockedThreads = ExtractBlockedThreads(runs),
            DeadlockCycles = ExtractDeadlockCycles(runs),
            ActiveExceptions = ExtractActiveExceptions(runs),
            FinalizerQueueCount = ExtractFinalizerQueueCount(runs),
            TotalObjects = ExtractTotalObjects(runs),
            UniqueTypes = ExtractUniqueTypes(runs),
            GcPressureLevel = ExtractGcPressureLevel(runs),
        };
    }

    private static long? ExtractLohBytes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.MemoryDomainResult mem) return (long)mem.LohBytes;
            if (run.Result is DumpDetective.Analysis.Models.GCGenerationDomainResult gc) return (long)gc.LohBytes;
        }
        return null;
    }

    private static double? ExtractLohPercent(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.MemoryDomainResult mem) return mem.LohPercent;
            if (run.Result is DumpDetective.Analysis.Models.GCGenerationDomainResult gc) return gc.LohPercent;
        }
        return null;
    }

    private static double? ExtractGen2Percent(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.GCGenerationDomainResult gc) return gc.Gen2Pct;
        }
        return null;
    }

    private static int? ExtractLeakCandidateCount(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.LeakCandidateDomainResult leak) return leak.TotalCandidates;
        }
        return null;
    }

    private static int? ExtractHangScore(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.HangDomainResult hang) return hang.HealthScore;
        }
        return null;
    }

    private static int? ExtractBlockedThreads(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.ThreadDomainResult thread) return thread.BlockedThreadCount;
        }
        return null;
    }

    private static int? ExtractDeadlockCycles(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.LockGraphDomainResult lockGraph) return lockGraph.DeadlockCandidateCount;
        }
        return null;
    }

    private static int? ExtractActiveExceptions(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.CrashDomainResult crash) return crash.ActiveExceptions;
        }
        return null;
    }

    private static int? ExtractFinalizerQueueCount(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.FinalizableObjectDomainResult fin) return fin.FinalizerQueueCount;
        }
        return null;
    }

    private static int? ExtractTotalObjects(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.MemoryDomainResult mem) return mem.TotalObjects;
            if (run.Result is DumpDetective.Analysis.Models.GCGenerationDomainResult gc) return gc.TotalObjects;
        }
        return null;
    }

    private static int? ExtractUniqueTypes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.MemoryDomainResult mem) return mem.UniqueTypes;
        }
        return null;
    }

    private static string? ExtractGcPressureLevel(IReadOnlyList<AnalyzerRunResult> runs)
    {
        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Result is DumpDetective.Analysis.Models.AllocationPatternDomainResult alloc)
                return alloc.GCPressure.ToString();
        }
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0
    };

    private static string SeverityFromOrdinal(int ordinal) => ordinal switch
    {
        2 => nameof(FindingSeverity.Critical),
        1 => nameof(FindingSeverity.Warning),
        _ => nameof(FindingSeverity.Info)
    };

    private static IReadOnlyList<string>? SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] parts = text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static string BuildCause(InsightFinding finding)
    {
        string analyzer = string.IsNullOrWhiteSpace(finding.Analyzer) ? "Analyzer" : finding.Analyzer;
        string category = string.IsNullOrWhiteSpace(finding.Category) ? "the signal" : finding.Category;

        return finding.Severity switch
        {
            FindingSeverity.Critical => $"{analyzer} produced a Critical signal in {category}; the underlying pattern is large enough to affect runtime behavior.",
            FindingSeverity.Warning => $"{analyzer} produced a Warning in {category}; the pattern is present and trending toward a production issue.",
            _ => $"{analyzer} produced a lower-severity signal in {category}."
        };
    }

    private static string BuildEffect(InsightFinding finding)
    {
        return finding.Severity switch
        {
            FindingSeverity.Critical => $"Expected effect: {finding.Title} can increase memory, latency, or failure risk immediately if the path continues.",
            FindingSeverity.Warning => $"Expected effect: {finding.Title} can become user-visible if the same pattern grows or repeats.",
            _ => $"Expected effect: {finding.Title} is informational but still worth reviewing."
        };
    }

    private static string BuildFix(InsightFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Recommendation))
            return finding.Recommendation;

        return finding.Severity switch
        {
            FindingSeverity.Critical => "Remove the retention source, re-run the analyzer, and confirm the signal drops.",
            FindingSeverity.Warning => "Add a guardrail or bounded cap, then verify the trend no longer worsens.",
            _ => "Review the analyzer output and decide whether follow-up is needed."
        };
    }

    private static double BuildConfidenceScore(InsightFinding finding) => finding.EffectiveConfidenceScore;

    private static string BuildSuggestedOwner(InsightFinding finding) => finding.Category switch
    {
        var c when c.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                   c.Contains("Leak", StringComparison.OrdinalIgnoreCase) ||
                   c.Contains("Retention", StringComparison.OrdinalIgnoreCase) => "Platform / Service Owner",
        var c when c.Contains("Thread", StringComparison.OrdinalIgnoreCase) ||
                   c.Contains("Hang", StringComparison.OrdinalIgnoreCase) ||
                   c.Contains("Concurrency", StringComparison.OrdinalIgnoreCase) => "Runtime / Service Owner",
        var c when c.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                   c.Contains("Exception", StringComparison.OrdinalIgnoreCase) => "Application Owner",
        _ => "Investigation Owner"
    };

    private static string BuildEffort(InsightFinding finding) => finding.Severity switch
    {
        FindingSeverity.Critical => "High",
        FindingSeverity.Warning => "Medium",
        _ => "Low"
    };

    private static string BuildValidationStep(InsightFinding finding) => finding.Severity switch
    {
        FindingSeverity.Critical => "Re-run the dump after the fix and confirm the finding disappears or drops sharply.",
        FindingSeverity.Warning => "Verify the trend or cap value after the change and confirm the signal stops growing.",
        _ => "Confirm whether the signal is expected for this workload."
    };

    private static string BuildTrackingStatus(InsightFinding finding) => finding.Severity switch
    {
        FindingSeverity.Critical => "Untracked",
        FindingSeverity.Warning => "InProgress",
        _ => "Review"
    };

    private static string NormalizeStatus(AnalyzerExecutionStatus status) => status switch
    {
        AnalyzerExecutionStatus.Success                 => "Completed",
        AnalyzerExecutionStatus.Failed                  => "Failed",
        AnalyzerExecutionStatus.SkippedByFilter         => "Skipped",
        AnalyzerExecutionStatus.SkippedByCancellation   => "Skipped",
        _                                               => status.ToString()
    };
}

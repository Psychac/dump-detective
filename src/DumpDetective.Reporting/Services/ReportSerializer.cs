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
internal sealed class ReportSerializer(ExecutiveSummaryProjector? executiveSummaryProjector = null)
{
    private readonly ExecutiveSummaryProjector _executiveSummaryProjector = executiveSummaryProjector ?? new ExecutiveSummaryProjector();

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
                string evidence = run.ErrorMessage ?? "Analyzer failed without error details.";
                allFindings.Add(new FindingRecord(
                    Id: $"analyzer-failure:{run.AnalyzerName}",
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Analyzer failed: {run.AnalyzerName}",
                    Details: [evidence],
                    Recommendation: "Inspect analyzer failure details and re-run analysis.",
                    Tags: []));
            }

            if (!string.IsNullOrWhiteSpace(run.FindingGeneratorError))
            {
                string evidence = $"The finding generator for '{run.AnalyzerName}' threw an exception. Findings for this analyzer may be incomplete or missing.";
                allFindings.Add(new FindingRecord(
                    Id: $"finding-generator-error:{run.AnalyzerName}",
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Finding generator failed: {run.AnalyzerName}",
                    Details: [evidence],
                    Recommendation: "Re-run analysis. If the error persists, report it with the full error details.",
                    Tags: []));
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
        IReadOnlyList<CorrelationEventRecord> correlationEvents = BuildCorrelationEvents(deduped);
        ReportAppendix appendix = BuildAppendix(runs);

        // Sort: Critical → Warning → Info, then by Category, then by Title
        deduped.Sort(static (a, b) =>
        {
            int severityCompare = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (severityCompare != 0) return severityCompare;
            int catCompare = StringComparer.Ordinal.Compare(NormalizeSortKey(a.Category), NormalizeSortKey(b.Category));
            if (catCompare != 0) return catCompare;
            return StringComparer.Ordinal.Compare(NormalizeSortKey(a.Title), NormalizeSortKey(b.Title));
        });

        // dedup diagnostics removed

        // ── 4. Audience-specific projections ─────────────────────────────────
        long totalManagedBytes = _executiveSummaryProjector.ComputeTotalManagedBytes(runs);

        // Include Executive summary for explicit Executive audience or when Audience==All
        HealthScorecard scorecard = HealthScorecardBuilder.Build(runs);

        ExecutiveSummaryRecord? executiveSummary = (audience == ReportAudience.Executive || audience == ReportAudience.All)
            ? _executiveSummaryProjector.Build(deduped, scorecard, runs, totalManagedBytes)
            : null;

        string? analyzerVersion = typeof(ReportSerializer).Assembly.GetName().Version?.ToString(3);

        return new SingleDumpReportDocument
        {
            DumpPath = dumpPath,
            ScoringModelVersion = ActionPriorityService.ScoringModelVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = elapsed.TotalSeconds,
            AnalyzerVersion = analyzerVersion,
            IncidentContext = incidentContext,
            HealthScorecard = scorecard,
            Domains = domains,
            CrossDomainInsights = crossDomainInsights,
            CorrelationEvents = correlationEvents,
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
        List<AnalyzerDetailSection> mergedSections = MergeSections(analyzerSections, specSections);

        // Keep section shape parity with full report serialization.
        ApplySectionMetadata(mergedSections, runs);
        NormalizeSectionContractSlots(mergedSections, runs, reportBuilders);
        ApplyDomainOrdering(mergedSections);

        return mergedSections;
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
        "Async"          => 5,
        "Exceptions"     => 6,
        "Runtime"        => 7,
        "Infrastructure" => 8,
        _                => 99   // unmapped / cross-cutting sections go last
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
            if (ShouldSuppressInfoInsight(finding))
                continue;

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
            List<FindingRecord> sortedInsights = [];
            if (domainInsights.TryGetValue(domain, out List<FindingRecord>? insights) && insights is not null)
            {
                sortedInsights = [.. insights];

                sortedInsights.Sort(static (a, b) =>
                {
                    int sev = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
                    if (sev != 0) return sev;

                    int analyzer = StringComparer.OrdinalIgnoreCase.Compare(a.Analyzer, b.Analyzer);
                    if (analyzer != 0) return analyzer;

                    return StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title);
                });
            }

            domains.Add(new ReportDomainSection(
                Domain: domain,
                LeadSeverity: domainSeverity.TryGetValue(domain, out FindingSeverity? severity) ? severity : null,
                Sections: pair.Value,
                DomainInsights: sortedInsights));
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
            if (ShouldSuppressInfoInsight(finding))
                continue;

            if (finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase))
                || string.Equals(finding.Analyzer, "InsightEngine", StringComparison.OrdinalIgnoreCase))
            {
                cross.Add(finding);
            }
        }

        cross.Sort(static (a, b) =>
        {
            int sev = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (sev != 0) return sev;

            int analyzer = StringComparer.OrdinalIgnoreCase.Compare(a.Analyzer, b.Analyzer);
            if (analyzer != 0) return analyzer;

            return StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title);
        });

        return cross;
    }

    private static IReadOnlyList<CorrelationEventRecord> BuildCorrelationEvents(IReadOnlyList<FindingRecord> findings)
    {
        var domainByFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<string, List<FindingRecord>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            string domain = InferFindingDomain(finding);
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            domainByFingerprint[finding.Id] = domain;

            HashSet<string> signalKeys = ExtractCorrelationSignalKeys(finding);
            if (signalKeys.Count == 0)
                continue;

            foreach (string signalKey in signalKeys)
            {
                if (!grouped.TryGetValue(signalKey, out List<FindingRecord>? list))
                {
                    list = [];
                    grouped[signalKey] = list;
                }

                list.Add(finding);
            }
        }

        var deduped = new Dictionary<string, CorrelationEventRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, List<FindingRecord>> pair in grouped)
        {
            string tag = pair.Key;
            List<FindingRecord> list = pair.Value;
            if (list.Count < 2)
                continue;

            if (IsKeywordSignal(tag) && list.Count < 3)
                continue;

            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int minSeverity = int.MaxValue;
            int maxSeverity = int.MinValue;
            double minConfidence = 1.0;
            double maxConfidence = 0.0;

            for (int i = 0; i < list.Count; i++)
            {
                FindingRecord finding = list[i];
                if (domainByFingerprint.TryGetValue(finding.Id, out string? domain) && !string.IsNullOrWhiteSpace(domain))
                    domains.Add(domain);
                fingerprints.Add(finding.Id);

                int sev = SeverityOrdinal(finding.Severity);
                if (sev < minSeverity) minSeverity = sev;
                if (sev > maxSeverity) maxSeverity = sev;

                double conf = finding.Confidence ?? 0.70;
                if (conf < minConfidence) minConfidence = conf;
                if (conf > maxConfidence) maxConfidence = conf;
            }

            if (domains.Count < 2)
                continue;

            bool severityConflict = (maxSeverity - minSeverity) >= 2;
            bool confidenceConflict = (maxConfidence - minConfidence) >= 0.45;
            bool isConflict = severityConflict || confidenceConflict;

            string confidence = domains.Count >= 3 ? "High" : "Medium";
            if (isConflict) confidence = "Medium";
            if (domains.Count == 2 && fingerprints.Count == 2 && !isConflict) confidence = "Medium";

            string eventType = isConflict ? "conflict" : "co-move";

            var orderedDomains = domains.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase).ToArray();
            var orderedFingerprints = fingerprints.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase).Take(6).ToArray();

            string title = BuildCorrelationTitle(eventType, [tag], orderedDomains);
            string rationale = BuildCorrelationRationale(
                eventType,
                [tag],
                orderedDomains,
                orderedFingerprints.Length,
                isConflict);

            string dedupeKey = eventType + "|" + string.Join("|", orderedDomains) + "|" + string.Join("|", orderedFingerprints);

            if (deduped.TryGetValue(dedupeKey, out CorrelationEventRecord? existing))
            {
                var mergedKeys = new HashSet<string>(existing.SignalKeys, StringComparer.OrdinalIgnoreCase);
                mergedKeys.Add(tag);

                deduped[dedupeKey] = existing with
                {
                    SignalKeys = mergedKeys.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray()
                };
                continue;
            }

            // Map textual confidence to numeric score
            double confScore = confidence.Equals("High", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.7;
            deduped[dedupeKey] = new CorrelationEventRecord(
                EventId: Guid.NewGuid().ToString("D"),
                EventType: eventType,
                Title: title,
                Rationale: rationale,
                Confidence: confScore,
                Domains: orderedDomains,
                SnapshotIndices: Array.Empty<int>(),
                SignalKeys: new[] { tag },
                SourceFingerprints: orderedFingerprints,
                PrimarySnapshotIndex: null);
        }

        List<CorrelationEventRecord> events = MergeCorrelationClusters(deduped.Values.ToList());

        events.Sort(static (a, b) =>
        {
            int typeA = a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            int typeB = b.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            int typeCmp = typeB.CompareTo(typeA);
            if (typeCmp != 0) return typeCmp;

            int confA = a.Confidence >= 0.85 ? 2 : 1;
            int confB = b.Confidence >= 0.85 ? 2 : 1;
            int confCmp = confB.CompareTo(confA);
            if (confCmp != 0) return confCmp;

            int domainCmp = b.Domains.Count.CompareTo(a.Domains.Count);
            if (domainCmp != 0) return domainCmp;

            return StringComparer.OrdinalIgnoreCase.Compare(NormalizeSortKey(a.Title), NormalizeSortKey(b.Title));
        });

        if (events.Count > 8)
            events = events.Take(8).ToList();

        return events;
    }

    private static List<CorrelationEventRecord> MergeCorrelationClusters(List<CorrelationEventRecord> input)
    {
        if (input.Count < 2)
            return input;

        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < input.Count; i++)
            {
                for (int j = i + 1; j < input.Count; j++)
                {
                    CorrelationEventRecord a = input[i];
                    CorrelationEventRecord b = input[j];
                    if (!ShouldMergeCorrelationEvents(a, b))
                        continue;

                    input[i] = MergeCorrelationEvents(a, b);
                    input.RemoveAt(j);
                    merged = true;
                    break;
                }

                if (merged)
                    break;
            }
        } while (merged);

        return input;
    }

    private static bool ShouldMergeCorrelationEvents(CorrelationEventRecord a, CorrelationEventRecord b)
    {
        if (!a.EventType.Equals(b.EventType, StringComparison.OrdinalIgnoreCase))
            return false;

        int sharedFingerprintCount = 0;
        for (int i = 0; i < a.SourceFingerprints.Count; i++)
        {
            if (b.SourceFingerprints.Contains(a.SourceFingerprints[i], StringComparer.OrdinalIgnoreCase))
                sharedFingerprintCount++;
        }
        if (sharedFingerprintCount > 0)
            return true;

        int sharedDomainCount = 0;
        for (int i = 0; i < a.Domains.Count; i++)
        {
            if (b.Domains.Contains(a.Domains[i], StringComparer.OrdinalIgnoreCase))
                sharedDomainCount++;
        }

        int sharedSignalCount = 0;
        for (int i = 0; i < a.SignalKeys.Count; i++)
        {
            if (b.SignalKeys.Contains(a.SignalKeys[i], StringComparer.OrdinalIgnoreCase))
                sharedSignalCount++;
        }

        if (sharedSignalCount > 0 && sharedDomainCount > 0)
            return true;

        bool sharedNonKeywordSignal = HasSharedSignal(a.SignalKeys, b.SignalKeys, includeKeywordSignals: false);
        if (sharedNonKeywordSignal && sharedDomainCount > 0)
            return true;

        bool sharedKeywordSignal = HasSharedSignal(a.SignalKeys, b.SignalKeys, includeKeywordSignals: true)
            && !sharedNonKeywordSignal;
        if (sharedKeywordSignal && sharedDomainCount >= 3)
            return true;

        return false;
    }

    private static bool HasSharedSignal(IReadOnlyList<string> a, IReadOnlyList<string> b, bool includeKeywordSignals)
    {
        for (int i = 0; i < a.Count; i++)
        {
            string signalA = a[i];
            if (!includeKeywordSignals && IsKeywordSignal(signalA))
                continue;

            for (int j = 0; j < b.Count; j++)
            {
                string signalB = b[j];
                if (!includeKeywordSignals && IsKeywordSignal(signalB))
                    continue;

                if (signalA.Equals(signalB, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static CorrelationEventRecord MergeCorrelationEvents(CorrelationEventRecord a, CorrelationEventRecord b)
    {
        var mergedDomains = new HashSet<string>(a.Domains, StringComparer.OrdinalIgnoreCase);
        mergedDomains.UnionWith(b.Domains);

        var mergedSignals = new HashSet<string>(a.SignalKeys, StringComparer.OrdinalIgnoreCase);
        mergedSignals.UnionWith(b.SignalKeys);

        var mergedFingerprints = new HashSet<string>(a.SourceFingerprints, StringComparer.OrdinalIgnoreCase);
        mergedFingerprints.UnionWith(b.SourceFingerprints);

        string[] orderedDomains = mergedDomains.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] orderedSignals = mergedSignals.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] orderedFingerprints = mergedFingerprints.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();

        string confidence = orderedDomains.Length >= 3 ? "High" : "Medium";
        if (a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase))
            confidence = "Medium";

        bool isConflict = a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase);
        string title = BuildCorrelationTitle(a.EventType, orderedSignals, orderedDomains);
        string rationale = BuildCorrelationRationale(
            a.EventType,
            orderedSignals,
            orderedDomains,
            orderedFingerprints.Length,
            isConflict);

        double confNum = orderedDomains.Length >= 3 ? 0.9 : 0.7;
        if (a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase))
            confNum = 0.7;

        int? primary = a.PrimarySnapshotIndex ?? b.PrimarySnapshotIndex;

        return new CorrelationEventRecord(
            EventId: Guid.NewGuid().ToString("D"),
            EventType: a.EventType,
            Title: title,
            Rationale: rationale,
            Confidence: confNum,
            Domains: orderedDomains,
            SnapshotIndices: Array.Empty<int>(),
            SignalKeys: orderedSignals,
            SourceFingerprints: orderedFingerprints,
            PrimarySnapshotIndex: primary);
    }

    private static string NormalizeCorrelationTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return string.Empty;

        return tag.Trim();
    }

    private static HashSet<string> ExtractCorrelationSignalKeys(FindingRecord finding)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (finding.Tags is { Count: > 0 })
        {
            for (int i = 0; i < finding.Tags.Count; i++)
            {
                string tag = NormalizeCorrelationTag(finding.Tags[i]);
                if (!IsCorrelationTagCandidate(tag))
                    continue;

                keys.Add("tag:" + tag.ToLowerInvariant());
            }
        }

        if (finding.Refs is { Count: > 0 })
        {
            for (int i = 0; i < finding.Refs.Count; i++)
            {
                EvidenceRef evidenceRef = finding.Refs[i];
                if (string.IsNullOrWhiteSpace(evidenceRef.MetricKey))
                    continue;

                string metricKey = NormalizeCorrelationTag(evidenceRef.MetricKey);
                if (metricKey.Length < 3)
                    continue;

                keys.Add("metric:" + metricKey.ToLowerInvariant());
            }
        }

        string text = string.Concat(
            finding.Title, " ",
            finding.GetSummaryText(), " ",
            finding.Recommendation);

        AddBridgeKeyword(keys, text, "deadlock", "kw:deadlock");
        AddBridgeKeyword(keys, text, "thread pool", "kw:thread-pool");
        AddBridgeKeyword(keys, text, "finalizer", "kw:finalizer");
        AddBridgeKeyword(keys, text, "gc handle", "kw:gc-handle");
        AddBridgeKeyword(keys, text, "pinned", "kw:pinned");
        AddBridgeKeyword(keys, text, "retention", "kw:retention");
        AddBridgeKeyword(keys, text, "fragmentation", "kw:fragmentation");
        AddBridgeKeyword(keys, text, "connection pool", "kw:connection-pool");
        AddBridgeKeyword(keys, text, "timeout", "kw:timeout");
        AddBridgeKeyword(keys, text, "latency", "kw:latency");

        return keys;
    }

    private static void AddBridgeKeyword(HashSet<string> keys, string text, string token, string signal)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(token))
            return;

        if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            keys.Add(signal);
    }

    private static bool IsCorrelationTagCandidate(string tag)
    {
        if (tag.Length < 4)
            return false;

        return !tag.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("threads", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("runtime", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("gc", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("leak", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("heap", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("allocation", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("dispose", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("exceptions", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("finalizer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeywordSignal(string signalKey)
        => signalKey.StartsWith("kw:", StringComparison.OrdinalIgnoreCase);

    private static string ToDisplaySignal(string signalKey)
    {
        if (string.IsNullOrWhiteSpace(signalKey))
            return "unknown";

        int idx = signalKey.IndexOf(':');
        string raw = idx > 0 ? signalKey[(idx + 1)..] : signalKey;
        return HumanizeSignal(raw);
    }

    private static string HumanizeSignal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        string normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Replace('-', ' ').Replace('_', ' ');

        return normalized switch
        {
            "thread pool" => "thread pool pressure",
            "gc handle" => "GC handle retention",
            "connection pool" => "connection pool pressure",
            _ => normalized
        };
    }

    private static string BuildCorrelationTitle(string eventType, IReadOnlyList<string> signalKeys, IReadOnlyList<string> domains)
    {
        List<string> subsystems = ConvertDomainsToSubsystems(domains);
        string domainSummary = BuildCompactList(subsystems, 2);
        string signalSummary = BuildCompactList(ConvertSignals(signalKeys), 1);

        string prefix = eventType.Equals("conflict", StringComparison.OrdinalIgnoreCase)
            ? "Conflicting interpretation across "
            : "Shared signal across ";

        if (!string.IsNullOrWhiteSpace(signalSummary))
            return prefix + domainSummary + ": " + signalSummary;

        return prefix + domainSummary;
    }

    private static string BuildCorrelationRationale(
        string eventType,
        IReadOnlyList<string> signalKeys,
        IReadOnlyList<string> domains,
        int findingCount,
        bool isConflict)
    {
        List<string> subsystems = ConvertDomainsToSubsystems(domains);
        string domainSummary = BuildCompactList(subsystems, 3);
        string signalSummary = BuildCompactList(ConvertSignals(signalKeys), 2);

        string rationale = "Why linked: " + domainSummary + " findings share " +
                           (string.IsNullOrWhiteSpace(signalSummary) ? "related pressure signals" : signalSummary) +
                           " across " + findingCount + " finding" + (findingCount == 1 ? string.Empty : "s") + ".";

        if (isConflict || eventType.Equals("conflict", StringComparison.OrdinalIgnoreCase))
            rationale += " Severity or confidence disagrees between domains; require verification before broad remediation.";

        return rationale;
    }

    private static List<string> ConvertDomainsToSubsystems(IReadOnlyList<string> domains)
    {
        var result = new List<string>(domains.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < domains.Count; i++)
        {
            string mapped = DomainToSubsystemLabel(domains[i]);
            if (string.IsNullOrWhiteSpace(mapped))
                continue;

            if (!seen.Add(mapped))
                continue;

            result.Add(mapped);
        }

        return result;
    }

    private static string DomainToSubsystemLabel(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return string.Empty;

        return domain.Trim() switch
        {
            "Leaks" => "memory retention",
            "Memory" => "managed heap",
            "GC" => "garbage collection",
            "TypeSystem" => "type metadata",
            "Threads" => "thread scheduling",
            "Async" => "async execution",
            "Exceptions" => "exception flow",
            "Runtime" => "runtime services",
            "Infrastructure" => "infrastructure integration",
            _ => domain.Trim().ToLowerInvariant()
        };
    }

    private static List<string> ConvertSignals(IReadOnlyList<string> signalKeys)
    {
        var result = new List<string>(signalKeys.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < signalKeys.Count; i++)
        {
            string label = ToDisplaySignal(signalKeys[i]);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (!seen.Add(label))
                continue;

            result.Add(label);
        }

        return result;
    }

    private static string BuildCompactList(IReadOnlyList<string> values, int maxInline)
    {
        if (values.Count == 0)
            return "multiple";

        if (values.Count == 1)
            return values[0];

        int inlineCount = Math.Min(maxInline, values.Count);
        if (inlineCount == 1)
            return values[0];

        if (inlineCount == 2 && values.Count == 2)
            return values[0] + " and " + values[1];

        if (values.Count <= maxInline)
        {
            string joined = string.Empty;
            for (int i = 0; i < values.Count; i++)
            {
                if (i == 0)
                    joined = values[i];
                else if (i == values.Count - 1)
                    joined += " and " + values[i];
                else
                    joined += ", " + values[i];
            }

            return joined;
        }

        return values[0] + ", " + values[1] + ", and " + (values.Count - 2) + " more";
    }

    private static string BuildLeadDedupKey(AnalyzerDetailSection section)
    {
        if (section.LeadFinding is not { } lead)
            return string.Empty;

        return BuildLeadDedupKey(section.AnalyzerName, lead.Title, lead.Summary, lead.Recommendation);
    }

    private static string BuildLeadDedupKey(FindingRecord finding)
    {
        return BuildLeadDedupKey(finding.Analyzer, finding.Title, finding.GetSummaryText(), finding.Recommendation);
    }

    private static string BuildLeadDedupKey(string analyzer, string title, string evidence, string recommendation)
    {
        return string.Concat(
            NormalizeForDedup(analyzer), "|",
            NormalizeForDedup(title), "|",
            NormalizeForDedup(evidence), "|",
            NormalizeForDedup(recommendation));
    }

    private static string NormalizeForDedup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim();
    }

    private static string InferFindingDomain(FindingRecord finding)
    {
        string domain = SectionIdDomainMap.GetDomain(finding.Analyzer);
        if (!string.IsNullOrWhiteSpace(domain))
            return domain;

        // Fallback for uncommon/custom analyzer names where category is still reliable.
        return finding.Category switch
        {
            "Leak" => "Leaks",
            "Memory" => "Memory",
            "GC" => "GC",
            "TypeSystem" => "TypeSystem",
            "Threads" => "Threads",
            "Async" => "Async",
            "Exceptions" => "Exceptions",
            "Runtime" => "Runtime",
            "Infrastructure" => "Infrastructure",
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

    private static FindingRecord MapFinding(InsightFinding f, IReadOnlyList<ReportArtifact>? artifacts = null, int? snapshotIndex = null)
    {
        IReadOnlyList<string>? details = SplitLines(f.Evidence);

        return new(
            Id: f.EffectiveFingerprint,
            Analyzer: f.Analyzer,
            Category: f.Category,
            Severity: f.Severity.ToString(),
            Title: f.Title,
            Details: details,
            Recommendation: f.Recommendation,
            Tags: f.Tags)
        {
            Confidence = BuildConfidenceScore(f),
            Refs = BuildEvidenceRefs(f, artifacts, snapshotIndex),
            Caveats = f.EffectiveCaveats.Count > 0 ? f.EffectiveCaveats : null
        };
    }

    private static IReadOnlyList<EvidenceRef> BuildEvidenceRefs(
        InsightFinding finding,
        IReadOnlyList<ReportArtifact>? artifacts,
        int? snapshotIndex)
    {
        string? metricKey = BuildMetricKey(finding);
        ReportArtifact[]? matchingArtifacts = artifacts?
            .Where(a => string.Equals(a.Analyzer, finding.Analyzer, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingArtifacts is { Length: > 0 })
        {
            return matchingArtifacts
                .Select(a => new EvidenceRef(
                    Analyzer: finding.Analyzer,
                    MetricKey: metricKey,
                    Addresses: null,
                    ArtifactPath: a.FilePath ?? a.FileName,
                    SnapshotIndex: snapshotIndex))
                .ToArray();
        }

        return new[]
        {
            new EvidenceRef(
                Analyzer: finding.Analyzer,
                MetricKey: metricKey,
                Addresses: null,
                ArtifactPath: null,
                SnapshotIndex: snapshotIndex)
        };
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0
    };

    private static string NormalizeSortKey(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool ShouldSuppressInfoInsight(FindingRecord finding)
    {
        if (!string.Equals(finding.Severity, nameof(FindingSeverity.Info), StringComparison.OrdinalIgnoreCase))
            return false;

        string category = NormalizeSortKey(finding.Category);
        return string.Equals(category, "Confidence", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Diagnostics", StringComparison.OrdinalIgnoreCase);
    }

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

    private static string BuildValidationStep(InsightFinding finding) => finding.Severity switch
    {
        FindingSeverity.Critical => "Re-run the dump after the fix and confirm the finding disappears or drops sharply.",
        FindingSeverity.Warning => "Verify the trend or cap value after the change and confirm the signal stops growing.",
        _ => "Confirm whether the signal is expected for this workload."
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

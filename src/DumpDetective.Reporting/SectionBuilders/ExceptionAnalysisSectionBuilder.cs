using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ExceptionAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    // Report-width display limits (§9.26 D5) — the analyzer emits complete, uncapped candidate and
    // instance data; these are render-layer-only slicing constants.
    private const int TopExceptionTypesToShow = 15;
    private const int TopCrashThreadCandidatesToShow = 5;
    private const int TopExceptionInstancesToShow = 25;
    private const int TopCrashBucketsToShow = 25;
    private const int TopRetentionPathsToShow = 25;

    public string AnalyzerName => "Crash Analysis";
    public string DisplayTitle => "Exception Analysis";
    public int SortOrder => 100;

    public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var crash = (CrashDomainResult)result;
        ThreadDomainResult? threads = null;
        ModuleDomainResult? modules = null;

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(0.85, ["Exception counts are taken from measured runtime objects and thread snapshots."]),
            T("Crash analysis is summarized here with active-vs-total counts, likely hotspots, and inferred trace provenance."),
        };

        SectionLeadFinding? leadFinding = null;
        if (crash.ActiveExceptions > 0)
        {
            string topType = crash.TopCrashThreadCandidates is { Count: > 0 }
                ? crash.TopCrashThreadCandidates[0].PrimaryExceptionType
                : (crash.ActiveExceptionTypeCounts.Count > 0
                    ? crash.ActiveExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).First().Key
                    : "Unknown");

            double confidenceScore = ComputeLeadFindingConfidence(crash.TopCrashThreadCandidates);
            var caveats = new List<string> { "Active exception count is based on measured runtime objects and thread snapshots." };
            string? tierSummary = SummarizeConfidenceTiers(crash.TopCrashThreadCandidates);
            if (tierSummary != null)
                caveats.Add($"Original stack trace confidence: {tierSummary}.");

            leadFinding = new SectionLeadFinding(
                Severity: "Critical",
                Title: $"Active exceptions detected ({crash.ActiveExceptions:N0} on thread stacks)",
                Summary: $"{crash.ActiveExceptions:N0} active exception(s) found on thread stacks. Primary type: {topType}.",
                Recommendation: "Investigate the crash thread candidates below; correlate with the thread section for full context.",
                ConfidenceSymbol: SymbolForScore(confidenceScore),
                ConfidenceScore: confidenceScore,
                Caveats: caveats);
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_exceptions"] = new NumericMetricValue(crash.TotalExceptions, MetricUnit.Count),
            ["active_exceptions"] = new NumericMetricValue(crash.ActiveExceptions, MetricUnit.Count),
            ["unique_exception_types"] = new NumericMetricValue(crash.ExceptionTypeCounts.Count, MetricUnit.Count),
            ["inferred_traces"] = new NumericMetricValue(crash.InferredTraceCount, MetricUnit.Count),
            ["aggregate_exceptions"] = new NumericMetricValue(crash.AggregateExceptionCount, MetricUnit.Count),
            ["exception_heap_bytes"] = new NumericMetricValue(
                crash.ExceptionHeapSizeByType is { Count: > 0 } ? crash.ExceptionHeapSizeByType.Values.Sum(v => (double)v) : 0,
                MetricUnit.Bytes),
        };

        compactTables.Add(STCompact("Exception counts",
            new[] { CH("Signal"), CH("Count","number"), CH("Notes") },
            new[] {
                R("Total exceptions", crash.TotalExceptions, "All exception objects"),
                R("Active exceptions", crash.ActiveExceptions, "Exceptions currently on threads"),
                R("Unique types", crash.ExceptionTypeCounts.Count, "Distinct exception types"),
                R("Inferred traces", crash.InferredTraceCount, "Heuristic original stack traces"),
                R("AggregateExceptions unwrapped", crash.AggregateExceptionCount, "AggregateException instances with InnerExceptions extracted"),
            }));

        // All-heap exception type counts
        if (crash.ExceptionTypeCounts.Count > 0)
        {
            var heapTypeRows = new List<TableRow>(Math.Min(crash.ExceptionTypeCounts.Count, TopExceptionTypesToShow));
            foreach (KeyValuePair<string, int> kvp in crash.ExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Take(TopExceptionTypesToShow))
                heapTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("Exception type counts (all heap)", new[] { CH("Exception Type"), CH("Count","number") }, heapTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Heap size per exception type — HeapEntry.Size summed unconditionally during the scan, so
        // exceptions retaining large strings/arrays (or deep chains) surface as heap-pressure
        // contributors even when their instance count alone looks unremarkable.
        if (crash.ExceptionHeapSizeByType is { Count: > 0 })
        {
            var sizeRows = new List<TableRow>(Math.Min(crash.ExceptionHeapSizeByType.Count, TopExceptionTypesToShow));
            foreach (KeyValuePair<string, ulong> kvp in crash.ExceptionHeapSizeByType.OrderByDescending(kvp => kvp.Value).Take(TopExceptionTypesToShow))
                sizeRows.Add(Row(Cell(kvp.Key), Cell(FormatBytes(kvp.Value), kvp.Value)));
            compactTables.Add(STCompact("Exception heap size by type",
                new[] { CH("Exception Type"), CH("Total Size","number") },
                sizeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Gen2/LOH retention paths (E-1) — a GC-root-to-object path for exception instances that
        // survived at least two collections, answering "what's still holding this alive" (static
        // field, event handler, cache entry). Found via the shared RootPathFinder/reverse-edge-
        // index infrastructure; bounded by MaxRetentionPathEnrichmentMs, so this list may be a
        // partial sample of the Gen2/LOH population, not exhaustive.
        if (crash.Gen2RetentionPaths is { Count: > 0 })
        {
            int retentionLimit = Math.Min(crash.Gen2RetentionPaths.Count, TopRetentionPathsToShow);
            var retentionRows = new List<TableRow>(retentionLimit);
            for (int i = 0; i < retentionLimit; i++)
            {
                ExceptionRetentionPath rp = crash.Gen2RetentionPaths[i];
                retentionRows.Add(Row(
                    Cell(rp.ExceptionType),
                    Cell($"0x{rp.Address:X}"),
                    Cell(rp.RootKind),
                    Cell(rp.FormattedPath),
                    Cell(rp.SearchTruncated ? "Yes" : "No")));
            }
            compactTables.Add(STCompact("Gen2/LOH exception retention paths",
                new[] { CH("Exception Type"), CH("Address"), CH("Root Kind"), CH("Retention Path"), CH("Truncated") },
                retentionRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            blocks.Add(T("Retention paths trace each Gen2/LOH exception object back to a GC root, identifying what is keeping it alive (static field, event handler, cache entry, etc.)."));
        }

        // Active exception type counts — separate table per spec
        if (crash.ActiveExceptionTypeCounts.Count > 0)
        {
            var activeTypeRows = new List<TableRow>(crash.ActiveExceptionTypeCounts.Count);
            foreach (KeyValuePair<string, int> kvp in crash.ActiveExceptionTypeCounts.OrderByDescending(kvp => kvp.Value))
                activeTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("Active exception type counts", new[] { CH("Exception Type"), CH("Active Count","number") }, activeTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // AggregateException inner exception types — the outer "AggregateException" count in the
        // heap-wide table above is near-meaningless for TPL/async code; this is the real signal.
        if (crash.AggregateInnerExceptionTypeCounts is { Count: > 0 })
        {
            var innerTypeRows = new List<TableRow>(crash.AggregateInnerExceptionTypeCounts.Count);
            foreach (KeyValuePair<string, int> kvp in crash.AggregateInnerExceptionTypeCounts.OrderByDescending(kvp => kvp.Value))
                innerTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("AggregateException inner exception types (unwrapped)",
                new[] { CH("Inner Exception Type"), CH("Count","number") },
                innerTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            blocks.Add(T($"{crash.AggregateExceptionCount:N0} AggregateException instance(s) were unwrapped; the table above attributes their InnerExceptions to the real underlying fault types."));
        }

        // Message distribution per type — a few distinct messages within a type points to a small
        // number of distinct fault sites; many distinct messages points to systemic/connectivity
        // failure. Derived from the same sampled instance set as "Exception instances" below.
        if (crash.MessageDistributions is { Count: > 0 })
        {
            var messageRows = new List<TableRow>(crash.MessageDistributions.Count);
            foreach (ExceptionMessageDistribution dist in crash.MessageDistributions)
                messageRows.Add(Row(
                    Cell(dist.Type),
                    Cell(dist.SampledInstanceCount.ToString("N0"), dist.SampledInstanceCount),
                    Cell(dist.DistinctMessageCount.ToString("N0"), dist.DistinctMessageCount),
                    Cell(dist.MostCommonMessage ?? "—"),
                    Cell(dist.MostCommonMessageCount.ToString("N0"), dist.MostCommonMessageCount),
                    Cell(dist.MostCommonActiveMessage ?? "—"),
                    Cell(dist.MostCommonActiveMessageCount.ToString("N0"), dist.MostCommonActiveMessageCount)));
            compactTables.Add(STCompact("Exception message distribution per type",
                new[] { CH("Exception Type"), CH("Sampled","number"), CH("Distinct Messages","number"), CH("Most Common Message"), CH("Count","number"), CH("Most Common Active Message"), CH("Active Count","number") },
                messageRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Crash buckets — (ExceptionType, TopUserFrame) dedup key. A single dominant bucket means a
        // systemic single-site fault; many low-count buckets sharing a type means scattered,
        // independent failures. Distinguishing these is invisible in the plain per-type counts above.
        if (crash.CrashBuckets is { Count: > 0 })
        {
            int bucketLimit = Math.Min(crash.CrashBuckets.Count, TopCrashBucketsToShow);
            var bucketRows = new List<TableRow>(bucketLimit);
            for (int i = 0; i < bucketLimit; i++)
            {
                CrashBucket bucket = crash.CrashBuckets[i];
                bucketRows.Add(Row(
                    Cell(bucket.ExceptionType),
                    Cell(bucket.TopUserFrame),
                    Cell(bucket.InstanceCount.ToString("N0"), bucket.InstanceCount),
                    Cell(bucket.ActiveInstanceCount.ToString("N0"), bucket.ActiveInstanceCount),
                    Cell($"0x{bucket.SampleAddress:X}")));
            }
            compactTables.Add(STCompact("Crash buckets (exception type + top user frame)",
                new[] { CH("Exception Type"), CH("Top User Frame"), CH("Instances","number"), CH("Active Instances","number"), CH("Sample Address") },
                bucketRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            CrashBucket topBucket = crash.CrashBuckets[0];
            int sampledTotal = crash.CrashBuckets.Sum(b => b.InstanceCount);
            blocks.Add(T(crash.CrashBuckets.Count == 1
                ? $"All {topBucket.InstanceCount:N0} sampled instance(s) share a single fault signature ({topBucket.ExceptionType} at {topBucket.TopUserFrame}) — a systemic single-site fault."
                : $"Top bucket ({topBucket.ExceptionType} at {topBucket.TopUserFrame}) accounts for {topBucket.InstanceCount:N0} of {sampledTotal:N0} sampled instances across {crash.CrashBuckets.Count:N0} distinct fault signatures."));
        }

        if (crash.TopCrashThreadCandidates is { Count: > 0 })
        {
            int candidateLimit = Math.Min(crash.TopCrashThreadCandidates.Count, TopCrashThreadCandidatesToShow);
            var hotspotRows = new List<TableRow>(candidateLimit);
            for (int i = 0; i < candidateLimit; i++)
            {
                CrashThreadCandidateSnapshot candidate = crash.TopCrashThreadCandidates[i];
                hotspotRows.Add(Row(
                    Cell(candidate.ThreadId.ToString("N0"), candidate.ThreadId),
                    Cell(candidate.OSThreadId.ToString("N0"), candidate.OSThreadId),
                    Cell(candidate.ActiveExceptionCount.ToString("N0"), candidate.ActiveExceptionCount),
                    Cell(candidate.PrimaryExceptionType),
                    Cell(candidate.OriginalStackTraceConfidence.ToString()),
                    Cell(candidate.OriginalStackTraceIsRethrown ? "Yes" : "No"),
                    Cell(candidate.OriginalStackTraceInferredFrom ?? "—"),
                    Cell(candidate.TopFrames.Count > 0 ? candidate.TopFrames[0] : "—")));
            }
            compactTables.Add(STCompact("Crash thread candidates",
                new[] { CH("Managed Thread","number"), CH("OS Thread","number"), CH("Active Exceptions","number"), CH("Primary Exception"), CH("Trace Confidence"), CH("Rethrown"), CH("Trace Source"), CH("Top Frame") },
                hotspotRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            var originRows = new List<TableRow>(candidateLimit);
            for (int i = 0; i < candidateLimit; i++)
            {
                CrashThreadCandidateSnapshot candidate = crash.TopCrashThreadCandidates[i];
                int frameworkFrames = 0; int thirdPartyFrames = 0; int userCodeFrames = 0;
                for (int j = 0; j < candidate.TopFrames.Count; j++)
                {
                    string frame = candidate.TopFrames[j];
                    switch (ClassifyFrameOrigin(frame, modules))
                    {
                        case "FrameworkCode": frameworkFrames++; break;
                        case "ThirdParty":    thirdPartyFrames++; break;
                        default:              userCodeFrames++; break;
                    }
                }
                originRows.Add(Row(
                    Cell(candidate.ThreadId.ToString("N0"), candidate.ThreadId),
                    Cell(frameworkFrames.ToString("N0"), frameworkFrames),
                    Cell(thirdPartyFrames.ToString("N0"), thirdPartyFrames),
                    Cell(userCodeFrames.ToString("N0"), userCodeFrames),
                    Cell(candidate.TopFrames.Count.ToString("N0"), candidate.TopFrames.Count)));
            }
            compactTables.Add(STCompact("Frame origin classification",
                new[] { CH("Managed Thread","number"), CH("Framework","number"), CH("ThirdParty","number"), CH("UserCode","number"), CH("Total Frames","number") },
                originRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            // Assembly attribution — the owning module of each candidate's top user-code frame,
            // resolved directly via ClrStackFrame.Method.Type.Module (not a ModuleDomainResult
            // cross-reference). Scoped to active crash threads: they're the only place the raw
            // ClrStackFrame — and therefore a real module handle — is still available; exception
            // instances elsewhere in this report only retain parsed stack-trace text.
            var moduleActiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int attributedActiveExceptions = 0;
            for (int i = 0; i < crash.TopCrashThreadCandidates.Count; i++)
            {
                CrashThreadCandidateSnapshot candidate = crash.TopCrashThreadCandidates[i];
                if (string.IsNullOrWhiteSpace(candidate.TopUserFrameModule))
                    continue;
                moduleActiveCounts.TryGetValue(candidate.TopUserFrameModule, out int count);
                moduleActiveCounts[candidate.TopUserFrameModule] = count + candidate.ActiveExceptionCount;
                attributedActiveExceptions += candidate.ActiveExceptionCount;
            }
            if (moduleActiveCounts.Count > 0)
            {
                var moduleRows = new List<TableRow>(moduleActiveCounts.Count);
                foreach (KeyValuePair<string, int> kvp in moduleActiveCounts.OrderByDescending(kvp => kvp.Value))
                {
                    double pct = attributedActiveExceptions > 0 ? 100.0 * kvp.Value / attributedActiveExceptions : 0;
                    moduleRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value), Cell($"{pct:F1}%", pct)));
                }
                compactTables.Add(STCompact("Exception attribution by assembly (active crash threads)",
                    new[] { CH("Assembly"), CH("Active Exceptions","number"), CH("% of Active Exceptions","number") },
                    moduleRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if (threads is not null && threads.TopBlockedThreads is { Count: > 0 })
                blocks.Add(T("Thread hotspots can be cross-checked against the blocked-thread tables in the thread/concurrency section."));
        }

        if (crash.TopExceptionInstances is { Count: > 0 })
        {
            int instanceLimit = Math.Min(crash.TopExceptionInstances.Count, TopExceptionInstancesToShow);
            var rows = new List<TableRow>(instanceLimit);
            for (int i = 0; i < instanceLimit; i++)
            {
                ExceptionInstanceSnapshot ex = crash.TopExceptionInstances[i];
                rows.Add(Row(
                    Cell(ex.Type),
                    Cell($"0x{ex.Address:X}"),
                    Cell(ex.Message ?? "—"),
                    Cell(ex.HResult.HasValue ? $"0x{ex.HResult.Value:X8}" : "—"),
                    Cell(ex.InnerExceptionType ?? "—"),
                    Cell(ex.ChainDepth.ToString("N0"), ex.ChainDepth),
                    Cell(ex.IsActive ? "ACTIVE" : "Inactive"),
                    Cell(ex.ThreadId.HasValue ? ex.ThreadId.Value.ToString("N0") : "—"),
                    Cell(ex.OSThreadId.HasValue ? ex.OSThreadId.Value.ToString("N0") : "—"),
                    Cell(ex.IsRethrown ? "Yes" : "No")));
            }
            compactTables.Add(STCompact("Exception instances",
                new[] { CH("Type"), CH("Address"), CH("Message"), CH("HRESULT"), CH("Inner Type"), CH("Chain Depth","number"), CH("Status"), CH("Thread","number"), CH("OS Thread","number"), CH("Rethrown") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            var depthBuckets = new Dictionary<int, int>();
            for (int i = 0; i < crash.TopExceptionInstances.Count; i++)
            {
                ExceptionInstanceSnapshot ex = crash.TopExceptionInstances[i];
                if (depthBuckets.TryGetValue(ex.ChainDepth, out int count))
                    depthBuckets[ex.ChainDepth] = count + 1;
                else
                    depthBuckets[ex.ChainDepth] = 1;
            }
            if (depthBuckets.Count > 0)
            {
                var depthRows = new List<TableRow>(depthBuckets.Count);
                foreach (KeyValuePair<int, int> kvp in depthBuckets.OrderBy(kvp => kvp.Key))
                    depthRows.Add(Row(Cell(kvp.Key.ToString("N0"), kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
                compactTables.Add(STCompact("Exception chain depth histogram", new[] { CH("Depth","number"), CH("Count","number") }, depthRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }

        blocks.Add(T(modules is null
            ? "Frame origin classification is approximate because module data was unavailable."
            : "Frames can be classified as FrameworkCode, ThirdParty, or UserCode by module prefix and module inventory."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    // Weighted (by ActiveExceptionCount) average of per-candidate tier scores, so a handful of
    // Exact-confidence threads with many active exceptions outweigh a single low-confidence
    // outlier, and vice versa. Falls back to a neutral 0.5 when there are no candidates.
    private static double ComputeLeadFindingConfidence(IReadOnlyList<CrashThreadCandidateSnapshot>? candidates)
    {
        if (candidates is not { Count: > 0 })
            return 0.5;

        double weightedScore = 0;
        double totalWeight = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            CrashThreadCandidateSnapshot candidate = candidates[i];
            double weight = Math.Max(candidate.ActiveExceptionCount, 1);
            weightedScore += ConfidenceTierScore(candidate.OriginalStackTraceConfidence) * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedScore / totalWeight : 0.5;
    }

    // Numeric anchors matching the qualitative tiers: Exact=High, ThreadId=Medium,
    // MessageHResult=Medium-Low, TypeInnerType=Low, None=no original trace found at all.
    private static double ConfidenceTierScore(InferenceConfidence confidence) => confidence switch
    {
        InferenceConfidence.Exact => 0.95,
        InferenceConfidence.ThreadId => 0.65,
        InferenceConfidence.MessageHResult => 0.5,
        InferenceConfidence.TypeInnerType => 0.3,
        InferenceConfidence.None => 0.15,
        _ => 0.5,
    };

    private static string? SummarizeConfidenceTiers(IReadOnlyList<CrashThreadCandidateSnapshot>? candidates)
    {
        if (candidates is not { Count: > 0 })
            return null;

        var tierCounts = new Dictionary<InferenceConfidence, int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            InferenceConfidence tier = candidates[i].OriginalStackTraceConfidence;
            tierCounts.TryGetValue(tier, out int count);
            tierCounts[tier] = count + 1;
        }

        return string.Join(", ", tierCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => $"{kvp.Value:N0} {kvp.Key}"));
    }

    private static string ClassifyFrameOrigin(string frame, ModuleDomainResult? modules)
    {
        if (frame.StartsWith("System.", StringComparison.Ordinal) || frame.StartsWith("Microsoft.", StringComparison.Ordinal))
            return "FrameworkCode";

        if (modules?.TopModulesBySize is { Count: > 0 })
        {
            for (int i = 0; i < modules.TopModulesBySize.Count; i++)
            {
                string moduleName = modules.TopModulesBySize[i].Name;
                if (!string.IsNullOrWhiteSpace(moduleName) && frame.IndexOf(moduleName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "ThirdParty";
            }
        }

        return "UserCode";
    }
}
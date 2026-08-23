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
            leadFinding = new SectionLeadFinding(
                Severity: "Critical",
                Title: $"Active exceptions detected ({crash.ActiveExceptions:N0} on thread stacks)",
                Summary: $"{crash.ActiveExceptions:N0} active exception(s) found on thread stacks. Primary type: {topType}.",
                Recommendation: "Investigate the crash thread candidates below; correlate with the thread section for full context.",
                ConfidenceSymbol: "●●●●",
                ConfidenceScore: 0.85,
                Caveats: ["Active exception count is based on measured runtime objects and thread snapshots."]);
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_exceptions"] = new NumericMetricValue(crash.TotalExceptions, MetricUnit.Count),
            ["active_exceptions"] = new NumericMetricValue(crash.ActiveExceptions, MetricUnit.Count),
            ["unique_exception_types"] = new NumericMetricValue(crash.ExceptionTypeCounts.Count, MetricUnit.Count),
            ["inferred_traces"] = new NumericMetricValue(crash.InferredTraceCount, MetricUnit.Count),
        };

        compactTables.Add(STCompact("Exception counts",
            new[] { CH("Signal"), CH("Count","number"), CH("Notes") },
            new[] {
                R("Total exceptions", crash.TotalExceptions, "All exception objects"),
                R("Active exceptions", crash.ActiveExceptions, "Exceptions currently on threads"),
                R("Unique types", crash.ExceptionTypeCounts.Count, "Distinct exception types"),
                R("Inferred traces", crash.InferredTraceCount, "Heuristic original stack traces"),
            }));

        // All-heap exception type counts
        if (crash.ExceptionTypeCounts.Count > 0)
        {
            var heapTypeRows = new List<TableRow>(Math.Min(crash.ExceptionTypeCounts.Count, TopExceptionTypesToShow));
            foreach (KeyValuePair<string, int> kvp in crash.ExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Take(TopExceptionTypesToShow))
                heapTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("Exception type counts (all heap)", new[] { CH("Exception Type"), CH("Count","number") }, heapTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Active exception type counts — separate table per spec
        if (crash.ActiveExceptionTypeCounts.Count > 0)
        {
            var activeTypeRows = new List<TableRow>(crash.ActiveExceptionTypeCounts.Count);
            foreach (KeyValuePair<string, int> kvp in crash.ActiveExceptionTypeCounts.OrderByDescending(kvp => kvp.Value))
                activeTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            compactTables.Add(STCompact("Active exception type counts", new[] { CH("Exception Type"), CH("Active Count","number") }, activeTypeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                    Cell(candidate.OriginalStackTraceInferredFrom ?? "—"),
                    Cell(candidate.TopFrames.Count > 0 ? candidate.TopFrames[0] : "—")));
            }
            compactTables.Add(STCompact("Crash thread candidates",
                new[] { CH("Managed Thread","number"), CH("OS Thread","number"), CH("Active Exceptions","number"), CH("Primary Exception"), CH("Trace Confidence"), CH("Trace Source"), CH("Top Frame") },
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
                    Cell(ex.OSThreadId.HasValue ? ex.OSThreadId.Value.ToString("N0") : "—")));
            }
            compactTables.Add(STCompact("Exception instances",
                new[] { CH("Type"), CH("Address"), CH("Message"), CH("HRESULT"), CH("Inner Type"), CH("Chain Depth","number"), CH("Status"), CH("Thread","number"), CH("OS Thread","number") },
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
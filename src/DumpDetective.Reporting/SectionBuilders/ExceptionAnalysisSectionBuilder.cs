using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ExceptionAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Crash Analysis";
    public string DisplayTitle => "Exception Analysis";
    public int SortOrder => 100;

    public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var crash = (CrashDomainResult)result;
        ThreadDomainResult? threads = null;
        ModuleDomainResult? modules = null;

        var tables = new List<SectionTable>();
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

        tables.Add(ST("Exception counts",
            ["Signal", "Count", "Notes"],
            [
                Row(Cell("Total exceptions"),   Cell(crash.TotalExceptions.ToString("N0"), crash.TotalExceptions),           Cell("All exception objects")),
                Row(Cell("Active exceptions"),  Cell(crash.ActiveExceptions.ToString("N0"), crash.ActiveExceptions),          Cell("Exceptions currently on threads")),
                Row(Cell("Unique types"),       Cell(crash.ExceptionTypeCounts.Count.ToString("N0"), crash.ExceptionTypeCounts.Count), Cell("Distinct exception types")),
                Row(Cell("Inferred traces"),    Cell(crash.InferredTraceCount.ToString("N0"), crash.InferredTraceCount),       Cell("Heuristic original stack traces")),
            ]));

        // All-heap exception type counts
        if (crash.ExceptionTypeCounts.Count > 0)
        {
            var heapTypeRows = new List<TableRow>(Math.Min(crash.ExceptionTypeCounts.Count, 15));
            foreach (KeyValuePair<string, int> kvp in crash.ExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Take(15))
                heapTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            tables.Add(ST("Exception type counts (all heap)", ["Exception Type", "Count"], heapTypeRows));
        }

        // Active exception type counts — separate table per spec
        if (crash.ActiveExceptionTypeCounts.Count > 0)
        {
            var activeTypeRows = new List<TableRow>(crash.ActiveExceptionTypeCounts.Count);
            foreach (KeyValuePair<string, int> kvp in crash.ActiveExceptionTypeCounts.OrderByDescending(kvp => kvp.Value))
                activeTypeRows.Add(Row(Cell(kvp.Key), Cell(kvp.Value.ToString("N0"), kvp.Value)));
            tables.Add(ST("Active exception type counts", ["Exception Type", "Active Count"], activeTypeRows));
        }

        if (crash.TopCrashThreadCandidates is { Count: > 0 })
        {
            var hotspotRows = new List<TableRow>(crash.TopCrashThreadCandidates.Count);
            for (int i = 0; i < crash.TopCrashThreadCandidates.Count; i++)
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
            tables.Add(ST("Crash thread candidates",
                ["Managed Thread", "OS Thread", "Active Exceptions", "Primary Exception", "Trace Confidence", "Trace Source", "Top Frame"],
                hotspotRows));

            var originRows = new List<TableRow>(crash.TopCrashThreadCandidates.Count);
            for (int i = 0; i < crash.TopCrashThreadCandidates.Count; i++)
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
            tables.Add(ST("Frame origin classification",
                ["Managed Thread", "Framework", "ThirdParty", "UserCode", "Total Frames"],
                originRows));

            if (threads is not null && threads.TopBlockedThreads is { Count: > 0 })
                blocks.Add(T("Thread hotspots can be cross-checked against the blocked-thread tables in the thread/concurrency section."));
        }

        if (crash.TopExceptionInstances is { Count: > 0 })
        {
            var rows = new List<TableRow>(crash.TopExceptionInstances.Count);
            for (int i = 0; i < crash.TopExceptionInstances.Count; i++)
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
            tables.Add(ST("Exception instances",
                ["Type", "Address", "Message", "HRESULT", "Inner Type", "Chain Depth", "Status", "Thread", "OS Thread"],
                rows));

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
                tables.Add(ST("Exception chain depth histogram", ["Depth", "Count"], depthRows));
            }
        }

        blocks.Add(T(modules is null
            ? "Frame origin classification is approximate because module data was unavailable."
            : "Frames can be classified as FrameworkCode, ThirdParty, or UserCode by module prefix and module inventory."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
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
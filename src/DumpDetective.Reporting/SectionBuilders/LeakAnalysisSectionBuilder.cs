using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LeakAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Leak Candidate Analysis";
    public string DisplayTitle => "Leak Candidates";
    public int SortOrder => 100;

    private const int TopCandidateCount = 30;

    public bool CanHandle(AnalyzerDomainResult result) => result is LeakCandidateDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var leak = (LeakCandidateDomainResult)result;

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(leak.HeuristicOnly ? 0.55 : 0.70, leak.HeuristicOnly
                ? new[] { "Heuristic-only leak analysis; no full retention scan." }
                : new[] { "Leak analysis is heuristic-guided; confirm with root-path review." }),
        };

        SectionLeadFinding? leadFinding = null;
        if (leak.TopCandidates.Count > 0)
        {
            LeakCandidateRecord top = leak.TopCandidates[0];
            if (top.Severity != FindingSeverity.Info)
            {
                leadFinding = new SectionLeadFinding(
                    Severity: top.Severity.ToString(),
                    Title: $"Memory leak candidate: {top.TypeName} ({top.Classification})",
                    Summary: $"Score: {top.SuspicionScore:N0}, {top.InstanceCount:N0} instances, {FormatBytes(top.TotalSize)} total. Gen2: {top.Gen2Pct:F1}%.",
                    Recommendation: "Investigate root paths in §A5 (GC Root Intelligence) to confirm retention.",
                    ConfidenceSymbol: leak.HeuristicOnly ? "●●○○" : "●●●○",
                    ConfidenceScore: leak.HeuristicOnly ? 0.55 : 0.70,
                    Caveats: leak.HeuristicOnly ? new[] { "Heuristic-only analysis; confirm with root-path review." } : Array.Empty<string>());
            }
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_candidates"] = new NumericMetricValue(leak.TotalCandidates, MetricUnit.Count),
            ["heuristic_only"] = new TextMetricValue(leak.HeuristicOnly ? "Yes" : "No"),
        };
        if (leak.TopCandidates.Count > 0)
        {
            LeakCandidateRecord top = leak.TopCandidates[0];
            keyMetrics["top_suspect"] = new TextMetricValue($"{top.TypeName} ({top.Severity})");
            keyMetrics["top_suspicion_score"] = new NumericMetricValue(top.SuspicionScore, MetricUnit.Custom, top.SuspicionScore.ToString("N0"));
        }

        if (leak.CandidatesByClass.Count > 0)
        {
            var classRows = new List<TableRow>(leak.CandidatesByClass.Count);
            foreach ((LeakClass leakClass, int count) in leak.CandidatesByClass.OrderByDescending(kvp => kvp.Value))
            {
                LeakCandidateRecord[] classCandidates = leak.TopCandidates
                    .Where(candidate => candidate.Classification == leakClass)
                    .OrderByDescending(candidate => candidate.TotalSize)
                    .ThenByDescending(candidate => candidate.SuspicionScore)
                    .ToArray();

                string topTypes = string.Join(", ", classCandidates.Take(3).Select(candidate => candidate.TypeName));
                ulong classSize = 0;
                for (int i = 0; i < classCandidates.Length; i++)
                    classSize += classCandidates[i].TotalSize;

                classRows.Add(Row(
                    Cell(leakClass.ToString()),
                    Cell(count.ToString("N0"), count),
                    Cell(FormatBytes(classSize), (long)Math.Min(classSize, long.MaxValue)),
                    Cell(string.IsNullOrWhiteSpace(topTypes) ? "—" : topTypes)));
            }
            tables.Add(ST(
                "Candidate groups by leak class",
                new[] { "Class", "Count", "Total Size", "Top Types" },
                classRows));
        }

        if (leak.TopCandidates.Count > 0)
        {
            blocks.Add(T("Top candidates are ranked by suspicion score; the report highlights likely leak patterns first and then expands the highest-signal rows below."));
                tables.Add(ST(
                "Top leak candidates by suspicion score",
                new[] { "Type", "Score", "Severity", "Class", "Total Size", "Instances", "Gen2%", "Root Kind", "Finalizable", "Container", "Ref Ratio" },
                leak.TopCandidates.Take(TopCandidateCount).Select(candidate => Row(
                    Cell(candidate.TypeName),
                    Cell(candidate.SuspicionScore.ToString("N0"), candidate.SuspicionScore),
                    Cell(candidate.Severity.ToString()),
                    Cell(candidate.Classification.ToString()),
                    Cell(FormatBytes(candidate.TotalSize), (long)Math.Min(candidate.TotalSize, long.MaxValue)),
                    Cell(candidate.InstanceCount.ToString("N0"), candidate.InstanceCount),
                    Cell(candidate.Gen2Pct.ToString("F1") + "%", (long)Math.Round(candidate.Gen2Pct * 10)),
                    Cell(candidate.RootKind ?? "—"),
                    Cell(candidate.IsFinalizable ? "Yes" : "No"),
                    Cell(candidate.IsContainer ? "Yes" : "No"),
                    Cell(candidate.ReferenceFieldRatio.ToString("F2"))
                )).ToArray()));

            blocks.Add(T("Score factors: +30 for Gen2-heavy (>80%), +20 for >100 MB shallow size, +15 for finalizable types with >1,000 Gen2 objects, +10 each for static-rooted, pinned, and dependent-handle candidates, +5 for container-like types, +5 for reference-heavy shapes, and +5 for delegate/event-style types."));
        }

        LeakCandidateRecord[] explanationCandidates = leak.TopCandidates
            .Where(candidate => candidate.Severity != FindingSeverity.Info)
            .OrderByDescending(candidate => candidate.SuspicionScore)
            .ThenByDescending(candidate => candidate.TotalSize)
            .ToArray();

        if (explanationCandidates.Length > 0)
        {
            blocks.Add(H("LEAK EXPLANATIONS"));
            blocks.Add(T("These explanations are generated for the highest-signal candidates in the list."));

            for (int i = 0; i < explanationCandidates.Length; i++)
            {
                LeakCandidateRecord candidate = explanationCandidates[i];
                blocks.Add(CollapseBegin($"[{i + 1}] {candidate.TypeName} — {candidate.Severity} / {candidate.Classification} ({candidate.SuspicionScore:N0})"));
                blocks.Add(M("Class",    candidate.Classification.ToString()));
                blocks.Add(M("Severity", candidate.Severity.ToString()));
                blocks.Add(M("Score",    candidate.SuspicionScore.ToString("N0"), candidate.SuspicionScore));
                blocks.Add(M("Root kind",candidate.RootKind ?? "—"));
                blocks.Add(M("Instances",candidate.InstanceCount.ToString("N0"), candidate.InstanceCount));
                blocks.Add(M("Total size",FormatBytes(candidate.TotalSize), (long)Math.Min(candidate.TotalSize, long.MaxValue)));
                blocks.Add(M("Gen2%",    candidate.Gen2Pct.ToString("F1") + "%", (long)Math.Round(candidate.Gen2Pct * 10)));
                blocks.Add(M("Finalizable", candidate.IsFinalizable ? "Yes" : "No"));
                blocks.Add(M("Container",   candidate.IsContainer ? "Yes" : "No"));
                blocks.Add(M("Reference field ratio", candidate.ReferenceFieldRatio.ToString("F2"), candidate.ReferenceFieldRatio));
                blocks.Add(T(LeakExplainer.Explain(candidate)));
                blocks.Add(CollapseEnd());
                if (i + 1 < explanationCandidates.Length) blocks.Add(Blank());
            }
        }

        if (leak.TopCandidates.Count > 0)
        {
            blocks.Add(H("LEAK IMPACT"));
            blocks.Add(T("Impact bands are derived from the candidate's shallow size so the report can rank operational risk without a separate retained-size scan."));

            for (int i = 0; i < leak.TopCandidates.Count; i++)
            {
                LeakCandidateRecord candidate = leak.TopCandidates[i];
                string impactBand = GetImpactBand(candidate.TotalSize);
                string gcImpact = candidate.IsFinalizable
                    ? "Finalizable objects can extend collection cycles and add second-pass GC pressure."
                    : "No finalizer-specific impact detected.";
                string lohImpact = candidate.TotalSize > 85_000 && IsLargeObjectLike(candidate.TypeName)
                    ? "Potential LOH fragmentation risk due to large array/string-like allocations."
                    : "No LOH-specific fragmentation note.";
                string heapShare = "N/A";

                blocks.Add(CollapseBegin($"[{i + 1}] {candidate.TypeName} — {impactBand}"));
                blocks.Add(M("Shallow size",    FormatBytes(candidate.TotalSize), (long)Math.Min(candidate.TotalSize, long.MaxValue)));
                blocks.Add(M("Heap share",      heapShare));
                blocks.Add(M("Stability risk",  impactBand));
                blocks.Add(T(gcImpact));
                blocks.Add(T(lohImpact));
                blocks.Add(CollapseEnd());
                if (i + 1 < leak.TopCandidates.Count) blocks.Add(Blank());
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Leak Candidate Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string GetImpactBand(ulong totalSize)
        => totalSize < 50UL * 1024 * 1024 ? "Low"
            : totalSize < 500UL * 1024 * 1024 ? "Medium"
            : totalSize < 2UL * 1024 * 1024 * 1024 ? "High"
            : "Critical";

    private static bool IsLargeObjectLike(string typeName)
        => typeName.Contains("[]", StringComparison.Ordinal)
        || typeName.Contains("string", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("String", StringComparison.Ordinal);

    private static class LeakExplainer
    {
        public static string Explain(LeakCandidateRecord candidate, string? rootField = null) => candidate.Classification switch
        {
            LeakClass.StaticRetention =>
                $"{candidate.TypeName} is retained by a static field{(rootField != null ? $" ({rootField})" : string.Empty)} and cannot be collected. " +
                $"Total retained: ~{FormatBytes(candidate.TotalSize)}. Review the static field lifetime; consider scoped DI registration.",

            LeakClass.EventLeak =>
                $"{candidate.TypeName} instances are held alive by event subscriptions. " +
                $"A long-lived publisher is preventing {candidate.InstanceCount:N0} subscriber objects from being collected. " +
                "Unsubscribe in Dispose() or use WeakEventManager / IObservable.",

            LeakClass.CacheLeak =>
                $"{candidate.TypeName} appears to be an unbounded cache: {candidate.InstanceCount:N0} instances ({FormatBytes(candidate.TotalSize)}) are in Gen2 with no eviction signal. " +
                "Apply a size limit (MemoryCache), use WeakReference values, or add an eviction policy.",

            LeakClass.ThreadLocalLeak =>
                $"{candidate.TypeName} is referenced via ThreadLocal<T> and is being retained per thread. " +
                "Ensure Dispose() is called on the ThreadLocal wrapper when threads finish.",

            LeakClass.FinalizerRetention =>
                $"{candidate.TypeName} is queued for finalization and is retaining sub-graph objects during the delay. " +
                "Implement IDisposable + GC.SuppressFinalize to avoid queuing.",

            LeakClass.GCHandleRetention =>
                $"{candidate.TypeName} is pinned or strongly referenced via a GC handle. " +
                "Verify the handle is freed when the object is no longer needed (GCHandle.Free).",

            LeakClass.DependentHandleLeak =>
                $"{candidate.TypeName} is kept alive as the value in a ConditionalWeakTable where the key is still reachable. " +
                "Review the table's owner lifetime and consider explicit cleanup.",

            LeakClass.Unknown =>
                $"{candidate.TypeName} is reachable from a GC root but the retention pattern was not recognised. " +
                "Investigate using the root paths in §5 and the dominator candidates in §3.2.",

            _ => $"{candidate.TypeName}: {candidate.SuspicionScore} suspicion score. Manual investigation required."
        };
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
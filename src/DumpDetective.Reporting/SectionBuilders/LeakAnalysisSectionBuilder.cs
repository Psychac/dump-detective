using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LeakAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Leak Candidate Analysis";
    public string DisplayTitle => "Leak Candidates";
    public int SortOrder => 100;

    // LeakCandidateCards render as rich narrative cards (score, explanation, GC/LOH impact notes),
    // not table rows — unlike STCompact, the client has no card-pagination affordance, so this
    // bounds report verbosity the same way an inline-prose truncation would (§11.2 D5's carve-out).
    // The STCompact table below carries the complete ranked population instead.
    private const int MaxLeakCandidateCards = 30;

    public bool CanHandle(AnalyzerDomainResult result) => result is LeakCandidateDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var leak = (LeakCandidateDomainResult)result;

        var (confidenceScore, leakCaveats) = ConfidenceScoring.Compute(0.75,
            ConfidenceScoring.F(leak.HeuristicOnly, 0.15, "Heuristic-only leak analysis; no full retention scan."));
        string confidenceSymbol = confidenceScore >= 0.85 ? "●●●●" : confidenceScore >= 0.65 ? "●●●○" : confidenceScore >= 0.45 ? "●●○○" : "●○○○";

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(confidenceScore, leakCaveats.Count > 0
                ? leakCaveats
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
                    ConfidenceSymbol: confidenceSymbol,
                    ConfidenceScore: confidenceScore,
                    Caveats: leakCaveats);
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
            compactTables.Add(STCompact(
                "Candidate groups by leak class",
                new[] { CH("Class"), CH("Count","number"), CH("Total Size","bytes"), CH("Top Types") },
                classRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (leak.TopCandidates.Count > 0)
        {
            blocks.Add(T("Top candidates are ranked by suspicion score; the report highlights likely leak patterns first and then expands the highest-signal rows below."));
                compactTables.Add(STCompact(
                "Top leak candidates by suspicion score",
                    new[] { CH("Type"), CH("Score","number"), CH("Severity"), CH("Class"), CH("Total Size","bytes"), CH("Instances","number"), CH("Gen2%", "number", "percent"), CH("Root Kind"), CH("Finalizable"), CH("Container"), CH("Ref Ratio", "number", "ratio") },
                leak.TopCandidates.Select(candidate => R(new object?[] {
                    candidate.TypeName,
                    candidate.SuspicionScore,
                    candidate.Severity.ToString(),
                    candidate.Classification.ToString(),
                        candidate.TotalSize,
                    candidate.InstanceCount,
                        candidate.Gen2Pct,
                    candidate.RootKind ?? "—",
                    candidate.IsFinalizable ? "Yes" : "No",
                    candidate.IsContainer ? "Yes" : "No",
                        candidate.ReferenceFieldRatio
                })).ToArray()));

            blocks.Add(T("Score factors: +30 for Gen2-heavy (>80%), +20 for >100 MB shallow size, +15 for finalizable types with >1,000 Gen2 objects, +10 each for static-rooted, pinned, and dependent-handle candidates, +5 for container-like types, +5 for reference-heavy shapes, and +5 for delegate/event-style types."));
        }

        var leakCandidateCards = new List<LeakCandidateCard>();

        // Merge explanation + impact into typed cards for the highest-signal candidates
        int cardCount = Math.Min(leak.TopCandidates.Count, MaxLeakCandidateCards);
        for (int i = 0; i < cardCount; i++)
        {
            LeakCandidateRecord candidate = leak.TopCandidates[i];
            string impactBand = GetImpactBand(candidate.TotalSize);
            string gcImpact = candidate.IsFinalizable
                ? "Finalizable objects can extend collection cycles and add second-pass GC pressure."
                : "No finalizer-specific impact detected.";
            string lohImpact = candidate.TotalSize > 85_000 && IsLargeObjectLike(candidate.TypeName)
                ? "Potential LOH fragmentation risk due to large array/string-like allocations."
                : "No LOH-specific fragmentation note.";
            string explanationText = candidate.Severity != FindingSeverity.Info
                ? LeakExplainer.Explain(candidate)
                : string.Empty;

            leakCandidateCards.Add(new LeakCandidateCard(
                TypeName:            candidate.TypeName,
                Severity:            candidate.Severity.ToString(),
                Classification:      candidate.Classification.ToString(),
                SuspicionScore:      candidate.SuspicionScore,
                InstanceCount:       candidate.InstanceCount,
                TotalSize:           candidate.TotalSize,
                Gen2Pct:             candidate.Gen2Pct,
                RootKind:            candidate.RootKind,
                IsFinalizable:       candidate.IsFinalizable,
                IsContainer:         candidate.IsContainer,
                ReferenceFieldRatio: candidate.ReferenceFieldRatio,
                ExplanationText:     explanationText,
                ImpactBand:          impactBand,
                GcImpactNote:        gcImpact,
                LohImpactNote:       lohImpact));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Leak Candidate Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            LeakCandidateCards: leakCandidateCards.Count > 0 ? leakCandidateCards : null);
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
        string[] units = new[] { "B", "KB", "MB", "GB", "TB" };
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
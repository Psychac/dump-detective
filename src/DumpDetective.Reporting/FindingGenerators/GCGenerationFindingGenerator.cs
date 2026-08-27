using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCGenerationFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Generation Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCGenerationDomainResult r) return [];

        var findings = new List<InsightFinding>(3);

        // ── POH share finding ─────────────────────────────────────────────────
        // Only emit if POH share exceeds configured threshold (default 5%).
        // POH (Pinned Object Heap, .NET 5+) holds pinned objects; a growing share often
        // indicates buffer-pinning pressure from interop, sockets, or Span<T>-heavy code.
        double pohPct = r.TotalObjects == 0 ? 0.0 : r.PohObjects * 100.0 / r.TotalObjects;
        if (r.PohBytes > 0 && pohPct >= r.PohThresholdPercent)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: pohPct >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Pinned Object Heap (POH) share",
                Evidence: $"POH holds {pohPct:F1}% of objects ({FormatBytes(r.PohBytes)}, {r.PohObjects:N0} objects).",
                Recommendation: pohPct >= 20
                    ? "Investigate sources of object pinning (interop buffers, sockets, Span<T>/Memory<T> usage). Excess pinning fragments the heap and can inflate GC pause times."
                    : "POH share is within expected range for this dump.",
                Tags: ["gc", "poh", "pinning"],
                MetricValue: pohPct,
                MetricUnit: "%"));
        }

        // ── LOH share finding ─────────────────────────────────────────────────
        // Only emit if LOH share exceeds configured threshold (default 20%).
        // This reduces noise for healthy dumps where LOH is within expected range.
        if (r.LohPercent >= r.LohThresholdPercent)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: r.LohPercent >= 35 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "GC generation footprint snapshot",
                Evidence: $"LOH memory share is {r.LohPercent:F1}% of managed heap.",
                Recommendation: r.LohPercent >= 35
                    ? "Inspect large object churn and promotion patterns."
                    : "Generation split appears within expected range for this dump.",
                Tags: ["gc", "generations", "loh"],
                MetricValue: r.LohPercent,
                MetricUnit: "%"));
        }

        // ── Gen0 allocation pressure finding ──────────────────────────────────
        // Gen0 > threshold (default 40%) indicates high allocation rate that may degrade GC throughput.
        // High Gen0 object count signals transient pressure and frequent GC cycles.
        double gen0Pct = r.TotalObjects == 0 ? 0.0 : r.Gen0Objects * 100.0 / r.TotalObjects;
        if (gen0Pct >= r.Gen0PressureThresholdPercent)
        {
            // Build top Gen0 type evidence. Ranked by Gen0Count independent of the profile
            // list's own order (which is now Gen2-bytes ranked — see P2-4), since this finding
            // is about allocation volume, not long-lived promotion.
            string topGen0Evidence = string.Empty;
            if (r.PerTypeGenerationProfiles is { Count: > 0 })
            {
                string top = BuildTopTypeEvidence(
                    r.PerTypeGenerationProfiles,
                    static p => p.Gen0Count,
                    static p => p.Gen0Count,
                    static p => $"{p.TypeName} ×{p.Gen0Count:N0}");
                if (top.Length > 0)
                    topGen0Evidence = $" Top allocating types: {top}.";
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: FindingSeverity.Warning,
                Title: $"High Gen0 allocation pressure: {gen0Pct:F1}% of objects in Gen0",
                Evidence: $"Gen0: {r.Gen0Objects:N0} objects, {FormatBytes(r.Gen0Bytes)}. " +
                          $"Total objects: {r.TotalObjects:N0}.{topGen0Evidence}",
                Recommendation: "Review allocation patterns and object lifetime. High Gen0 activity indicates frequent " +
                                "short-lived object creation; consider object pooling, lazy initialization, or batch processing " +
                                "to reduce allocation pressure and improve GC throughput.",
                Tags: ["gc", "gen0", "allocation-pressure", "throughput"],
                MetricValue: gen0Pct,
                MetricUnit: "%"));
        }

        // ── Gen2 pressure finding ─────────────────────────────────────────────
        // Gen2 > 50% indicates chronic object promotion: objects are surviving GC cycles
        // and settling into long-lived memory, often a signal of leaks or large caches.
        if (r.Gen2Pct >= 50.0)
        {
            FindingSeverity sev = r.Gen2Pct >= 75.0 ? FindingSeverity.Critical : FindingSeverity.Warning;

            // Build top-type evidence: prefer app-namespace types (non-System.*) as they are more
            // actionable. Ranked by exact Gen2 bytes when available (P2-4) — a large accumulator
            // is a stronger leak signal than a small type with a high instance count — falling
            // back to Gen2 instance count for heap indices predating Gen2TotalSize.
            string topTypeEvidence = string.Empty;
            if (r.PerTypeGenerationProfiles is { Count: > 0 })
            {
                bool hasGen2Bytes = false;
                for (int i = 0; i < r.PerTypeGenerationProfiles.Count; i++)
                {
                    if (r.PerTypeGenerationProfiles[i].Gen2Bytes > 0)
                    {
                        hasGen2Bytes = true;
                        break;
                    }
                }

                string top = hasGen2Bytes
                    ? BuildTopTypeEvidence(
                        r.PerTypeGenerationProfiles,
                        static p => (long)Math.Min(p.Gen2Bytes, long.MaxValue),
                        static p => (long)Math.Min(p.Gen2Bytes, long.MaxValue),
                        static p => $"{p.TypeName} ({FormatBytes(p.Gen2Bytes)})")
                    : BuildTopTypeEvidence(
                        r.PerTypeGenerationProfiles,
                        static p => p.Gen2Count,
                        static p => p.Gen2Count,
                        static p => $"{p.TypeName} ×{p.Gen2Count:N0}");

                if (top.Length > 0)
                    topTypeEvidence = $" Top accumulating types: {top}.";
            }

            string qualityNote = r.FallbackMode
                ? " ⚠⚠ FALLBACK MODE: Gen0/Gen1 values unknown (set to 0). Gen2% is unreliable — this finding may be a false positive."
                : r.GenBytesAreApproximate
                    ? " ⚠ Byte values are approximate and may not reflect exact per-generation distribution for high-variance types."
                    : "";

            // Cross-references FinalizableObjectAnalyzer: a finalizable type's Gen2 instances await
            // finalization before their memory can be reclaimed, which is an actionable subset of
            // this finding's Gen2 pressure.
            string finalizableNote = r.FinalizableGen2Count > 0
                ? $" {r.FinalizableGen2Count:N0} of these objects ({FormatBytes(r.FinalizableGen2Bytes)}) are of finalizable types — see Finalizable Object Analysis."
                : "";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Gen2 holds {r.Gen2Pct:F1}% of managed heap ({FormatBytes(r.Gen2Bytes)})",
                Evidence: $"Gen2: {r.Gen2Objects:N0} objects, {FormatBytes(r.Gen2Bytes)}. " +
                          $"Gen0: {FormatBytes(r.Gen0Bytes)} | Gen1: {FormatBytes(r.Gen1Bytes)} | " +
                          $"LOH: {FormatBytes(r.LohBytes)}.{topTypeEvidence}{finalizableNote}{qualityNote}",
                Recommendation: "Run: memory-leak <dump> for full Gen2/LOH breakdown and GC root chains. " +
                                "High Gen2 indicates chronic object promotion — review long-lived caches, " +
                                "static collections, and event subscriptions that keep objects alive.",
                Tags: ["gc", "gen2", "memory-pressure", "promotion"],
                MetricValue: r.Gen2Pct,
                MetricUnit: "%"));
        }

        return findings;
    }

    /// <summary>
    /// Selects up to 3 types with a non-zero signal, ranked descending by <paramref name="rankKey"/>,
    /// preferring application-namespace types over framework types (System.*, Microsoft.*, ...) when
    /// both compete for the remaining slots. The profile list's own order is not relied upon, since
    /// callers rank on different metrics (Gen0Count vs Gen2Bytes/Gen2Count).
    /// </summary>
    private static string BuildTopTypeEvidence(
        IReadOnlyList<TypeGenerationProfile> profiles,
        Func<TypeGenerationProfile, long> hasSignal,
        Func<TypeGenerationProfile, long> rankKey,
        Func<TypeGenerationProfile, string> formatEntry)
    {
        var candidates = new List<TypeGenerationProfile>(profiles.Count);
        for (int i = 0; i < profiles.Count; i++)
            if (hasSignal(profiles[i]) > 0)
                candidates.Add(profiles[i]);

        candidates.Sort((a, b) => rankKey(b).CompareTo(rankKey(a)));

        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < candidates.Count && shown < 3; i++)
        {
            if (IsFrameworkType(candidates[i].TypeName)) continue;
            if (shown > 0) sb.Append("; ");
            sb.Append(formatEntry(candidates[i]));
            shown++;
        }
        for (int i = 0; i < candidates.Count && shown < 3; i++)
        {
            if (!IsFrameworkType(candidates[i].TypeName)) continue;
            if (shown > 0) sb.Append("; ");
            sb.Append(formatEntry(candidates[i]));
            shown++;
        }

        return sb.ToString();
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024UL) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Returns true for well-known .NET framework namespaces that are less actionable
    /// in findings than application-specific types.
    /// </summary>
    private static bool IsFrameworkType(string typeName) =>
        typeName.StartsWith("System.", StringComparison.Ordinal) ||
        typeName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        typeName.StartsWith("Windows.", StringComparison.Ordinal) ||
        typeName.StartsWith("Mono.", StringComparison.Ordinal) ||
        typeName is "System" or "Microsoft" or "Windows";
}

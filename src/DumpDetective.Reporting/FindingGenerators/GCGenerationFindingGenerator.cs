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

        var findings = new List<InsightFinding>(2);

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
            // Build top Gen0 type evidence.
            string topGen0Evidence = string.Empty;
            if (r.PerTypeGenerationProfiles is { Count: > 0 })
            {
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                // First pass: app-domain types (not starting with System., Microsoft., Windows.)
                for (int i = 0; i < r.PerTypeGenerationProfiles.Count && shown < 3; i++)
                {
                    TypeGenerationProfile p = r.PerTypeGenerationProfiles[i];
                    if (p.Gen0Count > 0 && !IsFrameworkType(p.TypeName))
                    {
                        if (shown > 0) sb.Append("; ");
                        sb.Append($"{p.TypeName} ×{p.Gen0Count:N0}");
                        shown++;
                    }
                }
                // Second pass: fill remaining slots with framework types if no app types filled all slots
                for (int i = 0; i < r.PerTypeGenerationProfiles.Count && shown < 3; i++)
                {
                    TypeGenerationProfile p = r.PerTypeGenerationProfiles[i];
                    if (p.Gen0Count > 0 && IsFrameworkType(p.TypeName))
                    {
                        if (shown > 0) sb.Append("; ");
                        sb.Append($"{p.TypeName} ×{p.Gen0Count:N0}");
                        shown++;
                    }
                }
                if (sb.Length > 0)
                    topGen0Evidence = $" Top allocating types: {sb}.";
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

            // Build top-type evidence: prefer app-namespace types (non-System.*) as they are more actionable.
            string topTypeEvidence = string.Empty;
            if (r.PerTypeGenerationProfiles is { Count: > 0 })
            {
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                // First pass: app-domain types (not starting with System., Microsoft., Windows.)
                for (int i = 0; i < r.PerTypeGenerationProfiles.Count && shown < 3; i++)
                {
                    TypeGenerationProfile p = r.PerTypeGenerationProfiles[i];
                    if (p.Gen2Count > 0 && !IsFrameworkType(p.TypeName))
                    {
                        if (shown > 0) sb.Append("; ");
                        sb.Append($"{p.TypeName} ×{p.Gen2Count:N0}");
                        shown++;
                    }
                }
                // Second pass: fill remaining slots with framework types if no app types filled all slots
                for (int i = 0; i < r.PerTypeGenerationProfiles.Count && shown < 3; i++)
                {
                    TypeGenerationProfile p = r.PerTypeGenerationProfiles[i];
                    if (p.Gen2Count > 0 && IsFrameworkType(p.TypeName))
                    {
                        if (shown > 0) sb.Append("; ");
                        sb.Append($"{p.TypeName} ×{p.Gen2Count:N0}");
                        shown++;
                    }
                }
                if (sb.Length > 0)
                    topTypeEvidence = $" Top accumulating types: {sb}.";
            }

            string qualityNote = r.FallbackMode
                ? " ⚠⚠ FALLBACK MODE: Gen0/Gen1 values unknown (set to 0). Gen2% is unreliable — this finding may be a false positive."
                : r.GenBytesAreApproximate
                    ? " ⚠ Byte values are approximate and may not reflect exact per-generation distribution for high-variance types."
                    : "";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Gen2 holds {r.Gen2Pct:F1}% of managed heap ({FormatBytes(r.Gen2Bytes)})",
                Evidence: $"Gen2: {r.Gen2Objects:N0} objects, {FormatBytes(r.Gen2Bytes)}. " +
                          $"Gen0: {FormatBytes(r.Gen0Bytes)} | Gen1: {FormatBytes(r.Gen1Bytes)} | " +
                          $"LOH: {FormatBytes(r.LohBytes)}.{topTypeEvidence}{qualityNote}",
                Recommendation: "Run: memory-leak <dump> for full Gen2/LOH breakdown and GC root chains. " +
                                "High Gen2 indicates chronic object promotion — review long-lived caches, " +
                                "static collections, and event subscriptions that keep objects alive.",
                Tags: ["gc", "gen2", "memory-pressure", "promotion"],
                MetricValue: r.Gen2Pct,
                MetricUnit: "%"));
        }

        return findings;
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

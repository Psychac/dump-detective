using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ModuleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Module Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ModuleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ModuleDomainResult r) return [];

        var findings = new List<InsightFinding>();

        // ── Version conflicts ─────────────────────────────────────────────────
        int conflicts = r.VersionConflictGroups;
        FindingSeverity conflictSeverity = conflicts >= 3 ? FindingSeverity.Critical
            : conflicts > 0 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Dependency",
            Severity: conflictSeverity,
            Title: conflicts > 0 ? "Module identity conflicts detected" : "Module dependency snapshot",
            Evidence: $"{r.TotalModules:N0} modules loaded, {r.DynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).",
            Recommendation: conflicts > 0
                ? "Align dependency versions and verify binding redirects/deployment consistency."
                : "No immediate module-version conflict action required.",
            Tags: ["modules", "assemblies", "dependency"],
            MetricValue: conflicts,
            MetricUnit: "conflict-groups"));

        // ── Heavy modules (heap memory) ───────────────────────────────────────
        if (r.TopModulesByHeapMemory is { Count: > 0 } heapModules)
        {
            var heaviest = heapModules[0];
            ulong thresholdBytes = 200 * 1024 * 1024;
            FindingSeverity heavySeverity = heaviest.TotalBytes >= thresholdBytes
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: heavySeverity,
                Title: "Module heap memory distribution",
                Evidence: $"Heaviest module on heap: {heaviest.ModuleName} consuming {FormatHelper.FormatBytes(heaviest.TotalBytes)} across {heaviest.ObjectCount:N0} objects ({heaviest.UniqueTypeCount} type(s)).",
                Recommendation: heaviest.TotalBytes >= thresholdBytes
                    ? $"Investigate why {heaviest.ModuleName} dominates heap memory. Check for unbounded caches or collections."
                    : "Module heap distribution appears normal.",
                Tags: ["modules", "memory", "heap"],
                MetricValue: (long)heaviest.TotalBytes,
                MetricUnit: "bytes"));
        }

        // ── Type density anomalies ────────────────────────────────────────────
        if (r.HeavyTypeDensityModules is { Count: > 0 } densityModules)
        {
            var worst = densityModules[0];
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Abnormal type density — memory concentrated in very few types",
                Evidence: $"{densityModules.Count} module(s) have ≤5 types yet consume significant heap memory. Worst: {worst.ModuleName} — {worst.UniqueTypeCount} type(s), {FormatHelper.FormatBytes(worst.TotalBytes)}, {FormatHelper.FormatBytes(worst.BytesPerType)}/type.",
                Recommendation: "Few-type modules with large heap footprints often indicate singleton buffers, large arrays, or byte array pools. Verify these are intentional and bounded.",
                Tags: ["modules", "memory", "type-density", "concentration"],
                MetricValue: densityModules.Count,
                MetricUnit: "anomalous-modules"));
        }

        return findings;
    }
}

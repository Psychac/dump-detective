using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ModuleFindingGenerator : IFindingGenerator
{
    private const int DynamicModuleWarningCount = 20;
    private const int DynamicModuleCriticalCount = 100;
    private const int AnonymousModuleWarningCount = 5;

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
            FindingSeverity heavySeverity = heaviest.TotalBytes >= r.HeavyModuleWarningThresholdBytes
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: heavySeverity,
                Title: "Module heap memory distribution",
                Evidence: $"Heaviest module on heap: {heaviest.ModuleName} consuming {FormatHelper.FormatBytes(heaviest.TotalBytes)} across {heaviest.ObjectCount:N0} objects ({heaviest.UniqueTypeCount} type(s)).",
                Recommendation: heaviest.TotalBytes >= r.HeavyModuleWarningThresholdBytes
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

        // ── Dynamic module accumulation (AppDomain-scoped) ──────────────────────────
        if (r.TotalDynamicModules >= DynamicModuleWarningCount)
        {
            FindingSeverity sev = r.TotalDynamicModules >= DynamicModuleCriticalCount
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Modules",
                Severity: sev,
                Title: $"Dynamic module accumulation: {r.TotalDynamicModules:N0} dynamic modules loaded",
                Evidence: $"{r.TotalDynamicModules:N0} dynamic (in-memory) modules are present across " +
                          $"{r.TotalDomains:N0} AppDomain(s). Dynamic modules are generated at runtime " +
                          "(e.g. by Reflection.Emit, expression trees, or code-generation frameworks) " +
                          "and are never unloaded while the AppDomain is alive.",
                Recommendation: "Audit usage of Reflection.Emit, LCG (DynamicMethod), and code-generation " +
                                 "libraries. Re-use generated assemblies/methods instead of creating new ones " +
                                 "per request. Consider AssemblyLoadContext for isolation and unloadability.",
                Tags: ["dynamic-module", "reflection", "code-gen", "memory"],
                MetricValue: r.TotalDynamicModules,
                MetricUnit: "modules"));
        }

        // ── Anonymous module accumulation ───────────────────────────────────────────
        if (r.AnonymousModuleCount >= AnonymousModuleWarningCount)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Modules",
                Severity: FindingSeverity.Warning,
                Title: $"Anonymous module accumulation: {r.AnonymousModuleCount:N0} modules without a file name",
                Evidence: $"{r.AnonymousModuleCount:N0} modules have no file-system path (anonymous/in-memory). " +
                          "This is typical of LCG delegates, serializer proxies, or IL-woven code.",
                Recommendation: "Investigate the source of anonymous modules. They cannot be unloaded " +
                                 "independently; use a Collectible AssemblyLoadContext if module lifetime control is needed.",
                Tags: ["anonymous-module", "dynamic", "memory"],
                MetricValue: r.AnonymousModuleCount,
                MetricUnit: "modules"));
        }

        return findings;
    }
}

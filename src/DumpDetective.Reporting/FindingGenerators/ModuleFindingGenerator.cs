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

        string conflictEvidence = $"{r.TotalModules:N0} modules loaded, {r.DynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).";
        if (conflicts > 0 && r.ConflictDetails.Count > 0)
        {
            // Emit details of conflicting versions
            var conflictSummary = new System.Text.StringBuilder();
            int detailCount = Math.Min(3, r.ConflictDetails.Count);
            for (int i = 0; i < detailCount; i++)
            {
                var group = r.ConflictDetails[i];
                conflictSummary.Append($" {group.ModuleName}: {string.Join(", ", group.Versions)}");
                if (i < detailCount - 1) conflictSummary.Append(";");
            }
            if (r.ConflictDetails.Count > 3)
                conflictSummary.Append($" (+ {r.ConflictDetails.Count - 3} more)");
            conflictEvidence += $" Conflicts: {conflictSummary}";
        }

        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Dependency",
            Severity: conflictSeverity,
            Title: conflicts > 0 ? "Module identity conflicts detected" : "Module dependency snapshot",
            Evidence: conflictEvidence,
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

        // ── Truncated module enumeration (analysis blind spot) ────────────────────────
        if (r.ExcludedModuleCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Analysis",
                Severity: FindingSeverity.Warning,
                Title: $"Module enumeration truncated: {r.ExcludedModuleCount:N0} modules excluded from analysis",
                Evidence: $"{r.ExcludedModuleCount:N0} module(s) were excluded due to AppDomain enumeration limits. " +
                          "This represents a blind spot where heap attribution, type density, and other module metrics " +
                          "may be incomplete or skewed toward the largest modules.",
                Recommendation: "Increase ModuleEnumerationLimit in analysis options to include all modules if full coverage is required. " +
                                 "Note that analyzing all modules on large memory dumps may significantly increase runtime and memory consumption.",
                Tags: ["analysis", "truncation", "blind-spot"],
                MetricValue: r.ExcludedModuleCount,
                MetricUnit: "excluded-modules"));
        }

        // ── Unknown-identity duplicate modules ────────────────────────────────────────
        if (r.UnknownIdentityDuplicateModules.Count > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Dependency",
                Severity: FindingSeverity.Warning,
                Title: $"Unknown-identity duplicate modules: {r.UnknownIdentityDuplicateModules.Count:N0} module(s) with unidentifiable versions",
                Evidence: $"{r.UnknownIdentityDuplicateModules.Count:N0} module(s) appear multiple times with no version, public-key-token, or file-hash. " +
                          "This makes it impossible to distinguish whether these are intentional loads of the same assembly or accidental duplicates. " +
                          $"Modules: {string.Join(", ", r.UnknownIdentityDuplicateModules.Take(5))}." +
                          (r.UnknownIdentityDuplicateModules.Count > 5 ? $" (+ {r.UnknownIdentityDuplicateModules.Count - 5} more)" : ""),
                Recommendation: "Verify that duplicate loads of these assemblies are intentional. If possible, ensure assemblies have strong-named identity (version, public-key-token) to enable proper conflict detection.",
                Tags: ["modules", "identity", "duplicate", "dependency"],
                MetricValue: r.UnknownIdentityDuplicateModules.Count,
                MetricUnit: "unknown-identity-modules"));
        }

        return findings;
    }
}

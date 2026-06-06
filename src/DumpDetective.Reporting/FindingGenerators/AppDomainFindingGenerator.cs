using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class AppDomainFindingGenerator : IFindingGenerator
{
    private const int DynamicModuleWarningCount = 20;
    private const int DynamicModuleCriticalCount = 100;
    private const int AnonymousModuleWarningCount = 5;

    public string AnalyzerName => "AppDomain Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AppDomainDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AppDomainDomainResult r) return [];

        var findings = new List<InsightFinding>(2);

        // ── Dynamic module accumulation ───────────────────────────────────────
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
                          "and are never unloaded while their AppDomain is alive.",
                Recommendation: "Audit usage of Reflection.Emit, LCG (DynamicMethod), and code-generation " +
                                "libraries. Re-use generated assemblies/methods instead of creating new ones " +
                                "per request. Consider AssemblyLoadContext for isolation and unloadability.",
                Tags: ["dynamic-module", "reflection", "code-gen", "memory"],
                MetricValue: r.TotalDynamicModules,
                MetricUnit: "modules"));
        }

        // ── Anonymous module accumulation ─────────────────────────────────────
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
                                "independently; use Collectible AssemblyLoadContext if module lifetime control is needed.",
                Tags: ["anonymous-module", "dynamic", "memory"],
                MetricValue: r.AnonymousModuleCount,
                MetricUnit: "modules"));
        }

        return findings;
    }
}

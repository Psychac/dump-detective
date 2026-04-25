using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ModuleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Module Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ModuleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ModuleDomainResult r) return [];

        int conflicts = r.VersionConflictGroups;
        FindingSeverity severity = conflicts >= 3 ? FindingSeverity.Critical
            : conflicts > 0 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Dependency",
                Severity: severity,
                Title: conflicts > 0 ? "Module identity conflicts detected" : "Module dependency snapshot",
                Evidence: $"{r.TotalModules:N0} modules loaded, {r.DynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).",
                Recommendation: conflicts > 0
                    ? "Align dependency versions and verify binding redirects/deployment consistency."
                    : "No immediate module-version conflict action required.",
                Tags: ["modules", "assemblies", "dependency"],
                MetricValue: conflicts,
                MetricUnit: "conflict-groups")
        ];
    }
}

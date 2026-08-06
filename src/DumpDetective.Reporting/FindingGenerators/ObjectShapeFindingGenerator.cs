using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ObjectShapeFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Object Shape Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ObjectShapeAnalyzerDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ObjectShapeAnalyzerDomainResult r) return [];
        if (r.TotalTypesAnalyzed == 0) return [];

        var findings = new List<InsightFinding>(3);

        // Flag when avg ref fields per type is high — indicates heavy GC scan burden.
        if (r.AvgRefFieldsPerType >= 4.0)
        {
            string top = r.TopReferenceHeavyTypes.Count > 0
                ? string.Join(", ", r.TopReferenceHeavyTypes.Take(3).Select(t =>
                    $"{FormatTypeName(t.TypeName)} ({t.ReferenceFields} ref fields, {t.InstanceCount:N0} instances)"))
                : "none";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: r.AvgRefFieldsPerType >= 8.0 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: $"High reference-field density: avg {r.AvgRefFieldsPerType:F1} ref fields/type",
                Evidence: $"{r.TopReferenceHeavyTypes.Count} reference-heavy types found. " +
                                $"Top: {top}. Average ref fields per analyzed type: {r.AvgRefFieldsPerType:F1}.",
                Recommendation: "Review types with many reference fields. Consider splitting large aggregate types, " +
                                "using value types for hot-path data, or caching field offsets to reduce GC scan cost.",
                Tags: ["gc-scan", "reference-fields", "object-shape"],
                MetricValue: r.AvgRefFieldsPerType,
                MetricUnit: "ref-fields/type"));
        }

        // Flag value-heavy types with large instance counts (padding waste candidates).
        if (r.TopValueHeavyTypes.Count > 0)
        {
            ulong topValCount = r.TopValueHeavyTypes[0].InstanceCount;
            if (topValCount >= 10_000)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: FindingSeverity.Info,
                    Title: $"Value-heavy types with high instance count ({topValCount:N0} instances)",
                    Evidence: $"Top value-heavy type: {FormatTypeName(r.TopValueHeavyTypes[0].TypeName)} " +
                                    $"({r.TopValueHeavyTypes[0].TotalFields} fields, {topValCount:N0} instances). " +
                                    $"{r.TopValueHeavyTypes.Count} value-heavy types analyzed.",
                    Recommendation: "Check struct field ordering for padding waste. " +
                                    "Consider using BoxingAnalyzer for struct-layout optimization.",
                    Tags: ["value-type", "struct", "object-shape"],
                    MetricValue: topValCount,
                    MetricUnit: "instances"));
            }
        }

        // Flag finalizable reference-heavy types with large instance counts — finalizers
        // delay GC completion and these types amplify GC scan cost.
        var finalizableRefHeavy = r.TopReferenceHeavyTypes
            .Where(t => t.IsFinalizable && t.InstanceCount >= 10_000)
            .ToList();

        if (finalizableRefHeavy.Count > 0)
        {
            string typeList = string.Join(", ", finalizableRefHeavy.Take(3).Select(t =>
                $"{FormatTypeName(t.TypeName)} ({t.InstanceCount:N0} instances, {t.ReferenceFields} ref fields)"));

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"Finalizable reference-heavy types: {finalizableRefHeavy.Count} types with ≥10K instances",
                Evidence: $"{finalizableRefHeavy.Count} finalizable reference-heavy type(s) found. " +
                                $"Top: {typeList}. " +
                                $"These types combine high instance counts with many reference fields, " +
                                $"amplifying GC scan cost and delaying finalizer queues.",
                Recommendation: "Consider: (1) Reducing instance counts via pooling or caching, " +
                                "(2) Refactoring to value types or weak references, " +
                                "(3) Implementing IDisposable to avoid finalizer overhead.",
                Tags: ["finalizer", "reference-heavy", "gc-scan", "object-shape"],
                MetricValue: finalizableRefHeavy.Count,
                MetricUnit: "types"));
        }

        return findings;
    }

    private static string FormatTypeName(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }
}

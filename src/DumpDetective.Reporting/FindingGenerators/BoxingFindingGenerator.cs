using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class BoxingFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Boxing Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is BoxingDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not BoxingDomainResult r) return [];

        var findings = new List<InsightFinding>();

        // Boxed enum anti-pattern — common in large codebases.
        if (r.BoxedEnumCount > 1000)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: r.BoxedEnumCount > 50_000 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Boxed enum anti-pattern detected",
                Evidence: $"{r.BoxedEnumCount:N0} boxed enum instances found " +
                          $"({FormatHelper.FormatBytes(r.BoxedEnumBytes)}). " +
                          $"Top type: {(r.TopBoxedTypes.FirstOrDefault(t => t.IsEnum)?.ValueTypeName ?? "—")}.",
                Recommendation: "Replace boxing-prone enum storage (object, non-generic collections) " +
                                "with typed generics or enum-specific dictionaries.",
                Tags: ["boxing", "enum", "allocation"],
                MetricValue: r.BoxedEnumCount,
                MetricUnit: "objects"));
        }

        // Struct padding waste — top type with high waste ratio.
        var worstPadding = r.TopPaddingWasteTypes.Count > 0 ? r.TopPaddingWasteTypes[0] : null;
        if (worstPadding is not null && worstPadding.WasteRatio > 0.25)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Struct field padding waste detected",
                Evidence: $"'{worstPadding.TypeName}' wastes {worstPadding.WastedPaddingBytes} bytes " +
                          $"({worstPadding.WasteRatio:P0}) per instance due to field alignment padding. " +
                          $"Struct size: {worstPadding.StructSize} bytes, field bytes: {worstPadding.TotalFieldBytes}.",
                Recommendation: "Reorder fields from largest to smallest to minimise alignment padding " +
                                "or apply [StructLayout(LayoutKind.Sequential, Pack=1)] if layout is safe.",
                Tags: ["boxing", "struct", "padding", "layout"],
                MetricValue: worstPadding.WasteRatio,
                MetricUnit: "ratio"));
        }

        // Oversized value types — risk of stack pressure / unintended copies.
        if (r.OversizedValueTypeCount > 100)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Oversized value type instances detected",
                Evidence: $"{r.OversizedValueTypeCount:N0} instances of value types with StaticSize > 64 bytes. " +
                          $"Large structs incur significant copy cost and stack pressure.",
                Recommendation: "Convert large structs to classes, or use 'in'/'ref' parameters to avoid copies.",
                Tags: ["boxing", "struct", "value-type", "performance"],
                MetricValue: r.OversizedValueTypeCount,
                MetricUnit: "objects"));
        }

        // Summary finding (always).
        string capNote = r.TypeScanCapped ? " (type scan capped at 10 000 entries)" : string.Empty;
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Boxing pressure overview",
            Evidence: $"Total boxed value type instances: {r.TotalBoxedObjects:N0}{capNote} " +
                      $"({FormatHelper.FormatBytes(r.TotalBoxedBytes)}). " +
                      $"Boxed enums: {r.BoxedEnumCount:N0}. " +
                      $"Oversized value types: {r.OversizedValueTypeCount:N0}. " +
                      $"Types with padding waste: {r.TopPaddingWasteTypes.Count}.",
            Recommendation: r.BoxedEnumCount > 1000 || r.OversizedValueTypeCount > 100
                ? "Review boxing-heavy paths; prefer typed generics and struct layout optimisation."
                : "Boxing pressure is within acceptable range.",
            Tags: ["boxing", "value-type"],
            MetricValue: (double)r.TotalBoxedBytes,
            MetricUnit: "bytes"));

        return findings;
    }
}

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
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
            FindingSeverity enumSeverity = r.BoxedEnumCount > 1_000_000 ? FindingSeverity.Critical
                : r.BoxedEnumCount > 50_000 ? FindingSeverity.Warning
                : FindingSeverity.Info;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: enumSeverity,
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

        // Nullable<T> boxing anti-pattern — most common source of unexpected boxing in modern C#.
        if (r.NullableBoxedCount > 100)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: r.NullableBoxedCount > 10_000 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Nullable<T> boxing detected",
                Evidence: $"{r.NullableBoxedCount:N0} boxed Nullable<T> instances found " +
                          $"({FormatHelper.FormatBytes(r.NullableBoxedBytes)}). " +
                          $"Storing Nullable<T> in object-typed fields or non-generic collections causes boxing.",
                Recommendation: "Use typed generics (List<Nullable<T>>, Dictionary<K, Nullable<T>>) or constraint " +
                                "APIs to avoid boxing. Alternatively, use 'default' or explicit null checks instead of Nullable<T>.",
                Tags: ["boxing", "nullable", "allocation"],
                MetricValue: r.NullableBoxedCount,
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
        if (r.OversizedValueTypeInstanceCount > 100)
        {
            string oversizedTypeList = r.TopOversizedTypes.Count > 0
                ? string.Join(", ", r.TopOversizedTypes.Take(5).Select(t => $"{t.TypeName} ({t.StaticSize}B, {t.Count:N0}x)"))
                : "—";
            FindingSeverity oversizedSeverity = r.OversizedValueTypeInstanceCount > 500_000 ? FindingSeverity.Critical
                : r.OversizedValueTypeInstanceCount > 100_000 ? FindingSeverity.Warning
                : FindingSeverity.Info;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: oversizedSeverity,
                Title: "Oversized value type instances detected",
                Evidence: $"{r.OversizedValueTypeInstanceCount:N0} instances of value types with StaticSize > 64 bytes. " +
                          $"Large structs incur significant copy cost and stack pressure. " +
                          $"Top offenders: {oversizedTypeList}.",
                Recommendation: "Convert large structs to classes, or use 'in'/'ref' parameters to avoid copies.",
                Tags: ["boxing", "struct", "value-type", "performance"],
                MetricValue: r.OversizedValueTypeInstanceCount,
                MetricUnit: "objects"));
        }

        // Retained (Gen2) boxing — distinguishes transient churn from boxing that survived
        // collections and is actually contributing to steady-state memory/GC pressure.
        if (r.TotalBoxedObjects > 1000)
        {
            double gen2Fraction = (double)r.TotalGen2BoxedCount / r.TotalBoxedObjects;
            if (gen2Fraction > 0.5)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: gen2Fraction > 0.8 ? FindingSeverity.Warning : FindingSeverity.Info,
                    Title: "Boxed instances are predominantly Gen2 (retained)",
                    Evidence: $"{r.TotalGen2BoxedCount:N0} of {r.TotalBoxedObjects:N0} boxed instances " +
                              $"({gen2Fraction:P0}) are in Gen2, meaning most boxing survives collections " +
                              "rather than being transient allocation churn.",
                    Recommendation: "Prioritise the top boxed types by Gen2 count/fraction — these are " +
                                    "long-lived boxes contributing to steady-state memory footprint, not " +
                                    "just GC allocation-rate pressure.",
                    Tags: ["boxing", "generation", "retained"],
                    MetricValue: gen2Fraction,
                    MetricUnit: "ratio"));
            }
        }

        // Value types missing IEquatable<T> — equality comparisons (Dictionary/HashSet keys,
        // List.Contains) fall back to object.Equals, boxing the value on every comparison.
        // Enums excluded: their equality boxing is already tracked via the enum finding above.
        var missingEquatable = r.TopBoxedTypes
            .Where(t => !t.IsEnum && !t.HasIEquatable)
            .OrderByDescending(t => t.TotalBoxBytes)
            .ToList();
        long missingEquatableInstances = missingEquatable.Sum(t => (long)t.BoxCount);
        if (missingEquatableInstances > 1000)
        {
            string offenderList = string.Join(", ", missingEquatable.Take(5).Select(t => $"{t.ValueTypeName} ({t.BoxCount:N0}x)"));
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Boxed value types missing IEquatable<T>",
                Evidence: $"{missingEquatableInstances:N0} boxed instances across {missingEquatable.Count} value type(s) " +
                          "do not implement IEquatable<T>, so equality comparisons box via object.Equals fallback. " +
                          $"Top offenders: {offenderList}.",
                Recommendation: "Implement IEquatable<T> (and matching GetHashCode) on value types used as " +
                                "Dictionary/HashSet keys or compared via List.Contains to avoid per-comparison boxing.",
                Tags: ["boxing", "struct", "equality"],
                MetricValue: missingEquatableInstances,
                MetricUnit: "objects"));
        }

        // Summary finding (always).
        FindingSeverity overallSeverity = r.TotalBoxedObjects > 1_000_000 ? FindingSeverity.Critical
            : r.TotalBoxedObjects > 500_000 ? FindingSeverity.Warning
            : FindingSeverity.Info;
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: overallSeverity,
            Title: "Boxing pressure overview",
            Evidence: $"Total boxed value type instances: {r.TotalBoxedObjects:N0} " +
                      $"({FormatHelper.FormatBytes(r.TotalBoxedBytes)}). " +
                      $"Boxed enums: {r.BoxedEnumCount:N0}. " +
                      $"Oversized value types: {r.OversizedValueTypeInstanceCount:N0}. " +
                      $"Types with padding waste: {r.TopPaddingWasteTypes.Count}.",
            Recommendation: r.BoxedEnumCount > 1000 || r.OversizedValueTypeInstanceCount > 100
                ? "Review boxing-heavy paths; prefer typed generics and struct layout optimisation."
                : "Boxing pressure is within acceptable range.",
            Tags: ["boxing", "value-type"],
            MetricValue: (double)r.TotalBoxedBytes,
            MetricUnit: "bytes"));

        return findings;
    }
}

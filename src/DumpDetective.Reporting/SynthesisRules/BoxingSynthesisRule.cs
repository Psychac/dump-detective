using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>BoxingFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Whole-run scalars come from <c>type.boxing-pressure</c>, unconditionally emitted even when every
/// per-type list is empty — the old generator's summary finding fires unconditionally too, even
/// over an all-zero result, so this rule needs a guaranteed non-empty <c>DerivedFrom</c> to attach
/// to it. <c>type.boxing</c> (already sorted descending by <c>shallow.bytes</c>) is used only for
/// per-type identity/ranking lookups the whole-run scalars can't carry: the "top boxed enum type"
/// name and the "missing IEquatable" ranked offender list, both trusting emission order instead of
/// re-sorting — the same already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies
/// on. <c>type.oversized-value</c>/<c>type.struct-padding-waste</c> are used the same way, for their
/// own per-type identity lookups.
/// </remarks>
internal sealed class BoxingSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Boxing Analysis";

    public string RuleId => "boxing.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.boxing-pressure", "type.boxing", "type.oversized-value", "type.struct-padding-waste"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? pressure = null;
        var boxedTypes = new List<Observation>();
        var oversizedTypes = new List<Observation>();
        var paddingWasteTypes = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.boxing-pressure":
                    pressure = observation;
                    break;
                case "type.boxing":
                    boxedTypes.Add(observation);
                    break;
                case "type.oversized-value":
                    oversizedTypes.Add(observation);
                    break;
                case "type.struct-padding-waste":
                    paddingWasteTypes.Add(observation);
                    break;
            }
        }

        if (pressure is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalBoxedObjects = (long)pressure.Measures["total-boxed.count"].Value;
        ulong totalBoxedBytes = (ulong)pressure.Measures["total-boxed.bytes"].Value;
        long boxedEnumCount = (long)pressure.Measures["boxed-enum.count"].Value;
        ulong boxedEnumBytes = (ulong)pressure.Measures["boxed-enum.bytes"].Value;
        long nullableBoxedCount = (long)pressure.Measures["nullable-boxed.count"].Value;
        ulong nullableBoxedBytes = (ulong)pressure.Measures["nullable-boxed.bytes"].Value;
        long oversizedValueTypeInstanceCount = (long)pressure.Measures["oversized-value.count"].Value;
        long paddingWasteTypeCount = (long)pressure.Measures["padding-waste-type.count"].Value;
        long totalGen2BoxedCount = (long)pressure.Measures["gen2-boxed.count"].Value;

        var findings = new List<Claim>();

        // ── Boxed enum anti-pattern ──────────────────────────────────────────────
        if (boxedEnumCount > 1000)
        {
            Severity enumSeverity = boxedEnumCount > 1_000_000 ? Severity.Critical
                : boxedEnumCount > 50_000 ? Severity.Warning
                : Severity.Info;
            Observation? topEnumType = boxedTypes.FirstOrDefault(o => o.Measures["is-enum"].Value >= 1.0);
            string topEnumName = topEnumType is not null ? ((TypeRef)topEnumType.Subjects[0]).CanonicalName : "—";

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("boxed-enum"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = enumSeverity,
                Title = "Boxed enum anti-pattern detected",
                Narrative = $"{boxedEnumCount:N0} boxed enum instances found " +
                    $"({FormatHelper.FormatBytes(boxedEnumBytes)}). " +
                    $"Top type: {topEnumName}.",
                Recommendation = "Replace boxing-prone enum storage (object, non-generic collections) " +
                    "with typed generics or enum-specific dictionaries.",
                Tags = ["boxing", "enum", "allocation"],
                MetricValue = boxedEnumCount,
                MetricUnit = "objects",
                DerivedFrom = [pressure.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Nullable<T> boxing anti-pattern ──────────────────────────────────────
        if (nullableBoxedCount > 100)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("nullable-boxing"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = nullableBoxedCount > 10_000 ? Severity.Warning : Severity.Info,
                Title = "Nullable<T> boxing detected",
                Narrative = $"{nullableBoxedCount:N0} boxed Nullable<T> instances found " +
                    $"({FormatHelper.FormatBytes(nullableBoxedBytes)}). " +
                    "Storing Nullable<T> in object-typed fields or non-generic collections causes boxing.",
                Recommendation = "Use typed generics (List<Nullable<T>>, Dictionary<K, Nullable<T>>) or constraint " +
                    "APIs to avoid boxing. Alternatively, use 'default' or explicit null checks instead of Nullable<T>.",
                Tags = ["boxing", "nullable", "allocation"],
                MetricValue = nullableBoxedCount,
                MetricUnit = "objects",
                DerivedFrom = [pressure.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Struct padding waste ──────────────────────────────────────────────────
        Observation? worstPadding = paddingWasteTypes.Count > 0 ? paddingWasteTypes[0] : null;
        if (worstPadding is not null && worstPadding.Measures["waste.ratio"].Value > 0.25)
        {
            string typeName = ((TypeRef)worstPadding.Subjects[0]).CanonicalName;
            long wastedPaddingBytes = (long)worstPadding.Measures["wasted-padding.bytes"].Value;
            double wasteRatio = worstPadding.Measures["waste.ratio"].Value;
            long structSize = (long)worstPadding.Measures["struct-size.bytes"].Value;
            long fieldBytes = (long)worstPadding.Measures["field.bytes"].Value;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("struct-padding"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Struct field padding waste detected",
                Narrative = $"'{typeName}' wastes {wastedPaddingBytes} bytes " +
                    $"({wasteRatio:P0}) per instance due to field alignment padding. " +
                    $"Struct size: {structSize} bytes, field bytes: {fieldBytes}.",
                Recommendation = "Reorder fields from largest to smallest to minimise alignment padding " +
                    "or apply [StructLayout(LayoutKind.Sequential, Pack=1)] if layout is safe.",
                Tags = ["boxing", "struct", "padding", "layout"],
                MetricValue = wasteRatio,
                MetricUnit = "ratio",
                DerivedFrom = [worstPadding.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Oversized value types ─────────────────────────────────────────────────
        if (oversizedValueTypeInstanceCount > 100)
        {
            string oversizedTypeList = oversizedTypes.Count > 0
                ? string.Join(", ", oversizedTypes.Take(5).Select(o =>
                    $"{((TypeRef)o.Subjects[0]).CanonicalName} ({(long)o.Measures["static-size.bytes"].Value}B, {(long)o.Measures["instance.count"].Value:N0}x)"))
                : "—";
            Severity oversizedSeverity = oversizedValueTypeInstanceCount > 500_000 ? Severity.Critical
                : oversizedValueTypeInstanceCount > 100_000 ? Severity.Warning
                : Severity.Info;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("oversized-value-types"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = oversizedSeverity,
                Title = "Oversized value type instances detected",
                Narrative = $"{oversizedValueTypeInstanceCount:N0} instances of value types with StaticSize > 64 bytes. " +
                    "Large structs incur significant copy cost and stack pressure. " +
                    $"Top offenders: {oversizedTypeList}.",
                Recommendation = "Convert large structs to classes, or use 'in'/'ref' parameters to avoid copies.",
                Tags = ["boxing", "struct", "value-type", "performance"],
                MetricValue = oversizedValueTypeInstanceCount,
                MetricUnit = "objects",
                DerivedFrom = [pressure.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Retained (Gen2) boxing ─────────────────────────────────────────────────
        if (totalBoxedObjects > 1000)
        {
            double gen2Fraction = (double)totalGen2BoxedCount / totalBoxedObjects;
            if (gen2Fraction > 0.5)
            {
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("retained-boxing"),
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = gen2Fraction > 0.8 ? Severity.Warning : Severity.Info,
                    Title = "Boxed instances are predominantly Gen2 (retained)",
                    Narrative = $"{totalGen2BoxedCount:N0} of {totalBoxedObjects:N0} boxed instances " +
                        $"({gen2Fraction:P0}) are in Gen2, meaning most boxing survives collections " +
                        "rather than being transient allocation churn.",
                    Recommendation = "Prioritise the top boxed types by Gen2 count/fraction — these are " +
                        "long-lived boxes contributing to steady-state memory footprint, not " +
                        "just GC allocation-rate pressure.",
                    Tags = ["boxing", "generation", "retained"],
                    MetricValue = gen2Fraction,
                    MetricUnit = "ratio",
                    DerivedFrom = [pressure.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Value types missing IEquatable<T> ─────────────────────────────────────
        var missingEquatable = boxedTypes
            .Where(o => o.Measures["is-enum"].Value < 1.0 && o.Measures["has-iequatable"].Value < 1.0)
            .ToList();
        long missingEquatableInstances = missingEquatable.Sum(o => (long)o.Measures["instance.count"].Value);
        if (missingEquatableInstances > 1000)
        {
            string offenderList = string.Join(", ", missingEquatable.Take(5).Select(o =>
                $"{((TypeRef)o.Subjects[0]).CanonicalName} ({(long)o.Measures["instance.count"].Value:N0}x)"));

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("missing-iequatable"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Boxed value types missing IEquatable<T>",
                Narrative = $"{missingEquatableInstances:N0} boxed instances across {missingEquatable.Count} value type(s) " +
                    "do not implement IEquatable<T>, so equality comparisons box via object.Equals fallback. " +
                    $"Top offenders: {offenderList}.",
                Recommendation = "Implement IEquatable<T> (and matching GetHashCode) on value types used as " +
                    "Dictionary/HashSet keys or compared via List.Contains to avoid per-comparison boxing.",
                Tags = ["boxing", "struct", "equality"],
                MetricValue = missingEquatableInstances,
                MetricUnit = "objects",
                DerivedFrom = missingEquatable.Select(o => o.Id).ToList(),
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Summary finding (always) ───────────────────────────────────────────────
        Severity overallSeverity = totalBoxedObjects > 1_000_000 ? Severity.Critical
            : totalBoxedObjects > 500_000 ? Severity.Warning
            : Severity.Info;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("overview"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = overallSeverity,
            Title = "Boxing pressure overview",
            Narrative = $"Total boxed value type instances: {totalBoxedObjects:N0} " +
                $"({FormatHelper.FormatBytes(totalBoxedBytes)}). " +
                $"Boxed enums: {boxedEnumCount:N0}. " +
                $"Oversized value types: {oversizedValueTypeInstanceCount:N0}. " +
                $"Types with padding waste: {paddingWasteTypeCount}.",
            Recommendation = boxedEnumCount > 1000 || oversizedValueTypeInstanceCount > 100
                ? "Review boxing-heavy paths; prefer typed generics and struct layout optimisation."
                : "Boxing pressure is within acceptable range.",
            Tags = ["boxing", "value-type"],
            MetricValue = (double)totalBoxedBytes,
            MetricUnit = "bytes",
            DerivedFrom = [pressure.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

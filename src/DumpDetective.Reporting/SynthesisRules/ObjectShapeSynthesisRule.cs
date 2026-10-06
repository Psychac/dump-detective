using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>ObjectShapeFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>type.reference-heavy</c>/<c>type.value-heavy</c>/<c>type.finalizable-reference-heavy</c> are
/// each an already-selected handful, not the full whole-heap type population <c>type.shape-composition</c>
/// deliberately excludes — the rule trusts their emission order (the analyzer's own pre-existing
/// sort, not re-derived here), the same already-sorted-before-emission structure
/// <c>ArraySynthesisRule</c> relies on. <c>type.finalizable-reference-heavy</c> is the one exception
/// to "top-N only": it's a filtered, not capped, population, so its match count is the exact
/// <c>finalizableRefHeavy.Count</c> the old generator needs, not just a display sample.
/// </remarks>
internal sealed class ObjectShapeSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Object Shape Analysis";
    private const long TotalGcScanWorkCriticalThreshold = 250_000_000L;

    public string RuleId => "object-shape.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.shape-composition", "type.reference-heavy", "type.value-heavy", "type.finalizable-reference-heavy"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = null;
        var referenceHeavyTypes = new List<Observation>();
        var valueHeavyTypes = new List<Observation>();
        var finalizableReferenceHeavyTypes = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.shape-composition":
                    composition = observation;
                    break;
                case "type.reference-heavy":
                    referenceHeavyTypes.Add(observation);
                    break;
                case "type.value-heavy":
                    valueHeavyTypes.Add(observation);
                    break;
                case "type.finalizable-reference-heavy":
                    finalizableReferenceHeavyTypes.Add(observation);
                    break;
            }
        }

        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        int totalTypesAnalyzed = (int)composition.Measures["type.count"].Value;
        if (totalTypesAnalyzed == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double avgRefFieldsPerType = composition.Measures["avg-reference-fields"].Value;
        long totalGcScanWork = (long)composition.Measures["gc-scan-work"].Value;
        long referenceHeavyTypeCount = (long)composition.Measures["reference-heavy-type.count"].Value;
        long valueHeavyTypeCount = (long)composition.Measures["value-heavy-type.count"].Value;

        var findings = new List<Claim>(3);

        // ── High reference-field density ──────────────────────────────────────────
        if (avgRefFieldsPerType >= 4.0)
        {
            string top = referenceHeavyTypes.Count > 0
                ? string.Join(", ", referenceHeavyTypes.Take(3).Select(o =>
                    $"{FormatTypeName(((TypeRef)o.Subjects[0]).CanonicalName)} ({(long)o.Measures["reference-field.count"].Value} ref fields, {(long)o.Measures["instance.count"].Value:N0} instances)"))
                : "none";

            Severity severity = totalGcScanWork >= TotalGcScanWorkCriticalThreshold
                ? Severity.Critical
                : avgRefFieldsPerType >= 8.0
                    ? Severity.Warning
                    : Severity.Info;

            var derivedFrom = new List<ObservationId> { composition.Id };
            foreach (Observation o in referenceHeavyTypes) derivedFrom.Add(o.Id);

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("reference-field-density"),
                Assertion = BuildDensityAssertion(severity, avgRefFieldsPerType, totalGcScanWork),
                Support =
                [
                    new EvidenceStatement([composition.Id], $"{totalTypesAnalyzed:N0} types analysed average {avgRefFieldsPerType:F1} reference fields; total GC scan work is {totalGcScanWork:N0} slots."),
                    .. referenceHeavyTypes.Count > 0
                        ? [new EvidenceStatement(referenceHeavyTypes.Select(o => o.Id).ToList(), "Each is a type with an unusually high reference-field count.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("Field count does not show how many instances exist or how long they live; GC scan cost depends on both."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -stat",
                        Test = "If the reference-heavy types have far fewer live instances than the heap scan counted, the scan cost is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!do <instance address>",
                        Test = "On an instance of the top reference-heavy type: if most reference fields are null in practice, the effective scan cost is lower than the field count suggests.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = severity,
                Title = $"High reference-field density: avg {avgRefFieldsPerType:F1} ref fields/type",
                Narrative = $"{referenceHeavyTypeCount} reference-heavy types found. " +
                    $"Top: {top}. Average ref fields per analyzed type: {avgRefFieldsPerType:F1}. " +
                    $"Total GC scan work across all analyzed types: {totalGcScanWork:N0} ref-field scan slots.",
                Recommendation = "Review types with many reference fields. Consider splitting large aggregate types, " +
                    "using value types for hot-path data, or caching field offsets to reduce GC scan cost.",
                Tags = ["gc-scan", "reference-fields", "object-shape"],
                MetricValue = avgRefFieldsPerType,
                MetricUnit = "ref-fields/type",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Value-heavy types with large instance counts ──────────────────────────
        if (valueHeavyTypes.Count > 0)
        {
            Observation topValueHeavy = valueHeavyTypes[0];
            long topValCount = (long)topValueHeavy.Measures["instance.count"].Value;
            if (topValCount >= 10_000)
            {
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("value-heavy-instance-count"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exhibits,
                        Measure = ClaimParts.Count(topValCount),
                        Reference = ClaimParts.Budget(ClaimParts.Count(ValueHeavyInstanceNoticeCount), "instance-count notice threshold"),
                        Qualifiers = new AssertionQualifiers { Location = $"instances of {FormatTypeName(((TypeRef)topValueHeavy.Subjects[0]).CanonicalName)}" },
                    },
                    Support = [new EvidenceStatement([topValueHeavy.Id], $"The top value-heavy type has {topValCount:N0} instances; {valueHeavyTypeCount:N0} value-heavy types were analysed.")],
                    Counter = Counter.None("Padding waste is not measured here; many instances of a well-packed struct cost nothing extra."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = $"!dumpheap -stat -type {((TypeRef)topValueHeavy.Subjects[0]).CanonicalName}",
                            Test = $"If !dumpheap -stat finds far fewer than the reported {topValCount:N0} instances, the count is overstated.",
                        },
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.CodeInspection,
                            Test = "If the struct's fields are already ordered largest-to-smallest, there is no padding waste to recover.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = Severity.Info,
                    Title = $"Value-heavy types with high instance count ({topValCount:N0} instances)",
                    Narrative = $"Top value-heavy type: {FormatTypeName(((TypeRef)topValueHeavy.Subjects[0]).CanonicalName)} " +
                        $"({(long)topValueHeavy.Measures["total-field.count"].Value} fields, {topValCount:N0} instances). " +
                        $"{valueHeavyTypeCount} value-heavy types analyzed.",
                    Recommendation = "Check struct field ordering for padding waste — order fields largest-to-smallest " +
                        "and consider [StructLayout(LayoutKind.Sequential)] to control layout explicitly. " +
                        "See Boxing Analysis's \"Struct types with highest padding waste\" table for exact " +
                        "per-type wasted bytes (StaticSize minus summed field sizes) and boxing-site detection.",
                    Tags = ["value-type", "struct", "object-shape"],
                    MetricValue = topValCount,
                    MetricUnit = "instances",
                    DerivedFrom = [topValueHeavy.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Finalizable reference-heavy types ──────────────────────────────────────
        if (finalizableReferenceHeavyTypes.Count > 0)
        {
            string typeList = string.Join(", ", finalizableReferenceHeavyTypes.Take(3).Select(o =>
                $"{FormatTypeName(((TypeRef)o.Subjects[0]).CanonicalName)} ({(long)o.Measures["instance.count"].Value:N0} instances, {(long)o.Measures["reference-field.count"].Value} ref fields)"));

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("finalizable-reference-heavy"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(finalizableReferenceHeavyTypes.Count),
                    Qualifiers = new AssertionQualifiers { Location = "finalizable types with many instances and many reference fields" },
                },
                Support = [new EvidenceStatement(finalizableReferenceHeavyTypes.Select(o => o.Id).ToList(), "Each is a finalizable type combining a high instance count with many reference fields.")],
                Counter = Counter.None("Finalizer cost depends on how many instances are queued at once; the instance counts here do not show queue depth."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!finalizequeue",
                        Test = "If !finalizequeue shows far fewer registered objects of these types than the heap scan counted, the finalizer cost is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If these types call GC.SuppressFinalize in Dispose and are normally disposed, their finalizers rarely run.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = $"Finalizable reference-heavy types: {finalizableReferenceHeavyTypes.Count} types with ≥10K instances",
                Narrative = $"{finalizableReferenceHeavyTypes.Count} finalizable reference-heavy type(s) found. " +
                    $"Top: {typeList}. " +
                    "These types combine high instance counts with many reference fields, " +
                    "amplifying GC scan cost and delaying finalizer queues.",
                Recommendation = "Consider: (1) Reducing instance counts via pooling or caching, " +
                    "(2) Refactoring to value types or weak references, " +
                    "(3) Implementing IDisposable to avoid finalizer overhead.",
                Tags = ["finalizer", "reference-heavy", "gc-scan", "object-shape"],
                MetricValue = finalizableReferenceHeavyTypes.Count,
                MetricUnit = "types",
                DerivedFrom = finalizableReferenceHeavyTypes.Select(o => o.Id).ToList(),
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static string FormatTypeName(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    private const long ValueHeavyInstanceNoticeCount = 10_000;

    private static Assertion BuildDensityAssertion(Severity severity, double avgRefFieldsPerType, long totalGcScanWork)
    {
        if (severity == Severity.Critical)
            return new Assertion
            {
                Predicate = AssertionPredicate.Exceeds,
                Measure = ClaimParts.Count(totalGcScanWork),
                Reference = ClaimParts.Budget(ClaimParts.Count(TotalGcScanWorkCriticalThreshold), "critical GC scan-work threshold"),
                Qualifiers = new AssertionQualifiers { Location = "reference-field scan slots across analysed types" },
            };

        (double threshold, string basis) = severity == Severity.Warning
            ? (8.0, "warning threshold")
            : (4.0, "notice threshold");

        return new Assertion
        {
            Predicate = AssertionPredicate.Exceeds,
            Measure = new Measure(avgRefFieldsPerType, MeasureUnit.Custom, MeasureSemantics.Ratio),
            Reference = ClaimParts.Budget(new Measure(threshold, MeasureUnit.Custom, MeasureSemantics.Ratio), basis),
            Qualifiers = new AssertionQualifiers { Location = "average reference fields per type" },
        };
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

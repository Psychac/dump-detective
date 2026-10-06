using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>AllocationPatternFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Re-derives <c>AllocationProfile</c> and <c>GCPressureLevel</c> from raw percentages rather than
/// reading a stored classification — both are Stage-1 judgment
/// (docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md's Smell B/A), deliberately not
/// carried by <c>gc.allocation-composition</c>. <c>AllocationPatternPressureClassifier</c> (the
/// analyzer's own sole-owner classifier) is <c>internal</c> to a different assembly and can't be
/// called from here, so its two formulas (<c>ComputePressureScore</c>/
/// <c>ComputePromotionPressureScore</c>) and the analyzer's own <c>ClassifyProfile</c> are
/// duplicated verbatim below — the same "re-derive rather than reach into another assembly's
/// internals" choice <c>TimerLeakSynthesisRule</c> made for its type-category classification.
/// <para>
/// <c>transient-classification.threshold</c> is carried as a measure rather than a hard-coded 70.0
/// default — <c>AllocationPatternAnalysisOptions.TransientClassificationThreshold</c> is genuinely
/// runtime-configurable via <c>AnalysisContext.AnalyzerOptions</c>, the same wrinkle
/// <c>GCHandleSynthesisRule</c> hit for its own thresholds.
/// </para>
/// <para>
/// The unbounded, composite-ranked <c>TopLongLivedTypes</c> list (Smell B, same reasoning as
/// <c>AllocationProfile</c>, plus a real observation-volume risk on a large heap) is not observed
/// per-entry — only the single top type's name the old generator actually reads, carried as
/// <c>gc.allocation-composition</c>'s own <c>EvidenceRef.Description</c>.
/// </para>
/// </remarks>
internal sealed class AllocationPatternSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Allocation Pattern Analysis";

    public string RuleId => "allocation-pattern.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["gc.allocation-composition"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = null;
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "gc.allocation-composition")
            {
                composition = observation;
                break;
            }
        }

        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double gen0CountPct = composition.Measures["gen0.count-fraction"].Value * 100.0;
        double gen2CountPct = composition.Measures["gen2.count-fraction"].Value * 100.0;
        double gen0SizePct = composition.Measures["gen0.size-fraction"].Value * 100.0;
        double gen2SizePct = composition.Measures["gen2.size-fraction"].Value * 100.0;
        double lohSizePct = composition.Measures["loh.size-fraction"].Value * 100.0;
        long finalizableTypeCount = (long)composition.Measures["finalizable.type-count"].Value;
        ulong finalizableBytes = (ulong)composition.Measures["finalizable.bytes"].Value;
        double transientClassificationThreshold = composition.Measures["transient-classification.threshold"].Value;

        GcPressureLevel gcPressure = ClassifyPressure(gen0CountPct, gen2CountPct, lohSizePct);

        // Suppress findings at Low pressure (reduces noise) — no finding at all, not even an
        // info-level "nothing to report" one, matching the old generator exactly.
        if (gcPressure == GcPressureLevel.Low)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double promotionPressureScore = ComputePromotionPressureScore(gen2CountPct, lohSizePct);
        AllocationProfile profile = ClassifyProfile(gen0CountPct, gen2CountPct, transientClassificationThreshold);

        Severity severity = gcPressure switch
        {
            GcPressureLevel.Critical => Severity.Critical,
            GcPressureLevel.High => Severity.Warning,
            _ => Severity.Info,
        };

        string topLongLivedType = composition.Evidence.Count > 0
            ? $" Top long-lived type: {composition.Evidence[0].Description}."
            : "";

        string finalizableNote = finalizableTypeCount > 0
            ? $" {finalizableTypeCount:N0} finalizable type(s) hold {FormatHelper.FormatBytes(finalizableBytes)}."
            : "";

        string narrative = $"Allocation profile: {profile}. " +
            $"Gen0: {gen0CountPct:F1}% obj / {gen0SizePct:F1}% bytes, " +
            $"Gen2: {gen2CountPct:F1}% obj / {gen2SizePct:F1}% bytes, " +
            $"LOH: {lohSizePct:F1}% bytes. " +
            $"GC pressure: {gcPressure} (score: {promotionPressureScore:F1}).{topLongLivedType}{finalizableNote}";

        string recommendation = gcPressure switch
        {
            GcPressureLevel.Critical =>
                "Critical GC pressure. Large Gen2 and/or LOH accumulation detected. Investigate long-lived objects and finalizable types.",
            GcPressureLevel.High =>
                "High GC pressure. Reduce Gen2 retention — review caches, static fields, and event subscriptions.",
            GcPressureLevel.Moderate =>
                "Moderate GC pressure. Monitor Gen2 growth across snapshots.",
            _ =>
                "GC pressure is within normal bounds.",
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
        {
            Fingerprint = Fingerprint("gc-allocation-pattern"),
            Assertion = BuildAssertion(gcPressure, profile, gen0CountPct, gen2CountPct, lohSizePct),
            Support = [new EvidenceStatement([composition.Id], $"Gen0 holds {gen0CountPct:F1}% of objects, Gen2 {gen2CountPct:F1}% of objects, and the LOH {lohSizePct:F1}% of bytes; these shares feed the pressure score.")],
            Counter = BuildCounter(composition, profile, gen0CountPct),
            FalsifiedBy = BuildFalsifiedBy(gen2CountPct, lohSizePct),
            Analyzer = AnalyzerName,
            Category = "GC",
            Severity = severity,
            Title = $"GC allocation pattern: {profile} ({gcPressure} pressure)",
            Narrative = narrative,
            Recommendation = recommendation,
            Tags = ["gc", "allocation", "gen2", "pressure"],
            MetricValue = promotionPressureScore,
            MetricUnit = "pressure-score",
            DerivedFrom = [composition.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        }]);
    }

    // Duplicated from AllocationPatternPressureClassifier (internal to a different assembly) —
    // see remarks.
    private enum GcPressureLevel { Low, Moderate, High, Critical }

    // Mirrors AllocationPatternDomainResult's own AllocationProfile enum (also inaccessible here —
    // Reporting has no dependency on the Analysis project's model types).
    private enum AllocationProfile { Transient, Steady, Retained, Mixed }

    private static double ComputePressureScore(double gen0CountPct, double gen2CountPct, double lohSizePct) =>
        ((100.0 - gen0CountPct) * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2);

    private static GcPressureLevel ClassifyPressure(double gen0CountPct, double gen2CountPct, double lohSizePct)
    {
        double score = ComputePressureScore(gen0CountPct, gen2CountPct, lohSizePct);
        if (score > 70.0) return GcPressureLevel.Critical;
        if (score > 45.0) return GcPressureLevel.High;
        if (score > 20.0) return GcPressureLevel.Moderate;
        return GcPressureLevel.Low;
    }

    private static double ComputePromotionPressureScore(double gen2CountPct, double lohSizePct) =>
        gen2CountPct + (lohSizePct * 2.0);

    private static AllocationProfile ClassifyProfile(double gen0Pct, double gen2Pct, double transientThreshold)
    {
        if (gen0Pct > transientThreshold) return AllocationProfile.Transient;
        if (gen2Pct > 50.0) return AllocationProfile.Retained;
        if (gen0Pct > 50.0) return AllocationProfile.Steady;
        return AllocationProfile.Mixed;
    }

    private static Assertion BuildAssertion(
        GcPressureLevel pressure, AllocationProfile profile, double gen0CountPct, double gen2CountPct, double lohSizePct)
    {
        (double threshold, string basis) = pressure switch
        {
            GcPressureLevel.Critical => (70.0, "critical pressure threshold"),
            GcPressureLevel.High => (45.0, "high pressure threshold"),
            _ => (20.0, "moderate pressure threshold"),
        };

        return new Assertion
        {
            Predicate = AssertionPredicate.Exceeds,
            Measure = new Measure(ComputePressureScore(gen0CountPct, gen2CountPct, lohSizePct), MeasureUnit.Custom, MeasureSemantics.Ratio),
            Reference = ClaimParts.Budget(new Measure(threshold, MeasureUnit.Custom, MeasureSemantics.Ratio), basis),
            Qualifiers = new AssertionQualifiers { Location = "GC pressure score", Mechanism = $"{profile} allocation profile" },
        };
    }

    private static Counter BuildCounter(Observation composition, AllocationProfile profile, double gen0CountPct) =>
        profile == AllocationProfile.Transient
            ? Counter.Of(new EvidenceStatement(
                [composition.Id],
                $"{gen0CountPct:F1}% of objects are in Gen0, which is cheap to collect, so the pressure may be allocation churn, not retention."))
            : Counter.None("The score combines generation and LOH shares from one snapshot; nothing observed shows whether Gen2 is growing or stable.");

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(double gen2CountPct, double lohSizePct) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!eeheap -gc",
            Test = $"If the Gen2 and LOH sizes in !eeheap -gc do not match a {gen2CountPct:F1}% Gen2 object share and {lohSizePct:F1}% LOH byte share, the composition is wrong.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.Capture,
            Test = "Capture a second dump under comparable load; if the Gen2 share does not grow, the pressure is steady state, not accumulation.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}

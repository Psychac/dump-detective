using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectAllocationPressureCrossCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Re-derives <c>GcPressureLevel</c>/<c>AllocationProfile</c> from raw percentages rather than
/// reading a stored classification, the same choice <c>AllocationPatternSynthesisRule</c> already
/// made for the same reason (<c>AllocationPatternPressureClassifier</c> is <c>internal</c> to a
/// different assembly) — its two formulas and enum are duplicated verbatim below, a second time,
/// not shared, following that file's own precedent of each rule keeping its own copy.
/// </remarks>
internal sealed class AllocationPressureCrossCorrelationInsightRule : ISynthesisRule
{
    public string RuleId => "insight.allocation-pressure-cross-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["gc.allocation-composition", "runtime.thread-triage"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? allocPattern = null, threadTriage = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "gc.allocation-composition": allocPattern = observation; break;
                case "runtime.thread-triage": threadTriage = observation; break;
            }
        }

        if (allocPattern is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double gen0CountPct = allocPattern.Measures["gen0.count-fraction"].Value * 100.0;
        double gen2CountPct = allocPattern.Measures["gen2.count-fraction"].Value * 100.0;
        double gen0SizePct = allocPattern.Measures["gen0.size-fraction"].Value * 100.0;
        double lohSizePct = allocPattern.Measures["loh.size-fraction"].Value * 100.0;
        double transientThreshold = allocPattern.Measures["transient-classification.threshold"].Value;

        GcPressureLevel gcPressure = ClassifyPressure(gen0CountPct, gen2CountPct, lohSizePct);
        double promotionPressureScore = ComputePromotionPressureScore(gen2CountPct, lohSizePct);
        AllocationProfile profile = ClassifyProfile(gen0CountPct, gen2CountPct, transientThreshold);

        // Standalone: GC pressure is Critical
        if (gcPressure == GcPressureLevel.Critical)
        {
            bool threadImpact = threadTriage is not null &&
                                threadTriage.Measures["alive-thread.count"].Value > 0 &&
                                threadTriage.Measures["blocked-thread.count"].Value * 100.0 / threadTriage.Measures["alive-thread.count"].Value >= 30.0;

            string crossNote = threadImpact
                ? $" Combined with {threadTriage!.Measures["blocked-thread.count"].Value:N0} blocked threads " +
                  $"({threadTriage.Measures["blocked-thread.count"].Value * 100.0 / threadTriage.Measures["alive-thread.count"].Value:F0}%), " +
                  "GC pauses may be causing thread stalls."
                : string.Empty;

            var derivedFrom = threadTriage is not null ? new[] { allocPattern.Id, threadTriage.Id } : [allocPattern.Id];

            return ValueTask.FromResult<IReadOnlyList<Claim>>(
            [
                new Claim
                {
                    Fingerprint = "insightengine::allocation-pressure-critical",
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exceeds,
                        Measure = new Measure(ComputePressureScore(gen0CountPct, gen2CountPct, lohSizePct), MeasureUnit.Custom, MeasureSemantics.Ratio),
                        Reference = ClaimParts.Budget(new Measure(70.0, MeasureUnit.Custom, MeasureSemantics.Ratio), "critical pressure threshold"),
                        Qualifiers = new AssertionQualifiers
                        {
                            Location = "GC pressure score",
                            Mechanism = threadImpact ? $"{profile} allocation profile with many blocked threads" : $"{profile} allocation profile",
                        },
                    },
                    Support =
                    [
                        new EvidenceStatement([allocPattern.Id], $"Gen0 holds {gen0CountPct:F1}% of objects ({gen0SizePct:F1}% of size); the promotion pressure score is {promotionPressureScore:F2}."),
                        .. threadImpact
                            ? [new EvidenceStatement([threadTriage!.Id], "A large share of alive threads is blocked, which GC pauses can contribute to.")]
                            : Array.Empty<EvidenceStatement>(),
                    ],
                    Counter = profile == AllocationProfile.Transient
                        ? Counter.Of(new EvidenceStatement([allocPattern.Id], $"{gen0CountPct:F1}% of objects are in Gen0, which is cheap to collect, so the pressure may be allocation churn, not retention."))
                        : Counter.None("The score combines generation and LOH shares from one snapshot; nothing observed shows whether Gen2 is growing or stable."),
                    FalsifiedBy =
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
                    ],
                    Analyzer = "InsightEngine",
                    Category = "Memory",
                    Severity = Severity.Critical,
                    Title = "Critical GC allocation pressure detected",
                    Narrative = $"Allocation pattern: {profile}. " +
                                $"Gen0: {gen0CountPct:F1}% of objects ({gen0SizePct:F1}% of size). " +
                                $"Promotion pressure score: {promotionPressureScore:F2}.{crossNote}",
                    Recommendation = "Profile allocations with dotnet-trace. Reduce transient allocations with " +
                                     "object pooling (ArrayPool<T>, ObjectPool<T>), struct types, and Span<T>.",
                    Tags = ["gc-pressure", "allocation", "gen0"],
                    MetricValue = promotionPressureScore,
                    MetricUnit = "score",
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                },
            ]);
        }

        // Standalone: GC pressure is High
        if (gcPressure == GcPressureLevel.High)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>(
            [
                new Claim
                {
                    Fingerprint = "insightengine::allocation-pressure-high",
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exceeds,
                        Measure = new Measure(ComputePressureScore(gen0CountPct, gen2CountPct, lohSizePct), MeasureUnit.Custom, MeasureSemantics.Ratio),
                        Reference = ClaimParts.Budget(new Measure(45.0, MeasureUnit.Custom, MeasureSemantics.Ratio), "high pressure threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "GC pressure score", Mechanism = $"{profile} allocation profile" },
                    },
                    Support = [new EvidenceStatement([allocPattern.Id], $"Gen0 holds {gen0CountPct:F1}% of objects; the promotion pressure score is {promotionPressureScore:F2}.")],
                    Counter = profile == AllocationProfile.Transient
                        ? Counter.Of(new EvidenceStatement([allocPattern.Id], $"{gen0CountPct:F1}% of objects are in Gen0, which is cheap to collect, so the pressure may be allocation churn, not retention."))
                        : Counter.None("The score combines generation and LOH shares from one snapshot; nothing observed shows whether Gen2 is growing or stable."),
                    FalsifiedBy =
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
                    ],
                    Analyzer = "InsightEngine",
                    Category = "Memory",
                    Severity = Severity.Warning,
                    Title = "High GC allocation pressure detected",
                    Narrative = $"Allocation pattern: {profile}. " +
                                $"Gen0: {gen0CountPct:F1}% of objects. " +
                                $"Promotion pressure score: {promotionPressureScore:F2}.",
                    Recommendation = "Consider object pooling and reduced short-lived allocations on hot paths.",
                    Tags = ["gc-pressure", "allocation", "gen0"],
                    MetricValue = promotionPressureScore,
                    MetricUnit = "score",
                    DerivedFrom = [allocPattern.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                },
            ]);
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>([]);
    }

    // Duplicated from AllocationPatternPressureClassifier (internal to a different assembly) —
    // see remarks. Mirrors AllocationPatternSynthesisRule's own copy exactly.
    private enum GcPressureLevel { Low, Moderate, High, Critical }

    private enum AllocationProfile { Transient, Steady, Retained, Mixed }

    private static double ComputePressureScore(double gen0CountPct, double gen2CountPct, double lohSizePct) =>
        ((100.0 - gen0CountPct) * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2);

    private static GcPressureLevel ClassifyPressure(double gen0CountPct, double gen2CountPct, double lohSizePct)
    {
        double score = ComputePressureScore(gen0CountPct, gen2CountPct, lohSizePct);
        if (score >= 70.0) return GcPressureLevel.Critical;
        if (score >= 45.0) return GcPressureLevel.High;
        if (score >= 20.0) return GcPressureLevel.Moderate;
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
}

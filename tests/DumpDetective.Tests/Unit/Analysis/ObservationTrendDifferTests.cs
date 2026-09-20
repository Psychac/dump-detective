using DumpDetective.Analysis.Trend;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// See docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4 — this is the
/// generic pairwise differ over headline Observations, dispatching on MeasureSemantics.
/// </summary>
public sealed class ObservationTrendDifferTests
{
    // gc.generation-composition/gen0.bytes is a real MeasureDirectionRegistry entry (HigherIsWorse).
    private const string RegisteredObservationType = "gc.generation-composition";
    private const string RegisteredHigherIsWorseKey = "gen0.bytes";

    [Fact]
    public void Compare_AbsoluteSemantics_ProducesDeltaAndPercent()
    {
        Observation baseline = MakeHeadlineObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(100.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));
        Observation current = MakeHeadlineObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(150.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        MetricDelta delta = deltas.Should().ContainSingle().Subject;
        delta.Baseline.Should().Be(100.0);
        delta.Current.Should().Be(150.0);
        delta.Delta.Should().Be(50.0);
        delta.DeltaPercent.Should().Be(50.0);
        delta.Direction.Should().Be(MetricTrendDirection.HigherIsWorse);
    }

    [Fact]
    public void Compare_RatioSemantics_DiffsAsPointDifference_NeverPercentOfPercent()
    {
        // The concrete bug this differ fixes: AllocationPatternTrendComparer.Compare diffs the
        // *same underlying* gen2.count-fraction value with Absolute-shaped percent-change math,
        // reporting 10%->15% as "+50%" instead of "+5 percentage points."
        Observation baseline = MakeHeadlineObservation("gc.allocation-composition",
            ("gen2.count-fraction", new Measure(0.10, MeasureUnit.Ratio, MeasureSemantics.Ratio)));
        Observation current = MakeHeadlineObservation("gc.allocation-composition",
            ("gen2.count-fraction", new Measure(0.15, MeasureUnit.Ratio, MeasureSemantics.Ratio)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        MetricDelta delta = deltas.Should().ContainSingle().Subject;
        delta.Delta.Should().BeApproximately(0.05, 1e-9);
        delta.DeltaPercent.Should().BeNull("Ratio measures diff as a point difference, never percent-of-a-percent");
    }

    [Fact]
    public void Compare_UsesDirectionFromMeasureDirectionRegistry()
    {
        // heap.loh-fragmentation/largest-free-block.bytes is registered LowerIsWorse.
        Observation baseline = MakeHeadlineObservation("heap.loh-fragmentation",
            ("largest-free-block.bytes", new Measure(1000.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));
        Observation current = MakeHeadlineObservation("heap.loh-fragmentation",
            ("largest-free-block.bytes", new Measure(500.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        deltas.Should().ContainSingle().Which.Direction.Should().Be(MetricTrendDirection.LowerIsWorse);
    }

    [Fact]
    public void Compare_SkipsMeasure_WithNoRegisteredDirection()
    {
        Observation baseline = MakeHeadlineObservation("gc.generation-composition",
            ("has-gen2-bytes", new Measure(0.0, MeasureUnit.Custom, MeasureSemantics.Absolute)));
        Observation current = MakeHeadlineObservation("gc.generation-composition",
            ("has-gen2-bytes", new Measure(1.0, MeasureUnit.Custom, MeasureSemantics.Absolute)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        deltas.Should().BeEmpty("'has-gen2-bytes' has no MeasureDirectionRegistry entry — not yet classified, not neutral");
    }

    [Fact]
    public void Compare_SkipsMeasure_PresentInBaselineOnly()
    {
        Observation baseline = MakeHeadlineObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(100.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));
        Observation current = MakeHeadlineObservation(RegisteredObservationType); // no measures at all

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        deltas.Should().BeEmpty("a measure absent from current isn't diffable without fabricating a value");
    }

    [Fact]
    public void Compare_IgnoresScopedObservations()
    {
        Observation baseline = MakeScopedObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(100.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));
        Observation current = MakeScopedObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(150.0, MeasureUnit.Bytes, MeasureSemantics.Absolute)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        deltas.Should().BeEmpty("scoped (per-type) Observations are out of this differ's scope entirely");
    }

    [Fact]
    public void Compare_CountSemantics_FallsBackToDeltaAndPercent_NotYetSeriesAware()
    {
        // runtime.jit-activity/active-methods.count is registered Neutral, Absolute-typed in the
        // registry — but the *semantics dispatch* itself is what's under test, so force Count here.
        Observation baseline = MakeHeadlineObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(10.0, MeasureUnit.Count, MeasureSemantics.Count)));
        Observation current = MakeHeadlineObservation(RegisteredObservationType,
            (RegisteredHigherIsWorseKey, new Measure(20.0, MeasureUnit.Count, MeasureSemantics.Count)));

        IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare([baseline], [current]);

        MetricDelta delta = deltas.Should().ContainSingle().Subject;
        delta.Delta.Should().Be(10.0);
        delta.DeltaPercent.Should().Be(100.0, "Count/Rate/Duration fall back to Absolute-shaped math in this pairwise differ");
    }

    private static Observation MakeHeadlineObservation(string observationType, params (string Key, Measure Measure)[] measures) =>
        MakeObservation(observationType, Subjects: [], measures);

    private static Observation MakeScopedObservation(string observationType, params (string Key, Measure Measure)[] measures) =>
        MakeObservation(observationType, Subjects: [new TypeRef { CanonicalName = "System.String", Fidelity = MatchFidelity.Exact }], measures);

    private static Observation MakeObservation(
        string observationType, IReadOnlyList<EntityRef> Subjects, (string Key, Measure Measure)[] measures) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = Subjects,
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures.ToDictionary(m => m.Key, m => m.Measure),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };
}

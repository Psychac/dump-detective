using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Covers the 10 <c>*InsightRule</c> classes that replaced <c>InsightEngine</c>'s
/// <c>MemoryAndRuntimeRuleGroup</c> (phase 5 step 6.2,
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md). See
/// <c>InsightBaselineRulesTests</c> for the equivalent coverage of <c>BaselineRuleGroup</c>.
/// </summary>
public sealed class InsightMemoryAndRuntimeRulesTests
{
    private static Observation Obs(
        string observationType,
        IReadOnlyDictionary<string, Measure> measures,
        IReadOnlyList<EntityRef>? subjects = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjects ?? [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static Measure Count(double v) => new(v, MeasureUnit.Count, MeasureSemantics.Absolute);
    private static Measure Bytes(double v) => new(v, MeasureUnit.Bytes, MeasureSemantics.Absolute);
    private static Measure Pct(double v) => new(v, MeasureUnit.Percent, MeasureSemantics.Ratio);
    private static Measure Ratio(double v) => new(v, MeasureUnit.Ratio, MeasureSemantics.Ratio);
    private static Measure Custom(double v) => new(v, MeasureUnit.Custom, MeasureSemantics.Absolute);

    public sealed class GCRootLargeRetention
    {
        private readonly GCRootLargeRetentionInsightRule _rule = new();

        [Fact]
        public async Task LargestRootAboveThreshold_ReportsKindAndTargetType()
        {
            var targetType = new TypeRef { CanonicalName = "MyApp.Cache", Fidelity = MatchFidelity.Exact };

            var matched = new ObservationMatchSet(
            [
                Obs("heap.root",
                    new Dictionary<string, Measure> { ["retained.bytes"] = Bytes(80 * 1024 * 1024), ["root-kind.ordinal"] = Custom((double)(int)HeapRootKind.StaticVar) },
                    subjects: [targetType]),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("StaticVar").And.Contain("MyApp.Cache");
        }

        [Fact]
        public async Task BelowThreshold_ReturnsEmpty()
        {
            var targetType = new TypeRef { CanonicalName = "MyApp.Small", Fidelity = MatchFidelity.Exact };
            var matched = new ObservationMatchSet(
            [
                Obs("heap.root",
                    new Dictionary<string, Measure> { ["retained.bytes"] = Bytes(1024), ["root-kind.ordinal"] = Custom((double)(int)HeapRootKind.StaticVar) },
                    subjects: [targetType]),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class AllocationPressureCrossCorrelation
    {
        private readonly AllocationPressureCrossCorrelationInsightRule _rule = new();

        [Fact]
        public async Task CriticalPressure_WithThreadImpact_IncludesCrossNote()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("gc.allocation-composition", new Dictionary<string, Measure>
                {
                    ["gen0.count-fraction"] = Ratio(0.05),
                    ["gen2.count-fraction"] = Ratio(0.80),
                    ["gen0.size-fraction"] = Ratio(0.05),
                    ["loh.size-fraction"] = Ratio(0.50),
                    ["transient-classification.threshold"] = Ratio(70.0),
                }),
                Obs("runtime.thread-triage", new Dictionary<string, Measure>
                {
                    ["alive-thread.count"] = Count(10), ["blocked-thread.count"] = Count(5),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Narrative.Should().Contain("blocked threads");
        }

        [Fact]
        public async Task LowPressure_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("gc.allocation-composition", new Dictionary<string, Measure>
                {
                    ["gen0.count-fraction"] = Ratio(0.90),
                    ["gen2.count-fraction"] = Ratio(0.02),
                    ["gen0.size-fraction"] = Ratio(0.90),
                    ["loh.size-fraction"] = Ratio(0.01),
                    ["transient-classification.threshold"] = Ratio(70.0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class StringDuplicationRatio
    {
        private readonly StringDuplicationRatioInsightRule _rule = new();

        [Fact]
        public async Task HighRatioLowAbsoluteWaste_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("type.string-composition", new Dictionary<string, Measure>
                {
                    ["total-string.count"] = Count(1000),
                    ["duplication.ratio"] = Ratio(0.60),
                    ["duplicate-wasted.bytes"] = Bytes(1024),
                    ["unique-pattern.count"] = Count(400),
                    ["sampling-coverage.ratio"] = Ratio(1.0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }

        [Fact]
        public async Task HighAbsoluteWaste_DefersToLeakSuspicion()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("type.string-composition", new Dictionary<string, Measure>
                {
                    ["total-string.count"] = Count(1000),
                    ["duplication.ratio"] = Ratio(0.60),
                    ["duplicate-wasted.bytes"] = Bytes(20 * 1024 * 1024),
                    ["unique-pattern.count"] = Count(400),
                    ["sampling-coverage.ratio"] = Ratio(1.0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class LohArrayPressure
    {
        private readonly LohArrayPressureInsightRule _rule = new();

        [Fact]
        public async Task AboveThreshold_CorrelatesWithFragmentation()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("type.array-population", new Dictionary<string, Measure> { ["loh.bytes"] = Bytes(300 * 1024 * 1024), ["loh.count"] = Count(50) }),
                Obs("heap.loh-fragmentation-summary", new Dictionary<string, Measure>
                {
                    ["fragmentation.percent"] = Pct(45.0), ["free.bytes"] = Bytes(0), ["free-block.count"] = Count(0), ["largest-free-block.bytes"] = Bytes(0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
        }
    }

    public sealed class AsyncStateMachineFireAndForget
    {
        private readonly AsyncStateMachineFireAndForgetInsightRule _rule = new();

        [Fact]
        public async Task SumsInstanceCountAcrossSharedDeclaringMethod()
        {
            var declaringType = new TypeRef { CanonicalName = "MyApp.Service", Fidelity = MatchFidelity.Low };
            var method = new MethodRef { DeclaringType = declaringType, Name = "FireAndForgetAsync", NormalizedSignature = string.Empty, Fidelity = MatchFidelity.Low };
            var stateMachineTypeA = new TypeRef { CanonicalName = "MyApp.Service+<FireAndForgetAsync>d__1", Fidelity = MatchFidelity.High };
            var stateMachineTypeB = new TypeRef { CanonicalName = "MyApp.Service+<FireAndForgetAsync>d__2", Fidelity = MatchFidelity.High };

            var matched = new ObservationMatchSet(
            [
                Obs("type.async-state-machine",
                    new Dictionary<string, Measure> { ["instance.count"] = Count(60), ["shallow.bytes"] = Bytes(1000), ["dominant-state"] = Custom(0), ["gen2.count"] = Count(0), ["gen2.fraction"] = Ratio(0), ["is-async-void"] = Count(0) },
                    subjects: [stateMachineTypeA, method]),
                Obs("type.async-state-machine",
                    new Dictionary<string, Measure> { ["instance.count"] = Count(50), ["shallow.bytes"] = Bytes(1000), ["dominant-state"] = Custom(0), ["gen2.count"] = Count(0), ["gen2.fraction"] = Ratio(0), ["is-async-void"] = Count(0) },
                    subjects: [stateMachineTypeB, method]),
                Obs("type.async-state-machine-population", new Dictionary<string, Measure> { ["total.count"] = Count(110), ["total.bytes"] = Bytes(2000), ["gen2.count"] = Count(0), ["gen2.percent"] = Pct(0) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("110").And.Contain("MyApp.Service.FireAndForgetAsync");
        }
    }

    public sealed class StaleWeakReferenceAccumulation
    {
        private readonly StaleWeakReferenceAccumulationInsightRule _rule = new();

        [Fact]
        public async Task HighDeadRatio_ReportsStaleWrapperCount()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("weakreference.population", new Dictionary<string, Measure>
                {
                    ["total-handles.count"] = Count(100),
                    ["dead-targets.count"] = Count(90),
                    ["object.count"] = Count(0),
                    ["object.bytes"] = Bytes(0),
                    ["dependent-dead-key.count"] = Count(0),
                    ["held-only-detection-available"] = Count(0),
                    ["stale-wrapper.count"] = Count(15),
                    ["stale-wrapper.is-exact"] = Count(1),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
            findings[0].Narrative.Should().Contain("15");
            findings[0].Narrative.Should().NotContain("estimated");
        }
    }

    public sealed class AddressSpacePressure
    {
        private readonly AddressSpacePressureInsightRule _rule = new();

        [Fact]
        public async Task HighReservedToCommittedRatio_FiresPressureFinding()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.segment-reservation", new Dictionary<string, Measure>
                {
                    ["committed.bytes"] = Bytes(100 * 1024 * 1024),
                    ["reserved.bytes"] = Bytes(1000 * 1024 * 1024),
                    ["reservation-gap.bytes"] = Bytes(0),
                    ["reserved-to-committed.ratio"] = Ratio(10.0),
                    ["ephemeral.segment-count"] = Count(0),
                    ["ephemeral.avg-fill-percent"] = Pct(0),
                    ["segment.count"] = Count(5),
                    ["dump-pointer-size"] = Custom(8),
                    ["logical-heap.count"] = Count(1),
                    ["ratio-high-pressure.threshold"] = Ratio(5.0),
                    ["ratio-medium-pressure.threshold"] = Ratio(2.0),
                    ["thirty-two-bit-pressure.threshold-bytes"] = Bytes(1_500_000_000),
                    ["near-empty-region-fill-percent.threshold"] = Pct(10),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle(f => f.Title == "Managed heap virtual address space pressure");
            findings[0].Narrative.Should().Contain("ratio");
        }

        [Fact]
        public async Task CriticalEphemeralFill_FiresSeparateFinding()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.segment-reservation", new Dictionary<string, Measure>
                {
                    ["committed.bytes"] = Bytes(100 * 1024 * 1024),
                    ["reserved.bytes"] = Bytes(150 * 1024 * 1024),
                    ["reservation-gap.bytes"] = Bytes(0),
                    ["reserved-to-committed.ratio"] = Ratio(1.5),
                    ["ephemeral.segment-count"] = Count(4),
                    ["ephemeral.avg-fill-percent"] = Pct(95.0),
                    ["segment.count"] = Count(5),
                    ["dump-pointer-size"] = Custom(8),
                    ["logical-heap.count"] = Count(1),
                    ["ratio-high-pressure.threshold"] = Ratio(5.0),
                    ["ratio-medium-pressure.threshold"] = Ratio(2.0),
                    ["thirty-two-bit-pressure.threshold-bytes"] = Bytes(1_500_000_000),
                    ["near-empty-region-fill-percent.threshold"] = Pct(10),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle(f => f.Title == "Ephemeral GC segments critically full");
        }
    }

    public sealed class DynamicAssemblyAccumulation
    {
        private readonly DynamicAssemblyAccumulationInsightRule _rule = new();

        [Fact]
        public async Task AboveThreshold_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.module-population", new Dictionary<string, Measure>
                {
                    ["total.count"] = Count(0), ["dynamic.count"] = Count(0), ["version-conflict.count"] = Count(0),
                    ["heavy-module-warning.threshold-bytes"] = Bytes(0), ["total-dynamic.count"] = Count(150),
                    ["total-domains.count"] = Count(2), ["anonymous-module.count"] = Count(10), ["unknown-identity-duplicate.count"] = Count(0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
        }
    }

    public sealed class JitHeapBloat
    {
        private readonly JitHeapBloatInsightRule _rule = new();

        [Fact]
        public async Task LargeHeapWithManyThreads_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.jit-activity", new Dictionary<string, Measure> { ["jit-heap.bytes"] = Bytes(600 * 1024 * 1024), ["jit-manager.count"] = Count(2) }),
                Obs("runtime.thread-triage", new Dictionary<string, Measure> { ["alive-thread.count"] = Count(150), ["blocked-thread.count"] = Count(0) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }

        [Fact]
        public async Task LargeHeapWithFewThreads_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.jit-activity", new Dictionary<string, Measure> { ["jit-heap.bytes"] = Bytes(600 * 1024 * 1024), ["jit-manager.count"] = Count(2) }),
                Obs("runtime.thread-triage", new Dictionary<string, Measure> { ["alive-thread.count"] = Count(10), ["blocked-thread.count"] = Count(0) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class BoxingGCCorrelation
    {
        private readonly BoxingGCCorrelationInsightRule _rule = new();

        [Fact]
        public async Task HighBoxedEnumCount_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("type.boxing-pressure", new Dictionary<string, Measure>
                {
                    ["total-boxed.count"] = Count(50_000), ["total-boxed.bytes"] = Bytes(1_000_000), ["avg-instance.bytes"] = Bytes(20),
                    ["boxed-enum.count"] = Count(15_000), ["boxed-enum.bytes"] = Bytes(0), ["nullable-boxed.count"] = Count(0),
                    ["nullable-boxed.bytes"] = Bytes(0), ["oversized-value.count"] = Count(0), ["padding-waste-type.count"] = Count(0),
                    ["padding-aggregate.bytes"] = Bytes(0), ["gen2-boxed.count"] = Count(0), ["gen2.percent"] = Pct(0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }
    }

    public sealed class JitModuleHotspot
    {
        private readonly JitModuleHotspotInsightRule _rule = new();

        private static ModuleRef Module(string name) => new() { SimpleName = name, Fidelity = MatchFidelity.Exact };

        private static Observation Frames(string module, int count) => Obs(
            "runtime.jit-module-frames",
            new Dictionary<string, Measure> { ["active-frame.count"] = Count(count) },
            subjects: [Module(module)]);

        private static Observation DiskFootprint(string module, double bytes) => Obs(
            "runtime.module-disk-footprint",
            new Dictionary<string, Measure> { ["disk.bytes"] = Bytes(bytes) },
            subjects: [Module(module)]);

        private static Observation ConflictGroup(string module) => Obs(
            "runtime.module-conflict-group", new Dictionary<string, Measure>(), subjects: [Module(module)]);

        [Fact]
        public async Task TopModuleConflictedAndSized_Fires()
        {
            var matched = new ObservationMatchSet(
            [
                Frames("MyApp.Plugins.dll", 200),
                Frames("Other.dll", 60),
                DiskFootprint("MyApp.Plugins.dll", 5_000_000),
                ConflictGroup("MyApp.Plugins.dll"),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Info);
            findings[0].Narrative.Should().Contain("MyApp.Plugins.dll").And.Contain("200").And.Contain("version conflict");
            findings[0].DerivedFrom.Should().HaveCount(3);
        }

        [Fact]
        public async Task ModuleNamesMatchCaseInsensitively()
        {
            var matched = new ObservationMatchSet([Frames("myapp.plugins.dll", 200), ConflictGroup("MyApp.Plugins.dll")]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
        }

        [Fact]
        public async Task BelowMinFrameHits_DoesNotFire()
        {
            var matched = new ObservationMatchSet([Frames("MyApp.Plugins.dll", 49), ConflictGroup("MyApp.Plugins.dll")]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }

        [Fact]
        public async Task TopModuleNotFlaggedByModuleAnalysis_DoesNotFire()
        {
            var matched = new ObservationMatchSet([Frames("MyApp.Plugins.dll", 200), ConflictGroup("Other.dll")]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }
}

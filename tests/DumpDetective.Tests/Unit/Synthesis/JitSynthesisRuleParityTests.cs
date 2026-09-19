using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="JitFindingGenerator"/> (old) and
/// <see cref="JitSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class JitSynthesisRuleParityTests
{
    private readonly JitFindingGenerator _oldGenerator = new();
    private readonly JitSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoSignals_ReturnsOverviewOnly() => AssertParity(
        BuildResult(totalJitHeapBytes: 100 * 1024 * 1024, unmanagedFrames: 10, managedFrames: 200, tieredMethods: 0, topMethods: []));

    [Fact]
    public Task WithSignals_ReturnsOverviewAndAllSignals() => AssertParity(BuildResult(
        totalJitHeapBytes: 800 * 1024 * 1024, unmanagedFrames: 400, managedFrames: 200, tieredMethods: 250,
        topMethods: [new JitMethodSnapshot("Foo.Bar()", "Foo", 0x1000, 70_000, 10_000, false, false)]));

    [Fact]
    public Task JitHeapBloat_OverviewSeverityEscalatesToWarning() => AssertParity(
        BuildResult(totalJitHeapBytes: 600 * 1024 * 1024, unmanagedFrames: 0, managedFrames: 100, tieredMethods: 0, topMethods: []));

    [Fact]
    public Task DeepThreadStack_EmitsWarningFinding() => AssertParity(BuildResult(
        totalJitHeapBytes: 1024, unmanagedFrames: 0, managedFrames: 10, tieredMethods: 0, topMethods: [],
        maxThreadFrameDepth: 600, maxThreadFrameDepthOSThreadId: 4242));

    [Fact]
    public Task DynamicMethodFrames_EmitsInfoFinding() => AssertParity(BuildResult(
        totalJitHeapBytes: 1024, unmanagedFrames: 0, managedFrames: 10, tieredMethods: 0, topMethods: [],
        dynamicMethodFrameCount: 5));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        totalJitHeapBytes: 800 * 1024 * 1024, unmanagedFrames: 400, managedFrames: 200, tieredMethods: 250,
        topMethods: [new JitMethodSnapshot("Foo.Bar()", "Foo", 0x1000, 70_000, 10_000, false, false)],
        maxThreadFrameDepth: 600, maxThreadFrameDepthOSThreadId: 99, dynamicMethodFrameCount: 5));

    private static JitDomainResult BuildResult(
        ulong totalJitHeapBytes,
        int unmanagedFrames,
        int managedFrames,
        int tieredMethods,
        IReadOnlyList<JitMethodSnapshot> topMethods,
        int maxThreadFrameDepth = 0,
        uint maxThreadFrameDepthOSThreadId = 0,
        int dynamicMethodFrameCount = 0)
    {
        return new JitDomainResult(
            TotalJitHeapBytes: totalJitHeapBytes,
            JitManagerCount: 3,
            ActiveMethodsOnStacks: managedFrames,
            DistinctMethodsOnStacks: managedFrames,
            TopLargestMethods: topMethods,
            TopActiveFrameTypes: [],
            TopActiveModulesByFrameHits: [],
            UnmanagedFrameCount: unmanagedFrames,
            ManagedFrameCount: managedFrames,
            ReadyToRunFrameCount: 0,
            DynamicMethodFrameCount: dynamicMethodFrameCount,
            TieredMethodCount: tieredMethods,
            MaxThreadFrameDepth: maxThreadFrameDepth,
            MaxThreadFrameDepthOSThreadId: maxThreadFrameDepthOSThreadId,
            LargeMethodThresholdBytes: 64 * 1024);
    }

    private async Task AssertParity(JitDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(JitDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.RuntimeJit, CapabilityVocabulary.RuntimeThreads };

        var measures = new Dictionary<string, Measure>
        {
            ["jit-heap.bytes"] = new Measure(result.TotalJitHeapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["active-methods.count"] = new Measure(result.ActiveMethodsOnStacks, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["distinct-methods.count"] = new Measure(result.DistinctMethodsOnStacks, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["tiered-methods.count"] = new Measure(result.TieredMethodCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["managed-frame.count"] = new Measure(result.ManagedFrameCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["unmanaged-frame.count"] = new Measure(result.UnmanagedFrameCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["ready-to-run-frame.count"] = new Measure(result.ReadyToRunFrameCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["dynamic-method-frame.count"] = new Measure(result.DynamicMethodFrameCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["jit-manager.count"] = new Measure(result.JitManagerCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["large-method.count"] = new Measure(result.TopLargestMethods.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["large-method.threshold-bytes"] = new Measure(result.LargeMethodThresholdBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["max-thread-frame-depth.count"] = new Measure(result.MaxThreadFrameDepth, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["max-thread-frame-depth.os-thread-id"] = new Measure(result.MaxThreadFrameDepthOSThreadId, MeasureUnit.Custom, MeasureSemantics.Absolute),
        };

        IReadOnlyList<EvidenceRef> evidence = [];
        if (result.TopLargestMethods.Count > 0)
        {
            JitMethodSnapshot top = result.TopLargestMethods[0];
            measures["top-method.hot-size-bytes"] = new Measure(top.HotSize, MeasureUnit.Bytes, MeasureSemantics.Absolute);
            measures["top-method.cold-size-bytes"] = new Measure(top.ColdSize, MeasureUnit.Bytes, MeasureSemantics.Absolute);
            evidence = [new EvidenceRef { Artifact = TestArtifact, Locator = "top-method", Description = top.Signature }];
        }

        return
        [
            new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.jit-activity",
                Subjects = [],
                When = Point(),
                Measures = measures,
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "JIT Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
                Evidence = evidence,
            },
        ];
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };
}

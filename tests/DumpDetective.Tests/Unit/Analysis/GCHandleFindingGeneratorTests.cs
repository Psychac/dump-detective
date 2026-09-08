using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class GCHandleFindingGeneratorTests
{
    [Fact]
    public void Generate_HandlesBelowThresholds_EmitsInfoPressureSummaryOnly()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(totalHandles: 100, pinnedHandleTargets: 10);

        var findings = gen.Generate(result);

        var summary = findings.Should().ContainSingle().Subject;
        summary.Title.Should().Be("GC handle pressure summary");
        summary.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_TotalHandlesAboveThreshold_EmitsWarningPressureSummary()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(totalHandles: 20_000, totalHandlesWarningThreshold: 10_000);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Title == "GC handle pressure summary")
            .Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_PinnedRetainedBytesBelowThreshold_EmitsNoHighPinnedBytesFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(pinnedRetainedBytes: 1024, pinnedRetainedBytesWarningThreshold: 100 * 1024 * 1024);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Title == "High pinned retained bytes");
    }

    [Fact]
    public void Generate_PinnedRetainedBytesAboveThreshold_EmitsHighPinnedBytesWarning()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            pinnedRetainedBytes: 200 * 1024 * 1024,
            pinnedRetainedBytesWarningThreshold: 100 * 1024 * 1024);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Title == "High pinned retained bytes").Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("MB");
    }

    [Fact]
    public void Generate_SohPinnedTargetsBelowThreshold_EmitsNoCompactionFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(pinnedSohObjectCount: 10, pinnedSohObjectCountWarningThreshold: 500);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("compaction"));
    }

    [Fact]
    public void Generate_SohPinnedTargetsAboveThreshold_EmitsCompactionWarning_CombiningPinnedAndAsyncPinned()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            pinnedSohObjectCount: 300,
            asyncPinnedSohObjectCount: 300,
            pinnedNonSohObjectCount: 5,
            pinnedSohObjectCountWarningThreshold: 500);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("compaction")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.MetricValue.Should().Be(600);
        finding.Evidence.Should().Contain("Pinned: 300").And.Contain("AsyncPinned: 300");
    }

    [Fact]
    public void Generate_RefCountedHandlesBelowThreshold_EmitsNoComInteropFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(refCountedHandleCount: 10, refCountedHandleCountWarningThreshold: 100);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("com-interop"));
    }

    [Fact]
    public void Generate_RefCountedHandlesAboveThreshold_EmitsComInteropWarning_NamingDominantType()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            refCountedHandleCount: 500,
            refCountedHandleCountWarningThreshold: 100,
            topRefCountedTargetTypes: [new NameCountEntry("Some.Com.Wrapper", 500)]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("com-interop")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("Some.Com.Wrapper");
    }

    [Fact]
    public void Generate_WeakLongGen2Concentration_BelowMinimumCount_EmitsNoFinalizationBacklogFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            weakLongGen2Count: 50,
            weakLongGen2MinimumCountThreshold: 100,
            weakLongGen2FractionWarningThreshold: 70.0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("finalization"));
    }

    [Fact]
    public void Generate_WeakLongGen2Concentration_AboveFractionAndCountThresholds_EmitsFinalizationBacklogWarning()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            weakLongGen0Count: 10,
            weakLongGen1Count: 10,
            weakLongGen2Count: 180,
            weakLongLohCount: 0,
            weakLongGen2MinimumCountThreshold: 100,
            weakLongGen2FractionWarningThreshold: 70.0);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("finalization")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.MetricValue.Should().BeApproximately(90.0, 0.1);
    }

    [Fact]
    public void Generate_WeakLongGen2Concentration_AboveCountButBelowFraction_EmitsNoFinalizationBacklogFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            weakLongGen0Count: 1000,
            weakLongGen1Count: 0,
            weakLongGen2Count: 150,
            weakLongLohCount: 0,
            weakLongGen2MinimumCountThreshold: 100,
            weakLongGen2FractionWarningThreshold: 70.0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("finalization"));
    }

    [Fact]
    public void Generate_NoDependentHandles_EmitsNoDependentRetentionFinding()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(dependentHandleCount: 0);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("dependent-handle"));
    }

    [Fact]
    public void Generate_DependentHandlesWithLowUnresolvedPercent_EmitsInfoRetentionSummary()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            dependentHandleCount: 100,
            dependentResolvedEdgeCount: 95,
            dependentUnresolvedTargetCount: 5,
            dependentUnresolvedPercent: 5.0,
            dependentUnresolvedPercentWarningThreshold: 50.0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Tags.Contains("dependent-handle"))
            .Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_DependentHandlesWithHighUnresolvedPercent_EmitsWarningRetentionSummary()
    {
        var gen = new GCHandleFindingGenerator();
        var result = BuildResult(
            dependentHandleCount: 100,
            dependentResolvedEdgeCount: 20,
            dependentUnresolvedTargetCount: 80,
            dependentUnresolvedPercent: 80.0,
            dependentUnresolvedPercentWarningThreshold: 50.0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle(f => f.Tags.Contains("dependent-handle"))
            .Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    private static GCHandleDomainResult BuildResult(
        int totalHandles = 0,
        int pinnedHandleTargets = 0,
        int totalHandlesWarningThreshold = 10_000,
        int pinnedHandleTargetsWarningThreshold = 1_000,
        ulong pinnedRetainedBytes = 0,
        ulong pinnedRetainedBytesWarningThreshold = 100 * 1024 * 1024,
        int pinnedSohObjectCount = 0,
        int pinnedNonSohObjectCount = 0,
        int asyncPinnedSohObjectCount = 0,
        int asyncPinnedNonSohObjectCount = 0,
        int pinnedSohObjectCountWarningThreshold = 500,
        int refCountedHandleCount = 0,
        int refCountedHandleCountWarningThreshold = 100,
        IReadOnlyList<NameCountEntry>? topRefCountedTargetTypes = null,
        int weakShortGen0Count = 0,
        int weakShortGen1Count = 0,
        int weakShortGen2Count = 0,
        int weakShortLohCount = 0,
        int weakLongGen0Count = 0,
        int weakLongGen1Count = 0,
        int weakLongGen2Count = 0,
        int weakLongLohCount = 0,
        double weakLongGen2FractionWarningThreshold = 70.0,
        int weakLongGen2MinimumCountThreshold = 100,
        int dependentHandleCount = 0,
        int dependentResolvedEdgeCount = 0,
        int dependentUnresolvedTargetCount = 0,
        double dependentUnresolvedPercent = 0,
        double dependentUnresolvedPercentWarningThreshold = 50.0)
    {
        return new GCHandleDomainResult(
            TotalHandles: totalHandles,
            StrongLikeHandles: totalHandles,
            WeakLikeHandles: 0,
            PinnedHandleTargets: pinnedHandleTargets,
            PinnedRetainedBytes: pinnedRetainedBytes,
            PinnedSohObjectCount: pinnedSohObjectCount,
            PinnedNonSohObjectCount: pinnedNonSohObjectCount,
            AsyncPinnedSohObjectCount: asyncPinnedSohObjectCount,
            AsyncPinnedNonSohObjectCount: asyncPinnedNonSohObjectCount,
            RefCountedHandleCount: refCountedHandleCount,
            TopRefCountedTargetTypes: topRefCountedTargetTypes,
            WeakShortGen0Count: weakShortGen0Count,
            WeakShortGen1Count: weakShortGen1Count,
            WeakShortGen2Count: weakShortGen2Count,
            WeakShortLohCount: weakShortLohCount,
            WeakLongGen0Count: weakLongGen0Count,
            WeakLongGen1Count: weakLongGen1Count,
            WeakLongGen2Count: weakLongGen2Count,
            WeakLongLohCount: weakLongLohCount,
            DependentHandleCount: dependentHandleCount,
            DependentResolvedEdgeCount: dependentResolvedEdgeCount,
            DependentUnresolvedTargetCount: dependentUnresolvedTargetCount,
            DependentUnresolvedPercent: dependentUnresolvedPercent,
            TotalHandlesWarningThreshold: totalHandlesWarningThreshold,
            PinnedHandleTargetsWarningThreshold: pinnedHandleTargetsWarningThreshold,
            PinnedRetainedBytesWarningThreshold: pinnedRetainedBytesWarningThreshold,
            PinnedSohObjectCountWarningThreshold: pinnedSohObjectCountWarningThreshold,
            RefCountedHandleCountWarningThreshold: refCountedHandleCountWarningThreshold,
            WeakLongGen2FractionWarningThreshold: weakLongGen2FractionWarningThreshold,
            WeakLongGen2MinimumCountThreshold: weakLongGen2MinimumCountThreshold,
            DependentUnresolvedPercentWarningThreshold: dependentUnresolvedPercentWarningThreshold);
    }
}

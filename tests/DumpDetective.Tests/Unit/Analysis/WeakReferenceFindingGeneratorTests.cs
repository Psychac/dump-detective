using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class WeakReferenceFindingGeneratorTests
{
    [Fact]
    public void Generate_NoSignals_ReturnsOverviewOnly()
    {
        var gen = new WeakReferenceFindingGenerator();
        var result = BuildResult(total: 1000, alive: 700, dead: 300, ratio: 0.3, stale: 20, dependentDeadKeys: 0);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Title.Should().Contain("overview");
    }

    [Fact]
    public void Generate_WithMultipleSignals_ReturnsAllSorted()
    {
        var gen = new WeakReferenceFindingGenerator();
        // Use 50k dead handles to trigger both ratio and absolute count thresholds
        var result = BuildResult(total: 55000, alive: 5000, dead: 50000, ratio: 0.91, stale: 900, dependentDeadKeys: 25);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(4);
        findings[0].Title.Should().Contain("overview");
        findings[1].Severity.Should().Be(FindingSeverity.Critical); // High proportion (ratio >= 0.8)
        findings[2].Severity.Should().Be(FindingSeverity.Warning);  // High absolute count (50k, < 100k threshold)
        findings[3].Severity.Should().Be(FindingSeverity.Info);     // Dependent handles
    }

    private static WeakReferenceDomainResult BuildResult(
        int total,
        int alive,
        int dead,
        double ratio,
        int stale,
        int dependentDeadKeys)
    {
        return new WeakReferenceDomainResult(
            TotalWeakHandles: total,
            AliveWeakTargets: alive,
            DeadWeakTargets: dead,
            DeadTargetRatio: ratio,
            WeakHandleKinds: [],
            WeakReferenceObjectCount: total,
            WeakReferenceObjectBytes: (ulong)(total * 64),
            StaleWrapperCount: stale,
            TopWeakTargetTypes: [],
            TopStaleWrapperHolderTypes: [],
            DependentHandleDeadKeyCount: dependentDeadKeys,
            ScanCapped: false,
            ScanCapUsed: 50000);
    }
}

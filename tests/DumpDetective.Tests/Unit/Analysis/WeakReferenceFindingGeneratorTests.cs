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
    public void Generate_WithSignals_ReturnsOverviewAndTopDetail()
    {
        var gen = new WeakReferenceFindingGenerator();
        var result = BuildResult(total: 5000, alive: 300, dead: 4700, ratio: 0.94, stale: 900, dependentDeadKeys: 25);

        var findings = gen.Generate(result);

        findings.Should().HaveCount(2);
        findings[0].Title.Should().Contain("overview");
        findings[1].Title.Should().Contain("dead weak handle targets");
        findings[0].Severity.Should().Be(FindingSeverity.Critical);
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
            ScanCapped: false);
    }
}

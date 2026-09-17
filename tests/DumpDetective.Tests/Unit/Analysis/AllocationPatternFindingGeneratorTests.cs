using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AllocationPatternFindingGeneratorTests
{
    private readonly AllocationPatternFindingGenerator _gen = new();

    [Fact]
    public void Generate_LowPressure_EmitsNoFindings()
    {
        AllocationPatternDomainResult result = BuildResult(GCPressureLevel.Low);

        var findings = _gen.Generate(result);

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Generate_ModeratePressure_EmitsInfoFinding()
    {
        AllocationPatternDomainResult result = BuildResult(GCPressureLevel.Moderate);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_HighPressure_EmitsWarningFinding()
    {
        AllocationPatternDomainResult result = BuildResult(GCPressureLevel.High);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_CriticalPressure_EmitsCriticalFinding()
    {
        AllocationPatternDomainResult result = BuildResult(GCPressureLevel.Critical);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_WithFinalizableTypes_IncludesFinalizableCountInEvidence()
    {
        AllocationPatternDomainResult result = BuildResult(GCPressureLevel.Critical, finalizableTypeCount: 12, finalizableBytes: 4096);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Evidence.Should().Contain("12 finalizable type(s)");
    }

    [Fact]
    public void Generate_WithTopLongLivedType_IncludesTypeNameInEvidence()
    {
        AllocationPatternDomainResult result = BuildResult(
            GCPressureLevel.Critical,
            topLongLivedTypes: [new TypeAllocationProfile("MyApp.Cache.Node", 0, 0, 500, 1.0, TypeProfile.Retained, 100_000, 1.0)]);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Evidence.Should().Contain("MyApp.Cache.Node");
    }

    // GCPressure is derived (AllocationPatternPressureClassifier) from Gen0CountPct/Gen2CountPct/
    // LohSizePct, not injected directly — these triples are hand-picked to land in each band via
    // score = (100 - gen0CountPct) * 0.3 + gen2CountPct * 0.5 + lohSizePct * 0.2.
    private static AllocationPatternDomainResult BuildResult(
        GCPressureLevel pressure,
        int finalizableTypeCount = 0,
        ulong finalizableBytes = 0,
        IReadOnlyList<TypeAllocationProfile>? topLongLivedTypes = null)
    {
        (double gen0CountPct, double gen2CountPct, double lohSizePct) = pressure switch
        {
            GCPressureLevel.Low => (100.0, 0.0, 0.0),      // score 0
            GCPressureLevel.Moderate => (40.0, 30.0, 10.0), // score 35
            GCPressureLevel.High => (0.0, 60.0, 30.0),       // score 66
            GCPressureLevel.Critical => (0.0, 80.0, 50.0),   // score 80
            _ => throw new ArgumentOutOfRangeException(nameof(pressure))
        };

        return new(
            Gen0CountPct: gen0CountPct, Gen1CountPct: 20, Gen2CountPct: gen2CountPct, LohCountPct: 10,
            Gen0SizePct: 30, Gen1SizePct: 20, Gen2SizePct: 40, LohSizePct: lohSizePct,
            TotalManagedBytes: 1_000_000_000,
            Gen0Bytes: 300_000_000, Gen1Bytes: 200_000_000, Gen2Bytes: 400_000_000, LohBytes: 100_000_000,
            Profile: AllocationProfile.Mixed,
            TopTransientTypes: [],
            TopShortishTypes: [],
            TopLongLivedTypes: topLongLivedTypes ?? [],
            TopHighGen1SurvivorTypes: [],
            FinalizableTypeCount: finalizableTypeCount,
            FinalizableBytes: finalizableBytes);
    }
}

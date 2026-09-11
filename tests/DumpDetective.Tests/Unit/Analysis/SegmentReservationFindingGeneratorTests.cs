using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class SegmentReservationFindingGeneratorTests
{
    private readonly SegmentReservationFindingGenerator _gen = new();

    [Fact]
    public void Generate_AddressSpacePressureRisk_32Bit_EmitsCriticalFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            addressSpacePressureRisk: true,
            dumpPointerSize: 4,
            pressureRiskReason: "Reserved address space is within 10% of the 2 GB 32-bit limit.");

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("address-space")
            && f.Severity == FindingSeverity.Critical
            && f.Evidence.Contains("2 GB"));
    }

    [Fact]
    public void Generate_AddressSpacePressureRisk_64Bit_EmitsWarningFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            addressSpacePressureRisk: true,
            dumpPointerSize: 8,
            pressureRiskReason: "Reserved/committed ratio is unusually high for a 64-bit process.");

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("address-space")
            && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_RatioAboveHighThreshold_EmitsCriticalFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            reservedToCommittedRatio: 10.0,
            ratioHighPressureThreshold: 6.0,
            ratioMediumPressureThreshold: 3.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("reservation-ratio")
            && f.Severity == FindingSeverity.Critical
            && f.Title.Contains("10.0x"));
    }

    [Fact]
    public void Generate_RatioAboveMediumThreshold_BelowHighThreshold_EmitsWarningFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            reservedToCommittedRatio: 4.0,
            ratioHighPressureThreshold: 6.0,
            ratioMediumPressureThreshold: 3.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("reservation-ratio")
            && f.Severity == FindingSeverity.Warning
            && f.Title.Contains("Elevated segment reservation"));
    }

    [Fact]
    public void Generate_RatioAtOrBelowMediumThreshold_EmitsNoReservationRatioFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            reservedToCommittedRatio: 3.0,
            ratioHighPressureThreshold: 6.0,
            ratioMediumPressureThreshold: 3.0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("reservation-ratio"));
    }

    [Fact]
    public void Generate_EphemeralFillAboveNinety_EmitsWarningFinding()
    {
        SegmentReservationDomainResult result = BuildResult(avgEphemeralFillPct: 95.0, ephemeralSegmentCount: 3);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("ephemeral")
            && f.Severity == FindingSeverity.Warning
            && f.Evidence.Contains("95.0%"));
    }

    [Fact]
    public void Generate_EphemeralFillAtNinety_EmitsNoEphemeralFinding()
    {
        SegmentReservationDomainResult result = BuildResult(avgEphemeralFillPct: 90.0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("ephemeral"));
    }

    [Fact]
    public void Generate_NearEmptyRegions_ReclaimableAboveThreshold_EmitsWarningFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            isRegionsBased: true,
            nearEmptyRegionCount: 5,
            nearEmptyRegionCommittedBytes: 100 * 1024 * 1024UL);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("decommit")
            && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NearEmptyRegions_ReclaimableAtOrBelowThreshold_EmitsInfoFinding()
    {
        SegmentReservationDomainResult result = BuildResult(
            isRegionsBased: true,
            nearEmptyRegionCount: 2,
            nearEmptyRegionCommittedBytes: 10 * 1024 * 1024UL);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f =>
            f.Tags.Contains("decommit")
            && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_RegionsBased_NoNearEmptyRegions_EmitsNoDecommitFinding()
    {
        SegmentReservationDomainResult result = BuildResult(isRegionsBased: true, nearEmptyRegionCount: 0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("decommit"));
    }

    [Fact]
    public void Generate_NoTriggeredBranches_StillEmitsInfoOverviewFinding()
    {
        SegmentReservationDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Should().Match<InsightFinding>(f =>
            f.Severity == FindingSeverity.Info && f.Tags.Contains("reserved"));
    }

    private static SegmentReservationDomainResult BuildResult(
        bool addressSpacePressureRisk = false,
        string pressureRiskReason = "",
        int dumpPointerSize = 8,
        double reservedToCommittedRatio = 1.5,
        double ratioHighPressureThreshold = 6.0,
        double ratioMediumPressureThreshold = 3.0,
        double avgEphemeralFillPct = 50.0,
        int ephemeralSegmentCount = 2,
        bool isRegionsBased = false,
        int nearEmptyRegionCount = 0,
        ulong nearEmptyRegionCommittedBytes = 0) => new(
        TotalCommittedBytes: 100_000_000,
        TotalReservedBytes: 150_000_000,
        ReservationGapBytes: 50_000_000,
        ReservedToCommittedRatio: reservedToCommittedRatio,
        EphemeralSegmentCount: ephemeralSegmentCount,
        AvgEphemeralFillPct: avgEphemeralFillPct,
        MaxEphemeralFillPct: avgEphemeralFillPct,
        NonEphemeralSohSegmentCount: 1,
        TotalSegmentCount: 5,
        SegmentTable: [],
        ReservedByLogicalHeap: new Dictionary<int, ulong> { [0] = 150_000_000 },
        CommittedByLogicalHeap: new Dictionary<int, ulong> { [0] = 100_000_000 },
        ReservedByKind: new Dictionary<HeapSegmentKind, ulong>(),
        CommittedByKind: new Dictionary<HeapSegmentKind, ulong>(),
        SegmentCountByKind: new Dictionary<HeapSegmentKind, int>(),
        AddressSpacePressureRisk: addressSpacePressureRisk,
        PressureRiskReason: pressureRiskReason,
        RatioHighPressureThreshold: ratioHighPressureThreshold,
        RatioMediumPressureThreshold: ratioMediumPressureThreshold,
        DumpPointerSize: dumpPointerSize,
        IsServerGc: false,
        LogicalHeapCount: 1,
        IsRegionsBased: isRegionsBased,
        RegionStats: [],
        NearEmptyRegionCount: nearEmptyRegionCount,
        NearEmptyRegionCommittedBytes: nearEmptyRegionCommittedBytes,
        NearEmptyRegionFillPctThreshold: 10.0);
}

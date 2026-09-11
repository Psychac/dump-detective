using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ArrayFindingGeneratorTests
{
    private readonly ArrayFindingGenerator _gen = new();

    [Fact]
    public void Generate_LohArrayBytesAtOrAboveTwoGb_EmitsCriticalFinding()
    {
        ArrayDomainResult result = BuildResult(lohArrayBytes: 2_000_000_000UL, lohArrayCount: 5);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("LOH array pressure") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_LohArrayBytesBetweenWarningAndCritical_EmitsWarningFinding()
    {
        ArrayDomainResult result = BuildResult(lohArrayBytes: 600_000_000UL, lohArrayCount: 3);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("LOH array pressure") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LohArrayBytesBelowWarning_EmitsNoLohFinding()
    {
        ArrayDomainResult result = BuildResult(lohArrayBytes: 100_000_000UL);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Title.Contains("LOH array pressure"));
    }

    [Fact]
    public void Generate_MultiDimArrayCountAtOrAboveThreshold_EmitsWarningFinding()
    {
        ArrayDomainResult result = BuildResult(multiDimArrayCount: 1_500);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("multidim") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_SparseArrayAboveThresholds_EmitsWarningFinding()
    {
        ArrayDomainResult result = BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 20_000_000UL)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("sparse") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_SparseArrayBelowRatioThreshold_EmitsNoSparseFinding()
    {
        ArrayDomainResult result = BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 500, SparseRatio: 0.5, WastedBytes: 20_000_000UL)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("sparse"));
    }

    [Fact]
    public void Generate_PinnedArrayBytesAtOrAboveCritical_EmitsCriticalFinding()
    {
        ArrayDomainResult result = BuildResult(pinnedArrayBytes: 200_000_000UL, pinnedArrayCount: 150);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("pinned") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_PinnedArrayCountAboveThreshold_BelowCriticalBytes_EmitsWarningFinding()
    {
        ArrayDomainResult result = BuildResult(pinnedArrayCount: 150, pinnedArrayBytes: 1_000_000UL);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("pinned") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_AllBelowThresholds_EmitsNoFindings()
    {
        ArrayDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().BeEmpty();
    }

    private static ArrayDomainResult BuildResult(
        ulong lohArrayBytes = 0,
        int lohArrayCount = 0,
        int multiDimArrayCount = 0,
        ulong multiDimArrayBytes = 0,
        IReadOnlyList<SparseArrayEntry>? topSparseArrays = null,
        int pinnedArrayCount = 0,
        ulong pinnedArrayBytes = 0) => new(
        TotalArrayObjects: 10_000,
        TotalArrayBytes: 1_000_000_000,
        MultiDimArrayCount: multiDimArrayCount,
        MultiDimArrayBytes: multiDimArrayBytes,
        LohArrayCount: lohArrayCount,
        LohArrayBytes: lohArrayBytes,
        TopArrayTypesBySize: [],
        TopLargeArrays: [],
        TopSparseArrays: topSparseArrays ?? [],
        PinnedArrayCount: pinnedArrayCount,
        PinnedArrayBytes: pinnedArrayBytes);
}

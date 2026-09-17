using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.FindingGenerators;

using Xunit;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Freezes <see cref="ArrayFindingGenerator"/>'s exact output — full <c>Finding</c> text, not just
/// severity/tags — as the parity oracle for its eventual <c>ISynthesisRule</c> replacement. First
/// entry in the per-analyzer playbook in
/// docs/refactor/modularity/phase-5-observations-synthesis.md's "Before step 2" prerequisites.
/// </summary>
/// <remarks>
/// Reuses <see cref="ArrayFindingGeneratorTests"/>'s existing boundary fixtures (playbook step 1)
/// plus two new ones the existing suite never exercised: it never populates
/// <see cref="ArrayDomainResult.TopLargeArrays"/> or <see cref="ArrayDomainResult.TopPinnedArrays"/>,
/// so every existing fixture hits the generator's "N/A" narrative branch and the actual-type-name
/// branch was never frozen anywhere. That's a narrative-interpolation gap, not a threshold gap —
/// exactly the class of gap the widened audit scope (playbook step 3) exists to catch.
/// </remarks>
public sealed class ArrayFindingGeneratorGoldenTests : GoldenTestBase
{
    private readonly ArrayFindingGenerator _gen = new();

    [Fact]
    public void LohArrayBytesAtOrAboveTwoGb_MatchesGoldenOutput() =>
        Approve(BuildResult(lohArrayBytes: 2_000_000_000UL, lohArrayCount: 5));

    [Fact]
    public void LohArrayBytesBetweenWarningAndCritical_MatchesGoldenOutput() =>
        Approve(BuildResult(lohArrayBytes: 600_000_000UL, lohArrayCount: 3));

    [Fact]
    public void LohArrayBytesBelowWarning_MatchesGoldenOutput() =>
        Approve(BuildResult(lohArrayBytes: 100_000_000UL));

    [Fact]
    public void LohArrayWithTopLargeArrayPopulated_MatchesGoldenOutput() =>
        Approve(BuildResult(
            lohArrayBytes: 2_000_000_000UL,
            lohArrayCount: 5,
            topLargeArrays: [new LargeArrayEntry(Address: 0x2000, ElementTypeName: "System.Byte", Length: 250_000_000, Rank: 1, Size: 2_000_000_000UL)]));

    [Fact]
    public void MultiDimArrayCountAtOrAboveThreshold_MatchesGoldenOutput() =>
        Approve(BuildResult(multiDimArrayCount: 1_500));

    [Fact]
    public void MultiDimArrayBytesAtOrAboveThreshold_BelowCountThreshold_MatchesGoldenOutput() =>
        Approve(BuildResult(multiDimArrayCount: 10, multiDimArrayBytes: 150_000_000UL));

    [Fact]
    public void MultiDimArrayBelowBothThresholds_MatchesGoldenOutput() =>
        Approve(BuildResult(multiDimArrayCount: 10, multiDimArrayBytes: 1_000_000UL));

    [Fact]
    public void SparseArrayAboveThresholds_MatchesGoldenOutput() =>
        Approve(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 20_000_000UL),
        ]));

    [Fact]
    public void SparseArrayBelowRatioThreshold_MatchesGoldenOutput() =>
        Approve(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 500, SparseRatio: 0.5, WastedBytes: 20_000_000UL),
        ]));

    [Fact]
    public void SparseArrayExceedsMaxFindingsCap_MatchesGoldenOutput() =>
        Approve(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 20_000_000UL),
            new SparseArrayEntry(Address: 0x2000, ElementTypeName: "System.Int64", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 21_000_000UL),
            new SparseArrayEntry(Address: 0x3000, ElementTypeName: "System.Double", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 22_000_000UL),
            new SparseArrayEntry(Address: 0x4000, ElementTypeName: "System.String", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 23_000_000UL),
        ]));

    [Fact]
    public void PinnedArrayBytesAtOrAboveCritical_MatchesGoldenOutput() =>
        Approve(BuildResult(pinnedArrayCount: 150, pinnedArrayBytes: 200_000_000UL));

    [Fact]
    public void PinnedArrayCountAboveThreshold_BelowCriticalBytes_MatchesGoldenOutput() =>
        Approve(BuildResult(pinnedArrayCount: 150, pinnedArrayBytes: 1_000_000UL));

    [Fact]
    public void PinnedArrayWithTopPinnedArrayPopulated_MatchesGoldenOutput() =>
        Approve(BuildResult(
            pinnedArrayCount: 150,
            pinnedArrayBytes: 200_000_000UL,
            topPinnedArrays: [new LargeArrayEntry(Address: 0x3000, ElementTypeName: "System.Byte", Length: 50_000_000, Rank: 1, Size: 200_000_000UL)]));

    [Fact]
    public void AllBelowThresholds_MatchesGoldenOutput() =>
        Approve(BuildResult());

    private void Approve(ArrayDomainResult result, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var findings = _gen.Generate(result);
        ApproveGoldenOutput(SerializeToJson(findings), $"Synthesis/ArrayFindingGenerator/{testName}");
    }

    private static ArrayDomainResult BuildResult(
        ulong lohArrayBytes = 0,
        int lohArrayCount = 0,
        int multiDimArrayCount = 0,
        ulong multiDimArrayBytes = 0,
        IReadOnlyList<LargeArrayEntry>? topLargeArrays = null,
        IReadOnlyList<SparseArrayEntry>? topSparseArrays = null,
        int pinnedArrayCount = 0,
        ulong pinnedArrayBytes = 0,
        IReadOnlyList<LargeArrayEntry>? topPinnedArrays = null) => new(
        TotalArrayObjects: 10_000,
        TotalArrayBytes: 1_000_000_000,
        MultiDimArrayCount: multiDimArrayCount,
        MultiDimArrayBytes: multiDimArrayBytes,
        LohArrayCount: lohArrayCount,
        LohArrayBytes: lohArrayBytes,
        TopArrayTypesBySize: [],
        TopLargeArrays: topLargeArrays ?? [],
        TopSparseArrays: topSparseArrays ?? [],
        PinnedArrayCount: pinnedArrayCount,
        PinnedArrayBytes: pinnedArrayBytes,
        TopPinnedArrays: topPinnedArrays);
}

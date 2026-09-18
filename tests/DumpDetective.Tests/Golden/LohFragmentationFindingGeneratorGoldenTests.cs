using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.FindingGenerators;

using Xunit;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Freezes <see cref="LohFragmentationFindingGenerator"/>'s exact output as the parity oracle for
/// its eventual <c>ISynthesisRule</c> replacement — playbook step 2, reusing
/// <see cref="LohFragmentationFindingGeneratorTests"/>'s existing boundary fixtures (all five
/// threshold cases already covered there, so no new fixtures needed). See
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class LohFragmentationFindingGeneratorGoldenTests : GoldenTestBase
{
    private readonly LohFragmentationFindingGenerator _gen = new();

    [Fact]
    public void FragmentationAtOrAboveThirty_MatchesGoldenOutput() => Approve(BuildResult(35.0));

    [Fact]
    public void FragmentationAtThirtyBoundary_MatchesGoldenOutput() => Approve(BuildResult(30.0));

    [Fact]
    public void FragmentationBetweenFifteenAndThirty_MatchesGoldenOutput() => Approve(BuildResult(20.0));

    [Fact]
    public void FragmentationAtFifteenBoundary_MatchesGoldenOutput() => Approve(BuildResult(15.0));

    [Fact]
    public void FragmentationBelowFifteen_MatchesGoldenOutput() => Approve(BuildResult(5.0));

    private void Approve(LohFragmentationDomainResult result, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var findings = _gen.Generate(result);
        ApproveGoldenOutput(SerializeToJson(findings), $"Synthesis/LohFragmentationFindingGenerator/{testName}");
    }

    private static LohFragmentationDomainResult BuildResult(double fragmentationPercent) => new(
        SegmentCount: 4,
        TotalBytes: 400_000_000,
        FreeBytes: (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        UsedBytes: 400_000_000 - (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        FreeBlockCount: 10,
        FragmentationPercent: fragmentationPercent,
        LargestFreeBlock: 1_000_000);
}

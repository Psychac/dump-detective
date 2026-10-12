using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class CoverageMergerTests
{
    private static readonly ArtifactId Dump = new("dump-1");

    [Fact]
    public void Merge_ReplacesTheSessionRecordForTheSameCapabilityAndArtifact()
    {
        CoverageRecord complete = new(new Capability("heap.root-paths"), Dump, CoverageStatus.Complete, []);
        CoverageRecord other = new(new Capability("heap.types"), Dump, CoverageStatus.Complete, []);
        CoverageRecord partial = new(new Capability("heap.root-paths"), Dump, CoverageStatus.Partial, [], new CoverageBoundary(1, 2, "x", "y"));

        IReadOnlyList<CoverageRecord> merged = CoverageMerger.Merge([complete, other], [partial]);

        merged.Should().Equal(partial, other);
    }

    [Fact]
    public void Merge_AddsARuleRecordForACapabilityTheSessionDidNotList()
    {
        CoverageRecord partial = new(new Capability("heap.root-paths"), Dump, CoverageStatus.Partial, []);

        CoverageMerger.Merge([], [partial]).Should().Equal(partial);
    }

    [Fact]
    public void Merge_ReturnsTheSessionRecordsUnchanged_WhenNoRuleProducedAny()
    {
        CoverageRecord complete = new(new Capability("heap.types"), Dump, CoverageStatus.Complete, []);

        CoverageMerger.Merge([complete], []).Should().Equal(complete);
    }
}

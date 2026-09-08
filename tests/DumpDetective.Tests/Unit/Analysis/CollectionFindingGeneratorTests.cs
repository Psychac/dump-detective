using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionFindingGeneratorTests
{
    private static CollectionDomainResult BuildResult(
        Dictionary<CollectionKind, int>? countsByKind,
        Dictionary<CollectionKind, ulong>? bytesByKind,
        Dictionary<string, int>? countsByElementType = null,
        Dictionary<string, ulong>? bytesByElementType = null) =>
        new(
            TotalCollections: 100,
            Dictionaries: 20,
            Lists: 20,
            ArrayLists: 0,
            Stacks: 0,
            SortedLists: 0,
            SortedSets: 0,
            HashSets: 0,
            Queues: 10,
            TotalWastedMemory: 10_000,
            WastefulCollectionCount: 6,
            WasteCountsByKind: countsByKind,
            WasteBytesByKind: bytesByKind,
            WasteCountsByElementType: countsByElementType,
            WasteBytesByElementType: bytesByElementType);

    [Fact]
    public void Generate_Evidence_ListsKindsByWastedBytesDescending()
    {
        var result = BuildResult(
            countsByKind: new() { [CollectionKind.Dictionary] = 5, [CollectionKind.Queue] = 1 },
            bytesByKind: new() { [CollectionKind.Dictionary] = 2_000, [CollectionKind.Queue] = 8_000 });

        InsightFinding finding = new CollectionFindingGenerator().Generate(result).Single();

        finding.Evidence.Should().Contain("Queue: 1 /");
        finding.Evidence.Should().Contain("Dictionary: 5 /");
        finding.Evidence.IndexOf("Queue", StringComparison.Ordinal)
            .Should().BeLessThan(finding.Evidence.IndexOf("Dictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_Recommendation_TargetsKindDominatingWastedBytes()
    {
        var result = BuildResult(
            countsByKind: new() { [CollectionKind.List] = 50, [CollectionKind.Queue] = 1 },
            bytesByKind: new() { [CollectionKind.List] = 1_000, [CollectionKind.Queue] = 9_000 });

        InsightFinding finding = new CollectionFindingGenerator().Generate(result).Single();

        finding.Recommendation.Should().Contain("Queue");
    }

    [Fact]
    public void Generate_NoPerKindData_OmitsBreakdown()
    {
        InsightFinding finding = new CollectionFindingGenerator().Generate(BuildResult(null, null)).Single();

        finding.Evidence.Should().NotContain("(");
    }

    [Fact]
    public void Generate_Evidence_NamesElementTypeDominatingWastedBytes()
    {
        var result = BuildResult(
            countsByKind: null,
            bytesByKind: null,
            countsByElementType: new() { ["System.String"] = 2, ["System.Int32"] = 50 },
            bytesByElementType: new() { ["System.String"] = 9_000, ["System.Int32"] = 1_000 });

        InsightFinding finding = new CollectionFindingGenerator().Generate(result).Single();

        finding.Evidence.Should().Contain("Dominant wasted element type: System.String");
    }

    [Fact]
    public void Generate_NoElementTypeData_OmitsDominantElementTypeNote()
    {
        InsightFinding finding = new CollectionFindingGenerator().Generate(BuildResult(null, null)).Single();

        finding.Evidence.Should().NotContain("Dominant wasted element type");
    }
}

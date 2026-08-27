using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionKindClassificationTests
{
    [Theory]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String],[System.Int32]]", CollectionKind.Dictionary)]
    [InlineData("System.Collections.Generic.List`1[[System.String]]", CollectionKind.List)]
    [InlineData("System.Collections.Generic.HashSet`1[[System.String]]", CollectionKind.HashSet)]
    [InlineData("System.Collections.Generic.Queue`1[[System.String]]", CollectionKind.Queue)]
    [InlineData("System.Collections.ArrayList", CollectionKind.ArrayList)]
    [InlineData("System.Collections.Generic.Stack`1[[System.String]]", CollectionKind.Stack)]
    [InlineData("System.Collections.Generic.SortedList`2[[System.String],[System.Int32]]", CollectionKind.SortedList)]
    [InlineData("System.Collections.Generic.SortedSet`1[[System.String]]", CollectionKind.SortedSet)]
    [InlineData("System.Collections.Immutable.ImmutableArray`1[[System.String]]", CollectionKind.ImmutableArray)]
    public void ClassifyCollectionTypeName_RecognizesBclType(string typeName, CollectionKind expected)
    {
        CollectionAnalyzer.ClassifyCollectionTypeName(typeName).Should().Be(expected);
    }

    [Fact]
    public void ClassifyCollectionTypeName_ImmutableArrayBuilder_ReturnsBuilderKind()
    {
        const string typeName = "System.Collections.Immutable.ImmutableArray`1+Builder[[System.String]]";

        CollectionAnalyzer.ClassifyCollectionTypeName(typeName).Should().Be(CollectionKind.ImmutableArrayBuilder);
    }

    [Theory]
    [InlineData("System.Collections.Immutable.ImmutableList`1+Builder[[System.String]]")]
    [InlineData("System.Collections.Immutable.ImmutableDictionary`2+Builder[[System.String],[System.Int32]]")]
    public void ClassifyCollectionTypeName_NonArrayImmutableBuilder_IsUnclassified(string typeName)
    {
        // Only ImmutableArray<T>.Builder is array-backed with reclaimable slack; every other
        // immutable-collection builder wraps a tree/node structure with no spare capacity to report.
        CollectionAnalyzer.ClassifyCollectionTypeName(typeName).Should().Be(CollectionKind.None);
    }

    [Fact]
    public void ClassifyCollectionTypeName_OtherNestedType_IsUnclassified()
    {
        CollectionAnalyzer.ClassifyCollectionTypeName("System.Collections.Generic.Dictionary`2+Entry[[System.String],[System.Int32]]")
            .Should().Be(CollectionKind.None);
    }

    [Fact]
    public void ClassifyCollectionTypeName_ConcurrentCollection_IsUnclassified()
    {
        CollectionAnalyzer.ClassifyCollectionTypeName("System.Collections.Concurrent.ConcurrentDictionary`2[[System.String],[System.Int32]]")
            .Should().Be(CollectionKind.None);
    }

    [Fact]
    public void ClassifyCollectionTypeName_NonBclType_IsUnclassified()
    {
        CollectionAnalyzer.ClassifyCollectionTypeName("MyApp.Models.CustomCollection`1[[System.String]]")
            .Should().Be(CollectionKind.None);
    }
}

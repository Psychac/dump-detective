using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// P3-2: <see cref="StringAnalyzer"/> selects up to N top duplicate patterns (by wasted bytes,
/// in caller-supplied order) that have a resolvable sample address as candidates for a GC
/// root-path search. The search itself needs a live ClrHeap/IHeapAnalysisCache and isn't unit
/// tested here (see BuildRetentionPaths' null-cache/zero-count short circuits below, which are).
/// </summary>
public sealed class StringAnalyzerRetentionPathTests
{
    private static readonly MethodInfo SelectCandidatesMethod = typeof(StringAnalyzer).GetMethod(
        "SelectRetentionPathCandidates", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo BuildRetentionPathsMethod = typeof(StringAnalyzer).GetMethod(
        "BuildRetentionPaths", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IReadOnlyList<(DuplicateStringSnapshot Duplicate, ulong Address)> SelectCandidates(
        IReadOnlyList<DuplicateStringSnapshot> topDuplicates, int sampleCount)
    {
        object raw = SelectCandidatesMethod.Invoke(null, [topDuplicates, sampleCount])!;
        var result = new List<(DuplicateStringSnapshot, ulong)>();
        foreach (object? item in (System.Collections.IEnumerable)raw)
        {
            Type t = item!.GetType();
            result.Add(((DuplicateStringSnapshot)t.GetField("Item1")!.GetValue(item)!, (ulong)t.GetField("Item2")!.GetValue(item)!));
        }
        return result;
    }

    [Fact]
    public void SelectRetentionPathCandidates_TakesFirstSampleCount_Entries()
    {
        var duplicates = new List<DuplicateStringSnapshot>
        {
            new("a", 10, 100, SampleAddresses: [0x1000]),
            new("b", 9, 90, SampleAddresses: [0x2000]),
            new("c", 8, 80, SampleAddresses: [0x3000]),
        };

        var candidates = SelectCandidates(duplicates, sampleCount: 2);

        candidates.Should().HaveCount(2);
        candidates[0].Should().Be((duplicates[0], 0x1000UL));
        candidates[1].Should().Be((duplicates[1], 0x2000UL));
    }

    [Fact]
    public void SelectRetentionPathCandidates_SkipsPatterns_WithNoSampleAddress()
    {
        var duplicates = new List<DuplicateStringSnapshot>
        {
            new("a", 10, 100, SampleAddresses: null),
            new("b", 9, 90, SampleAddresses: []),
            new("c", 8, 80, SampleAddresses: [0x3000]),
        };

        var candidates = SelectCandidates(duplicates, sampleCount: 5);

        candidates.Should().ContainSingle();
        candidates[0].Should().Be((duplicates[2], 0x3000UL));
    }

    [Fact]
    public void SelectRetentionPathCandidates_UsesFirstSampleAddress_WhenPatternHasTwo()
    {
        var duplicates = new List<DuplicateStringSnapshot>
        {
            new("a", 10, 100, SampleAddresses: [0x1000, 0x1001]),
        };

        var candidates = SelectCandidates(duplicates, sampleCount: 5);

        candidates.Should().ContainSingle();
        candidates[0].Address.Should().Be(0x1000UL);
    }

    [Fact]
    public void SelectRetentionPathCandidates_ReturnsEmpty_WhenSampleCountIsZero()
    {
        var duplicates = new List<DuplicateStringSnapshot> { new("a", 10, 100, SampleAddresses: [0x1000]) };

        var candidates = SelectCandidates(duplicates, sampleCount: 0);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public void BuildRetentionPaths_ReturnsNull_WhenCacheIsNull()
    {
        var duplicates = new List<DuplicateStringSnapshot> { new("a", 10, 100, SampleAddresses: [0x1000]) };

        object? result = BuildRetentionPathsMethod.Invoke(
            null, [null!, null, duplicates, 5, CancellationToken.None]);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildRetentionPaths_ReturnsNull_WhenSampleCountIsZero()
    {
        var duplicates = new List<DuplicateStringSnapshot> { new("a", 10, 100, SampleAddresses: [0x1000]) };

        object? result = BuildRetentionPathsMethod.Invoke(
            null, [null!, null, duplicates, 0, CancellationToken.None]);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildRetentionPaths_ReturnsNull_WhenNoDuplicates()
    {
        object? result = BuildRetentionPathsMethod.Invoke(
            null, [null!, null, Array.Empty<DuplicateStringSnapshot>(), 5, CancellationToken.None]);

        result.Should().BeNull();
    }
}

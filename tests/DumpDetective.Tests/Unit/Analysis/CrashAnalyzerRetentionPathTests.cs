using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashAnalyzerRetentionPathTests
{
    private readonly CrashAnalyzer _analyzer = new();

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)] // Large (LOH)
    [InlineData(4, true)] // Pinned
    [InlineData(-1, false)] // unresolved generation (fallback path default)
    public void SelectGen2RetentionCandidates_FiltersByGenerationThreshold(int generation, bool expectedIncluded)
    {
        var instance = new ExceptionInstance { Address = 0x1000, Type = "FooException", Generation = generation };
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new() { ["FooException"] = [instance] }
        };

        var candidates = CrashAnalyzer.SelectGen2RetentionCandidates(analysis);

        candidates.Should().HaveCount(expectedIncluded ? 1 : 0);
    }

    [Fact]
    public void SelectGen2RetentionCandidates_MixedGenerationsAcrossTypes_ReturnsOnlyGen2AndAbove()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["FooException"] = [
                    new ExceptionInstance { Address = 0x1000, Generation = 0 },
                    new ExceptionInstance { Address = 0x1100, Generation = 2 },
                ],
                ["BarException"] = [
                    new ExceptionInstance { Address = 0x2000, Generation = 3 },
                ]
            }
        };

        var candidates = CrashAnalyzer.SelectGen2RetentionCandidates(analysis);

        candidates.Select(c => c.Address).Should().BeEquivalentTo([0x1100UL, 0x2000UL]);
    }

    [Fact]
    public void BuildGen2RetentionPaths_NoCache_ReturnsEmptyWithoutTouchingHeap()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new() { ["FooException"] = [new() { Address = 0x1000, Generation = 2 }] }
        };

        var result = _analyzer.BuildGen2RetentionPaths(heap: null!, cache: null, analysis);

        result.Should().BeEmpty();
    }
}

using FluentAssertions;

using Xunit;
using DumpDetective.Analysis.Analyzers.Types;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class BoxingAnalyzerTests
{
    [Theory]
    [InlineData("System.Nullable`1[[System.Int32, mscorlib]]", false)]
    [InlineData("System.Nullable<System.Int32>", true)]
    [InlineData("System.Int32", false)]
    [InlineData("MyApp.NullableWrapper", false)]
    public void IsNullableTypeName_MatchesOnlySystemNullablePrefix(string typeName, bool expected)
    {
        BoxingAnalyzer.IsNullableTypeName(typeName).Should().Be(expected);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1_000L, 1_000)]
    [InlineData((long)int.MaxValue, int.MaxValue)]
    [InlineData((long)int.MaxValue + 1, int.MaxValue)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void SafeInstanceCount_ClampsToIntMaxValue(long count, int expected)
    {
        BoxingAnalyzer.SafeInstanceCount(count).Should().Be(expected);
    }

    [Theory]
    [InlineData(16, 12, 4)]   // 4 bytes of alignment padding
    [InlineData(16, 16, 0)]   // no waste
    [InlineData(16, 0, 0)]    // field bytes unavailable — no waste reported
    [InlineData(0, 8, 0)]     // struct size smaller than field bytes — treat as no waste
    public void ComputePaddingWaste_ReturnsDifferenceOrZero(int structSize, int fieldBytes, int expected)
    {
        BoxingAnalyzer.ComputePaddingWaste(structSize, fieldBytes).Should().Be(expected);
    }

    // HasIEquatableInterface/EnumerateInterfacesSafe moved to HeapTypeStatisticsQuery as part of the
    // 2026-09-12 Tier-1 retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md)
    // -- no longer public API on BoxingAnalyzer. Coverage moves to
    // BoxingAnalyzerRetypingCharacterizationTests, the same shape as every other Tier-1 batch's
    // dump-side-only helpers (e.g. HeapTypeStatisticsQuery.ComputeArrayRank/ComputeBaseTypeDepth,
    // never unit-tested directly either).
}

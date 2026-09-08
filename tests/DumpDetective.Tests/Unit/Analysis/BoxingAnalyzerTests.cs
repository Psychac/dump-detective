using DumpDetective.Analysis.Analyzers;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

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

    [Fact]
    public void HasIEquatableInterface_ReturnsTrue_WhenIEquatableIsPresent()
    {
        var interfaces = new[]
        {
            new ClrInterface("IComparable", null),
            new ClrInterface("IEquatable`1", null),
        };

        BoxingAnalyzer.HasIEquatableInterface(interfaces).Should().BeTrue();
    }

    [Fact]
    public void HasIEquatableInterface_ReturnsFalse_WhenIEquatableIsAbsent()
    {
        var interfaces = new[]
        {
            new ClrInterface("IComparable", null),
            new ClrInterface("IDisposable", null),
        };

        BoxingAnalyzer.HasIEquatableInterface(interfaces).Should().BeFalse();
    }

    [Fact]
    public void HasIEquatableInterface_ReturnsFalse_WhenNoInterfaces()
    {
        BoxingAnalyzer.HasIEquatableInterface([]).Should().BeFalse();
    }
}

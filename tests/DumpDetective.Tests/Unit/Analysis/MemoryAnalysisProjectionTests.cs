using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class MemoryAnalysisProjectionTests
{
    [Fact]
    public void Build_ShouldReturnAllTypesBySize_AndComputePressureScore()
    {
        Dictionary<string, CachedTypeStatistics> typeStats = new(StringComparer.Ordinal)
        {
            ["Alpha"] = new CachedTypeStatistics { TypeName = "Alpha", Count = 100, TotalSize = 1_000, LohCount = 0, LohSize = 0 },
            ["Beta"] = new CachedTypeStatistics { TypeName = "Beta", Count = 10, TotalSize = 50_000, LohCount = 1, LohSize = 40_000 },
            ["Gamma"] = new CachedTypeStatistics { TypeName = "Gamma", Count = 1, TotalSize = 60_000, LohCount = 1, LohSize = 60_000 },
        };

        MemoryAnalysisProjectionResult result = MemoryAnalysisProjection.Build(typeStats, null);

        result.TotalMemory.Should().Be(111_000);
        result.TotalLohMemory.Should().Be(100_000);
        result.AllTypesBySize.Should().HaveCount(3);
        result.AllTypesBySize[0].TypeName.Should().Be("Gamma");
        result.AllTypesBySize[1].TypeName.Should().Be("Beta");
        result.AllTypesBySize[2].TypeName.Should().Be("Alpha");
        result.MemoryPressureScore.Should().BeGreaterThan(0);
        result.Top5Bytes.Should().Be(111_000);
        result.SmallObjectCount.Should().Be(100);
    }
}

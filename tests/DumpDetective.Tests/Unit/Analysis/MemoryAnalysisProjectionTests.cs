using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class MemoryAnalysisProjectionTests
{
    [Fact]
    public void Build_ShouldRankTypesByCompositePressure_AndComputePressureScore()
    {
        Dictionary<string, CachedTypeStatistics> typeStats = new(StringComparer.Ordinal)
        {
            ["Alpha"] = new CachedTypeStatistics { TypeName = "Alpha", Count = 100, TotalSize = 1_000, LohCount = 0, LohSize = 0 },
            ["Beta"] = new CachedTypeStatistics { TypeName = "Beta", Count = 10, TotalSize = 50_000, LohCount = 1, LohSize = 40_000 },
            ["Gamma"] = new CachedTypeStatistics { TypeName = "Gamma", Count = 1, TotalSize = 60_000, LohCount = 1, LohSize = 60_000 },
        };

        MemoryAnalysisOptions options = new()
        {
            TopTypesCount = 2
        };

        MemoryAnalysisProjectionResult result = MemoryAnalysisProjection.Build(typeStats, null, options);

        result.TotalMemory.Should().Be(111_000);
        result.TotalLohMemory.Should().Be(100_000);
        result.SelectedTypes.Should().HaveCount(2);
        result.SelectedTypes[0].TypeName.Should().Be("Gamma");
        result.SelectedTypes[1].TypeName.Should().Be("Alpha");
        result.MemoryPressureScore.Should().BeGreaterThan(0);
        result.Top5Bytes.Should().Be(111_000);
        result.SmallObjectCount.Should().Be(100);
    }
}

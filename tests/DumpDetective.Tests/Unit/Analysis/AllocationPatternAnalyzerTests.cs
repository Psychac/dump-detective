using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AllocationPatternAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_RespectsPresets_AndThresholds()
    {
        // Build synthetic aggregates
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            // Transient: Gen0 dominant
            [0x1] = new TypeAggregateIndexEntry(0x1, 0, 100, 1000, 0, 0, 0, Gen0Count: 80, Gen1Count: 10, Gen2Count: 10),
            // Retained: Gen2 dominant
            [0x2] = new TypeAggregateIndexEntry(0x2, 0, 100, 1000, 0, 0, 0, Gen0Count: 5, Gen1Count: 5, Gen2Count: 90),
            // Mixed
            [0x3] = new TypeAggregateIndexEntry(0x3, 0, 100, 1000, 0, 0, 0, Gen0Count: 30, Gen1Count: 30, Gen2Count: 40)
        };

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: string.Empty,
            ObjectCount: 300,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        // Create HeapAnalysisCache and inject the heapIndex via reflection
        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        var options = new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 10,
            ScanMultiplier = 2,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5
        };

        var context = new AnalysisContext
        {
            Runtime = null!,
            Heap = null!,
            Cache = cache,
            Options = new Dictionary<System.Type, object?> { [typeof(AllocationPatternAnalysisOptions)] = options }
        };

        var analyzer = new AllocationPatternAnalyzer();
        var result = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        result.Profile.Should().Be(AllocationProfile.Mixed);
        result.TopTransientTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x1", System.StringComparison.OrdinalIgnoreCase));
        result.TopLongLivedTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x2", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllocationPatternAnalysisOptions_Presets_SetExpectedValues()
    {
        var fast = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Fast);
        var balanced = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Balanced);
        var full = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Full);

        fast.TopTypeLimit.Should().Be(10);
        balanced.TopTypeLimit.Should().Be(20);
        full.TopTypeLimit.Should().Be(50);

        balanced.ScanMultiplier.Should().BeGreaterThan(0);
        balanced.LongLivedSelectionThreshold.Should().BeInRange(0.0, 1.0);
    }
}

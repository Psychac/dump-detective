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
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = options }
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

    [Fact]
    public void AllocationPatternAnalysisOptions_Presets_SetAlgorithmicDefaults()
    {
        var fast = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Fast);
        var balanced = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Balanced);
        var full = AllocationPatternAnalysisOptions.Preset(AnalysisProfile.Full);

        fast.Mode.Should().Be(AllocationPatternAnalysisOptions.SelectionMode.TopByCount);
        fast.Strategy.Should().Be(AllocationPatternAnalysisOptions.ScanStrategy.TopN);

        balanced.Mode.Should().Be(AllocationPatternAnalysisOptions.SelectionMode.CompositeScore);
        balanced.Strategy.Should().Be(AllocationPatternAnalysisOptions.ScanStrategy.TopNByComparator);

        full.Mode.Should().Be(AllocationPatternAnalysisOptions.SelectionMode.CompositeScore);
        full.Strategy.Should().Be(AllocationPatternAnalysisOptions.ScanStrategy.FullScan);
        full.MaxScanItemsAbsolute.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SelectionMode_TopBySize_PicksLargestLongLived()
    {
        // Build synthetic aggregates where gen2 counts exist and sizes vary
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            [0x10] = new TypeAggregateIndexEntry(0x10, 0, 100, 1000, 0, 0, 0, Gen0Count: 10, Gen1Count: 40, Gen2Count: 50), // count 100, size 1000
            [0x20] = new TypeAggregateIndexEntry(0x20, 0, 50, 5000, 0, 0, 0, Gen0Count: 5, Gen1Count: 20, Gen2Count: 25),  // count 50, size 5000 (largest)
            [0x30] = new TypeAggregateIndexEntry(0x30, 0, 20, 200, 0, 0, 0, Gen0Count: 2, Gen1Count: 13, Gen2Count: 5)    // count 20, size 200
        };

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: string.Empty,
            ObjectCount: 170,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        var options = new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 1,
            ScanMultiplier = 1,
            LongLivedSelectionThreshold = 0.0, // pick any with gen2>0
            LongLivedClassificationThreshold = 0.0,
            Mode = AllocationPatternAnalysisOptions.SelectionMode.TopBySize,
            Strategy = AllocationPatternAnalysisOptions.ScanStrategy.TopNByComparator
        };

        var context = new AnalysisContext
        {
            Runtime = null!,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = options }
        };

        var analyzer = new AllocationPatternAnalyzer();
        var result = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        result.TopLongLivedTypes.Should().ContainSingle();
        result.TopLongLivedTypes[0].TypeName.StartsWith("MT:0x20", System.StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Fact]
    public async Task SelectionPriority_ClassificationFirst_ChoosesByBuckets()
    {
        // Create aggregates where a Gen0-heavy MT is lower by count but should appear when classifying-first
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            // Large count but mixed
            [0xA] = new TypeAggregateIndexEntry(0xA, 0, 1000, 1000, 0, 0, 0, Gen0Count: 10, Gen1Count: 40, Gen2Count: 50),
            // Lower count but Gen0 heavy
            [0xB] = new TypeAggregateIndexEntry(0xB, 0, 50, 50, 0, 0, 0, Gen0Count: 40, Gen1Count: 5, Gen2Count: 5),
            // long-lived
            [0xC] = new TypeAggregateIndexEntry(0xC, 0, 200, 200, 0, 0, 0, Gen0Count: 5, Gen1Count: 5, Gen2Count: 190)
        };

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: string.Empty,
            ObjectCount: 1250,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        var options = new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 5,
            ScanMultiplier = 2,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            Mode = AllocationPatternAnalysisOptions.SelectionMode.TopByCount,
            Strategy = AllocationPatternAnalysisOptions.ScanStrategy.TopN,
            Priority = AllocationPatternAnalysisOptions.SelectionPriority.ClassificationFirst
        };

        var context = new AnalysisContext
        {
            Runtime = null!,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = options }
        };

        var analyzer = new AllocationPatternAnalyzer();
        var result = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        // The Gen0-heavy MT 0xB should appear in transient or shortish despite lower count
        (result.TopTransientTypes.Any(t => t.TypeName.StartsWith("MT:0xB", System.StringComparison.OrdinalIgnoreCase))
         || result.TopShortishTypes.Any(t => t.TypeName.StartsWith("MT:0xB", System.StringComparison.OrdinalIgnoreCase))).Should().BeTrue();
    }

    [Fact]
    public async Task EmitFlags_DisableShortish_ClearsShortishList()
    {
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            [0x1] = new TypeAggregateIndexEntry(0x1, 0, 100, 1000, 0, 0, 0, Gen0Count: 30, Gen1Count: 40, Gen2Count: 30)
        };

        var heapIndex = new HeapIndexBuildResult(HeapIndexStorageKind.Memory, string.Empty, 100, TimeSpan.Zero, aggregates);
        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        var options = new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 5,
            ScanMultiplier = 1,
            Mode = AllocationPatternAnalysisOptions.SelectionMode.TopByCount,
            Strategy = AllocationPatternAnalysisOptions.ScanStrategy.TopN,
            EmitShortish = false
        };

        var context = new AnalysisContext { Runtime = null!, Cache = cache, AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = options } };
        var analyzer = new AllocationPatternAnalyzer();
        var result = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        result.TopShortishTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectionPriority_Mixed_AllowsSpilloverToFillDeficits()
    {
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            // transient candidate (one only)
            [0x1] = new TypeAggregateIndexEntry(0x1, 0, 10, 10, 0, 0, 0, Gen0Count: 9, Gen1Count: 0, Gen2Count: 1),
            // shortish candidates (none)
            // long-lived candidates (many)
            [0x2] = new TypeAggregateIndexEntry(0x2, 0, 100, 100, 0, 0, 0, Gen0Count: 5, Gen1Count: 5, Gen2Count: 90),
            [0x3] = new TypeAggregateIndexEntry(0x3, 0, 80, 80, 0, 0, 0, Gen0Count: 4, Gen1Count: 6, Gen2Count: 70)
        };

        var heapIndex = new HeapIndexBuildResult(HeapIndexStorageKind.Memory, string.Empty, 200, TimeSpan.Zero, aggregates);
        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        var options = new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 2,
            ScanMultiplier = 2,
            Mode = AllocationPatternAnalysisOptions.SelectionMode.TopByCount,
            Strategy = AllocationPatternAnalysisOptions.ScanStrategy.TopN,
            Priority = AllocationPatternAnalysisOptions.SelectionPriority.Mixed
        };

        var context = new AnalysisContext { Runtime = null!, Cache = cache, AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = options } };
        var analyzer = new AllocationPatternAnalyzer();
        var result = (AllocationPatternDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        // shortish bucket was empty; Mixed should allow spillover from long-lived to fill transient/shortish up to TopTypeLimit
        (result.TopTransientTypes.Count + result.TopShortishTypes.Count + result.TopLongLivedTypes.Count).Should().BeGreaterOrEqualTo(2);
    }
}

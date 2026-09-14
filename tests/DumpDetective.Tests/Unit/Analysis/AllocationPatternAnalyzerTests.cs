using DumpDetective.Analysis.Pipeline;
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
using DumpDetective.Analysis.Analyzers.Memory;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Ported for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="AllocationPatternAnalyzer"/> in place of the retyped
/// <see cref="AllocationPatternAnalyzer"/> directly (the pipeline only ever sees the registered
/// <c>Core.Abstractions.IAnalyzer</c>). All synthetic <c>TypeAggregates</c>/<c>HeapIndexBuildResult</c>
/// injection is unchanged from the pre-retyping version.
///
/// One real, structural finding from the port: these tests used <c>Runtime = null!</c>
/// pre-retyping, relying on the old analyzer's own <c>try { ComputeExactGenBytes(context.Heap,...) }
/// catch { ComputeApproxGenBytes(aggregates,...) }</c> to incidentally swallow the null-Runtime NRE
/// and fall back to the aggregates-only path. <c>LegacyAnalysisContextTranslator.Translate</c>
/// eagerly constructs every Tier-1 capability implementation up front, regardless of which ones this
/// specific analyzer actually needs, so it dereferences <c>legacy.Heap</c> (-&gt; <c>Runtime.Heap</c>)
/// unconditionally and throws before the analyzer ever runs. Not something to fix speculatively in
/// the shared translator for one analyzer's test convenience — these tests now attach a real,
/// self-process <c>ClrRuntime</c> instead (same pattern every other retyping's characterization
/// tests already use), which the retyped analyzer's own logic still never touches beyond the one
/// segment-based gen-bytes call.
/// </summary>
public sealed class AllocationPatternAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ClassifiesTypesByGenerationProfile()
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
            HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 300,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        RuntimeAnalysisContext contextRuntimeCtx = new() {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = new AllocationPatternAnalysisOptions() }
        };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        result.Profile.Should().Be(AllocationProfile.Mixed);
        result.TopTransientTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x1", System.StringComparison.OrdinalIgnoreCase));
        result.TopLongLivedTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x2", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesEveryCandidate_NoScanCap()
    {
        // MaxScanItemsAbsolute/ScanMultiplier/TopTypeLimit were deleted (D7, §11.2) — every
        // distinct type in TypeAggregates must be classified and reported, regardless of count.
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>();
        for (ulong mt = 1; mt <= 500; mt++)
        {
            aggregates[mt] = new TypeAggregateIndexEntry(mt, 0, 10, 100, 0, 0, 0, Gen0Count: 9, Gen1Count: 1, Gen2Count: 0);
        }

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 5000,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        RuntimeAnalysisContext contextRuntimeCtx = new() {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = new AllocationPatternAnalysisOptions() }
        };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        result.TopTransientTypes.Should().HaveCount(500);
    }

    [Fact]
    public async Task AnalyzeAsync_RanksEachBucketByCompositeScore_ClassificationFirst()
    {
        // A Gen0-heavy MT with a lower object count must still appear in its bucket —
        // classify-first bucketing (D7, §11.2) guarantees this regardless of scan order,
        // unlike the deleted single-pass incremental (LongLivedFirst) selection.
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
            HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 1250,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        RuntimeAnalysisContext contextRuntimeCtx = new() {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = new AllocationPatternAnalysisOptions() }
        };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        // The Gen0-heavy MT 0xB should appear in transient or shortish despite lower count
        (result.TopTransientTypes.Any(t => t.TypeName.StartsWith("MT:0xB", System.StringComparison.OrdinalIgnoreCase))
         || result.TopShortishTypes.Any(t => t.TypeName.StartsWith("MT:0xB", System.StringComparison.OrdinalIgnoreCase))).Should().BeTrue();
    }

    [Fact]
    public async Task FinalizableFlag_SurfacedOnTypeProfile_AndAggregatedInDomainResult()
    {
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            // Finalizable, retained (long-lived)
            [0x1] = new TypeAggregateIndexEntry(0x1, 0, 100, 1000, 0, 0, 0, Gen0Count: 5, Gen1Count: 5, Gen2Count: 90, Flags: TypeAggregateFlags.IsFinalizableType),
            // Not finalizable, transient
            [0x2] = new TypeAggregateIndexEntry(0x2, 0, 100, 500, 0, 0, 0, Gen0Count: 80, Gen1Count: 10, Gen2Count: 10)
        };

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 200,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = new AllocationPatternAnalysisOptions() } };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);
        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        result.FinalizableTypeCount.Should().Be(1);
        result.FinalizableBytes.Should().Be(1000);
        result.TopLongLivedTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x1", System.StringComparison.OrdinalIgnoreCase) && t.IsFinalizable);
        result.TopTransientTypes.Should().ContainSingle(t => t.TypeName.StartsWith("MT:0x2", System.StringComparison.OrdinalIgnoreCase) && !t.IsFinalizable);
    }

    [Fact]
    public async Task LohSizeBands_PopulatedFromGlobalSizeBuckets()
    {
        var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
        {
            // avg size 90,000 -> bucket 6 (85 KB-1 MB)
            [0x1] = new TypeAggregateIndexEntry(0x1, 0, 1, 90_000, 1, 90_000, 0, Gen0Count: 0, Gen1Count: 0, Gen2Count: 1),
            // avg size 2,000,000 -> bucket 7 (1 MB-10 MB)
            [0x2] = new TypeAggregateIndexEntry(0x2, 0, 1, 2_000_000, 1, 2_000_000, 0, Gen0Count: 0, Gen1Count: 0, Gen2Count: 1),
            // avg size 20,000,000 -> bucket 8 (>=10 MB)
            [0x3] = new TypeAggregateIndexEntry(0x3, 0, 1, 20_000_000, 1, 20_000_000, 0, Gen0Count: 0, Gen1Count: 0, Gen2Count: 1),
        };

        var globalBuckets = new long[SizeBucketHelper.BucketCount];
        globalBuckets[6] = 1;
        globalBuckets[7] = 1;
        globalBuckets[8] = 1;

        var heapIndex = new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            IndexPath: string.Empty,
            ObjectCount: 3,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: aggregates,
            GlobalSizeBuckets: globalBuckets);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        var cache = new HeapAnalysisCache();
        var fi = typeof(HeapAnalysisCache).GetField("_heapIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fi.SetValue(cache, heapIndex);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions { AllocationPatternAnalysis = new AllocationPatternAnalysisOptions() } };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);
        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        result.LohSizeBands.Should().NotBeNull();
        result.LohSizeBands!.Should().HaveCount(3);
        result.LohSizeBands[0].RangeLabel.Should().Be("85 KB–1 MB");
        result.LohSizeBands[0].ObjectCount.Should().Be(1);
        result.LohSizeBands[0].TotalBytes.Should().Be(90_000);
        result.LohSizeBands[1].RangeLabel.Should().Be("1 MB–10 MB");
        result.LohSizeBands[1].TotalBytes.Should().Be(2_000_000);
        result.LohSizeBands[2].RangeLabel.Should().Be("≥ 10 MB");
        result.LohSizeBands[2].TotalBytes.Should().Be(20_000_000);
    }
}

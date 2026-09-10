using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="HeapTopologyAnalyzerLegacyAdapter"/> reproduces the pre-retyping arithmetic. Same
/// ClrMD-ground-truth-cross-check approach as <c>SegmentReservationAnalyzerRetypingCharacterizationTests</c>
/// for segment/LOH/POH/Frozen data (can't be hand-crafted via reflection injection); the exact-SOH-
/// derivation branch uses a synthetic injected index (same <c>AnalysisPipelineTests.InjectHeapIndex</c>
/// backdoor-field pattern) sized from those real ground-truth counts, rather than running a real
/// <c>PrebuildHeapIndex</c> scan against this test process's own live heap.
/// </summary>
public sealed class HeapTopologyAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_WithIndex_DerivesSohArithmeticFromInjectedTotals()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            // Ground truth straight from ClrMD, independent of the analyzer's own segment walk.
            int expectedSegmentCount = 0;
            ulong expectedTotalCommitted = 0, expectedTotalReserved = 0;
            int expectedLohCount = 0, expectedPohCount = 0, expectedFrozenCount = 0;
            long expectedLohObjects = 0, expectedPohObjects = 0, expectedFrozenObjects = 0;
            ulong expectedNonSohUsedBytes = 0;
            foreach (ClrSegment segment in heap.Segments)
            {
                expectedSegmentCount++;
                expectedTotalCommitted += RangeLength(segment.CommittedMemory);
                expectedTotalReserved += RangeLength(segment.ReservedMemory);

                HeapSegmentKind kind = SegmentKindMapper.Map(segment);
                if (kind is not (HeapSegmentKind.LargeObjectHeap or HeapSegmentKind.PinnedObjectHeap or HeapSegmentKind.Frozen))
                    continue;

                long objects = 0;
                ulong usedBytes = 0;
                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.IsFree) continue;
                    objects++;
                    usedBytes += obj.Size;
                }
                expectedNonSohUsedBytes += usedBytes;

                switch (kind)
                {
                    case HeapSegmentKind.LargeObjectHeap: expectedLohCount++; expectedLohObjects += objects; break;
                    case HeapSegmentKind.PinnedObjectHeap: expectedPohCount++; expectedPohObjects += objects; break;
                    case HeapSegmentKind.Frozen: expectedFrozenCount++; expectedFrozenObjects += objects; break;
                }
            }

            // Synthetic index, sized from the real ground-truth counts above: "SOH holds exactly
            // 12,345 more objects than everything else combined" is an arbitrary but fully
            // predictable fact, letting SohObjects/SohUsedBytes/SohFragmentedBytes be asserted
            // exactly rather than just structurally.
            const long syntheticExtraSohObjects = 12_345;
            long syntheticObjectCount = expectedLohObjects + expectedPohObjects + expectedFrozenObjects + syntheticExtraSohObjects;
            ulong syntheticTotalIndexedBytes = expectedNonSohUsedBytes + 500_000UL;

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: syntheticObjectCount, Elapsed: TimeSpan.Zero,
                TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>
                {
                    [0xDEAD_BEEF] = new(0xDEAD_BEEF, ModuleId: 0, Count: syntheticObjectCount, TotalSize: syntheticTotalIndexedBytes, LohCount: 0, LohSize: 0, SampleAddress: 0),
                }));

            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            HeapTopologyAnalyzerLegacyAdapter adapter = new();
            HeapTopologyDomainResult result = (HeapTopologyDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("Heap Topology");
            result.Category.Should().Be("Memory");

            result.TotalSegments.Should().Be(expectedSegmentCount);
            result.TotalCommittedBytes.Should().Be(expectedTotalCommitted);
            result.TotalReservedBytes.Should().Be(expectedTotalReserved);

            result.LohSegmentCount.Should().Be(expectedLohCount);
            result.PohSegmentCount.Should().Be(expectedPohCount);
            result.FrozenSegmentCount.Should().Be(expectedFrozenCount);

            // LOH/POH/Frozen are walked directly.
            result.KindSummaries.Should().ContainSingle(k => k.Kind == HeapSegmentKind.LargeObjectHeap && k.ObjectCount == expectedLohObjects);
            result.KindSummaries.Should().ContainSingle(k => k.Kind == HeapSegmentKind.PinnedObjectHeap && k.ObjectCount == expectedPohObjects);
            result.KindSummaries.Should().ContainSingle(k => k.Kind == HeapSegmentKind.Frozen && k.ObjectCount == expectedFrozenObjects);

            // SOH is derived arithmetically from the injected index totals, exactly.
            result.KindSummaries.Should().ContainSingle(k => k.Kind == HeapSegmentKind.SmallObjectHeap && k.ObjectCount == syntheticExtraSohObjects);
            result.SohBytes.Should().BeGreaterThanOrEqualTo(0);
            ulong expectedSohUsedBytes = syntheticTotalIndexedBytes > expectedNonSohUsedBytes ? syntheticTotalIndexedBytes - expectedNonSohUsedBytes : 0;
            result.TotalUsedBytes.Should().Be(expectedSohUsedBytes + expectedNonSohUsedBytes);
            ulong expectedSohFragmented = result.SohBytes > expectedSohUsedBytes ? result.SohBytes - expectedSohUsedBytes : 0;
            result.SohFragmentedBytes.Should().Be(expectedSohFragmented);

            result.IsServerGc.Should().Be(heap.IsServer);
            result.LogicalHeapCount.Should().Be(heap.SubHeaps.Length);

            ulong sumKindCommitted = 0;
            foreach (SegmentKindSummary k in result.KindSummaries) sumKindCommitted += k.TotalBytes;
            sumKindCommitted.Should().Be(result.TotalCommittedBytes);

            result.TopSegmentsBySize!.Count.Should().BeLessThanOrEqualTo(10);
            for (int i = 1; i < result.TopSegmentsBySize!.Count; i++)
                result.TopSegmentsBySize[i].CommittedBytes.Should().BeLessThanOrEqualTo(result.TopSegmentsBySize[i - 1].CommittedBytes);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public void AnalyzeAsync_WithoutIndex_SkipsSohDerivation()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            HeapTopologyAnalyzerLegacyAdapter adapter = new();
            HeapTopologyDomainResult result = (HeapTopologyDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            // No index available: SOH's arithmetic derivation never runs. Every SOH segment's
            // CountObjects call returns the "-1, not walked here" sentinel, which the per-segment
            // loop propagates straight into SohObjects (sohObjects = -1) — a pre-existing,
            // preserved quirk: -1 means "genuinely unknown," distinct from "confirmed zero."
            result.KindSummaries.Should().ContainSingle(k => k.Kind == HeapSegmentKind.SmallObjectHeap && k.ObjectCount == -1);
            result.SohFragmentedBytes.Should().Be(0);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong RangeLength(MemoryRange range) => range.End >= range.Start ? range.End - range.Start : 0;

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult result)
    {
        typeof(HeapAnalysisCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cache, result);
    }
}

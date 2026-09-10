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
/// <see cref="GCRootAnalyzerLegacyAdapter"/> — the new capability-scoped
/// <see cref="GCRootAnalyzer"/>, wrapped to run through the unchanged
/// <c>Core.Abstractions.IAnalyzer</c> pipeline — reproduces the pre-retyping analyzer's arithmetic
/// and invariants. Unlike <c>GCGenerationAnalyzer</c>'s pilot test, root data can't be hand-crafted
/// via reflection injection the way <c>TypeAggregateIndexEntry</c> fixtures can — a
/// <c>HeapRootRef</c>'s target is a real, live address on the test process's own heap — so this
/// cross-checks internal-consistency invariants (counts, sort order, byte accounting) against a
/// real self-attached process heap instead of hand-computed fixture arithmetic, matching
/// <c>SegmentReservationAnalyzerRetypingCharacterizationTests</c>'s approach for the same reason.
/// </summary>
public sealed class GCRootAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_WithHeapIndex_ProducesInternallyConsistentResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            // Only HasExactGenerationData/GetTotalIndexedBytes need to come from the injected
            // index — roots, object metadata, and per-address generation all resolve live against
            // this process's own heap regardless (see class remarks), so a small, fully-controlled
            // two-type fixture is enough to make the "% of managed heap" arithmetic independently
            // checkable below.
            const ulong typeATotalSize = 400_000;
            const ulong typeBTotalSize = 600_000;
            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [0x1000] = new(0x1000, ModuleId: 0, Count: 40, TotalSize: typeATotalSize, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 40, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
                [0x2000] = new(0x2000, ModuleId: 0, Count: 60, TotalSize: typeBTotalSize, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 60, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
            };
            ulong expectedTotalHeapBytes = typeATotalSize + typeBTotalSize;

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 100,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates));

            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using GCRootAnalyzerLegacyAdapter adapter = new();
            var result = (GCRootDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("GC Root Analysis");
            result.Category.Should().Be("Memory");

            // A live process always has at least stack/static/handle roots.
            result.TotalRoots.Should().BeGreaterThan(0);

            int nonDroppedRootCount = result.TotalRoots - result.DroppedZeroEstimateRootCount;
            result.TopRootsBySeverity.Should().HaveCount(nonDroppedRootCount);
            result.RootOwnedSubgraphs.Should().HaveCount(nonDroppedRootCount);

            int sumByKindCounts = 0;
            foreach (RootKindSummary kind in result.ByKind)
            {
                sumByKindCounts += kind.Count;

                // Independently recomputed from the same, fully-known fixture total — not a
                // hardcoded value, since EstimatedRetainedBytes itself depends on this process's
                // live heap state.
                double expectedPct = expectedTotalHeapBytes > 0
                    ? (double)kind.EstimatedRetainedBytes / expectedTotalHeapBytes * 100.0
                    : 0.0;
                kind.PctOfManagedHeap.Should().BeApproximately(expectedPct, 0.0001);

                (kind.Gen0Fraction + kind.Gen1Fraction + kind.Gen2Fraction + kind.LohFraction).Should().BeInRange(0.0, 1.0001);
            }
            sumByKindCounts.Should().Be(nonDroppedRootCount);

            // ByKind sorted by EstimatedRetainedBytes descending.
            for (int i = 1; i < result.ByKind.Count; i++)
                result.ByKind[i].EstimatedRetainedBytes.Should().BeLessThanOrEqualTo(result.ByKind[i - 1].EstimatedRetainedBytes);

            // Findings sorted by SeverityScore descending.
            for (int i = 1; i < result.TopRootsBySeverity.Count; i++)
                result.TopRootsBySeverity[i].SeverityScore.Should().BeLessThanOrEqualTo(result.TopRootsBySeverity[i - 1].SeverityScore);

            foreach (RootFinding finding in result.TopRootsBySeverity)
            {
                finding.EstimatedRetainedBytes.Should().BeGreaterThan(0);
                finding.TargetAddress.Should().NotBe(0UL);
            }

            foreach (RootOwnedSubgraphFinding subgraph in result.RootOwnedSubgraphs)
                subgraph.SubgraphNodeCount.Should().Be(subgraph.SubgraphTypeNames.Count);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public void AnalyzeAsync_WithoutHeapIndex_ReturnsEmptyResult()
    {
        // Matches the pre-retyping analyzer's own gate: it requires the Phase-1 heap index to run
        // at all (its per-kind "% of managed heap" and severity scoring both depend on the index's
        // exact type-aggregate totals), rather than falling back to a live-heap-only pass.
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using GCRootAnalyzerLegacyAdapter adapter = new();
            var result = (GCRootDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.TotalRoots.Should().Be(0);
            result.ByKind.Should().BeEmpty();
            result.TopRootsBySeverity.Should().BeEmpty();
            result.RootOwnedSubgraphs.Should().BeEmpty();
            result.SubgraphWalkCapped.Should().BeFalse();
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult result)
    {
        typeof(HeapAnalysisCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cache, result);
    }
}

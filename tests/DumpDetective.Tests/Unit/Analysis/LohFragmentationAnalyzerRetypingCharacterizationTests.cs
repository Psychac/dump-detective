using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="LohFragmentationAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic on the
/// no-index (live segment scan) fallback path — segment/free-block data can't be hand-crafted via
/// reflection injection any more than other batches' segment/root data could. Deliberately doesn't
/// also exercise the disk-index fast path here: unlike a plain <c>Dictionary</c> fixture (cheap to
/// inject, as the pilot did), faking a well-formed on-disk <c>LohFreeBlockIndex.bin</c>/
/// <c>LargeObjectIndex.bin</c> pair correctly is a materially bigger lift for a unit test than the
/// value it would add — <see cref="LohFragmentationAnalyzerRealDumpTests"/> already exercises that
/// path against a real disk index with real ground truth, which is strictly better evidence than a
/// hand-built synthetic container would be.
/// </summary>
public sealed class LohFragmentationAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_NoIndex_MatchesClrMdGroundTruth()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            int expectedSegmentCount = 0;
            ulong expectedTotalBytes = 0, expectedFreeBytes = 0, expectedUsedBytes = 0;
            foreach (ClrSegment segment in heap.Segments)
            {
                if (segment.Kind is not (GCSegmentKind.Large or GCSegmentKind.Pinned))
                    continue;

                expectedSegmentCount++;
                ulong committed = SegmentKindMapper.GetCommittedBytes(segment);
                expectedTotalBytes += committed;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid) continue;
                    if (obj.IsFree) expectedFreeBytes += obj.Size;
                    else expectedUsedBytes += obj.Size;
                }
            }
            expectedUsedBytes = expectedTotalBytes > expectedFreeBytes ? expectedTotalBytes - expectedFreeBytes : 0;

            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using LohFragmentationAnalyzerLegacyAdapter adapter = new();
            var result = (LohFragmentationDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("LOH & POH Fragmentation Analysis");
            result.Category.Should().Be("Memory");

            if (expectedSegmentCount == 0)
            {
                result.SegmentCount.Should().Be(0);
                return;
            }

            result.SegmentCount.Should().Be(expectedSegmentCount);
            result.TotalBytes.Should().Be(expectedTotalBytes);
            result.UsedBytes.Should().Be(expectedUsedBytes);

            ulong sumKindBytes = 0;
            int sumKindSegments = 0;
            foreach (LohKindBreakdown kind in result.KindBreakdown!)
            {
                sumKindBytes += kind.TotalBytes;
                sumKindSegments += kind.SegmentCount;
            }
            sumKindBytes.Should().Be(result.TotalBytes);
            sumKindSegments.Should().Be(result.SegmentCount);

            IReadOnlyList<LohSegmentSnapshot> topFragmented = result.TopFragmentedSegments!;
            for (int i = 1; i < topFragmented.Count; i++)
            {
                int cmp = topFragmented[i - 1].FragmentationPercent.CompareTo(topFragmented[i].FragmentationPercent);
                if (cmp == 0)
                    topFragmented[i].FreeBytes.Should().BeLessThanOrEqualTo(topFragmented[i - 1].FreeBytes);
                else
                    cmp.Should().BeGreaterThan(0);
            }

            IReadOnlyList<LargeObjectSnapshot> topLargeObjects = result.TopLargeObjects!;
            foreach (LargeObjectSnapshot obj in topLargeObjects)
                obj.Size.Should().BeGreaterThanOrEqualTo(85_000);
            for (int i = 1; i < topLargeObjects.Count; i++)
                topLargeObjects[i].Size.Should().BeLessThanOrEqualTo(topLargeObjects[i - 1].Size);

            IReadOnlyList<LohTypeProfile> typeProfiles = result.TopLargeObjectTypes!;
            for (int i = 1; i < typeProfiles.Count; i++)
                typeProfiles[i].TotalBytes.Should().BeLessThanOrEqualTo(typeProfiles[i - 1].TotalBytes);

            result.FragmentationPercent.Should().BeInRange(0.0, 100.0);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }
}

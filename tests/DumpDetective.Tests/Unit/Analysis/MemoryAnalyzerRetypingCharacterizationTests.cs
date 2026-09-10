using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="MemoryAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic and invariants.
/// Runs the no-index (live heap scan) path only — the only path a self-attached test process can
/// exercise without a real <c>PrebuildHeapIndex</c> disk scan (avoided here for the same reason
/// <c>HeapTopologyAnalyzerRetypingCharacterizationTests</c> avoids it: disk side effects and
/// runtime cost this assertion doesn't need) — and cross-checks segment byte totals directly
/// against ClrMD ground truth, matching <c>SegmentReservationAnalyzerRetypingCharacterizationTests</c>'s
/// approach for the same underlying reason (segment/type data can't be hand-crafted via reflection
/// injection the way a pilot's <c>TypeAggregateIndexEntry</c> fixture can).
/// </summary>
public sealed class MemoryAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_NoIndex_ProducesInternallyConsistentResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using MemoryAnalyzerLegacyAdapter adapter = new();
            var result = (MemoryDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("Memory Analysis");
            result.Category.Should().Be("Memory");

            result.UniqueTypes.Should().BeGreaterThan(0);
            result.TopTypes.Should().HaveCount(result.UniqueTypes, "MemoryAnalyzer reports every distinct type, uncapped");

            ulong sumTypeBytes = 0;
            long sumTypeCount = 0;
            foreach (TypeSnapshot snapshot in result.TopTypes)
            {
                sumTypeBytes += snapshot.TotalBytes;
                sumTypeCount += snapshot.Count;
                snapshot.LohBytes.Should().BeLessThanOrEqualTo(snapshot.TotalBytes);
            }
            sumTypeBytes.Should().Be(result.TotalBytes);
            sumTypeCount.Should().Be(result.TotalObjects);

            result.LohPercent.Should().BeInRange(0.0, 100.0);
            result.Top1BytesPercent.Should().BeInRange(0.0, 100.0);
            result.Top5BytesPercent.Should().BeInRange(result.Top1BytesPercent, 100.0);
            result.Top10BytesPercent.Should().BeInRange(result.Top5BytesPercent, 100.0);
            result.SmallObjectCountPercent.Should().BeInRange(0.0, 100.0);
            result.SmallObjectBytesPercent.Should().BeInRange(0.0, 100.0);
            result.MemoryPressureScore.Should().BeInRange(0.0, 100.0);
            result.LohPressureScore.Should().BeInRange(0.0, 100.0);
            result.ConcentrationPressureScore.Should().BeInRange(0.0, 100.0);
            result.SmallObjectPressureScore.Should().BeInRange(0.0, 100.0);
            result.DensityPressureScore.Should().BeInRange(0.0, 100.0);
            result.LohFragmentationRatio.Should().BeInRange(0.0, 100.0);

            // At most TypesToWalkForRetainedSize types get an EstimatedRetainedBytes walk.
            int walkedCount = 0;
            foreach (TypeSnapshot snapshot in result.TopTypes)
                if (snapshot.EstimatedRetainedBytes > 0) walkedCount++;
            walkedCount.Should().BeLessThanOrEqualTo(MemoryAnalyzer.TypesToWalkForRetainedSize);

            // Segment summaries cross-checked directly against ClrMD ground truth.
            result.SegmentSummaries.Should().NotBeNull();
            int expectedSegmentCount = 0;
            ulong expectedCommitted = 0;
            foreach (ClrSegment segment in heap.Segments)
            {
                expectedSegmentCount++;
                expectedCommitted += CommittedLength(segment);
            }

            int actualSegmentCount = 0;
            ulong actualCommitted = 0;
            foreach (GCSegmentSummary summary in result.SegmentSummaries!)
            {
                actualSegmentCount += summary.SegmentCount;
                actualCommitted += summary.CommittedBytes;
                summary.UsedBytes.Should().Be(summary.CommittedBytes, "pre-retyping UsedBytes was byte-identical to CommittedBytes");
            }
            actualSegmentCount.Should().Be(expectedSegmentCount);
            actualCommitted.Should().Be(expectedCommitted);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong CommittedLength(ClrSegment segment) =>
        segment.CommittedMemory.End >= segment.CommittedMemory.Start ? segment.CommittedMemory.End - segment.CommittedMemory.Start : 0;
}

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
/// <see cref="SegmentReservationAnalyzerLegacyAdapter"/> reproduces the pre-retyping arithmetic.
/// Unlike <c>GCGenerationAnalyzer</c>'s pilot test, segment data can't be hand-crafted via
/// reflection injection — <c>SegmentSummary</c> wraps a real, live <c>ClrSegment</c>, not a
/// synthesizable value — so this cross-checks the retyped output directly against ClrMD ground
/// truth on the test process's own live heap (<see cref="ClrHeap.Segments"/>,
/// <see cref="ClrRuntime.DataTarget"/>, <see cref="ClrHeap.IsServer"/>) instead of hand-computed
/// fixture arithmetic.
/// </summary>
public sealed class SegmentReservationAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_MatchesClrMdGroundTruth()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            SegmentReservationAnalyzerLegacyAdapter adapter = new();
            SegmentReservationDomainResult result = (SegmentReservationDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("Segment Reservation Analysis");
            result.Category.Should().Be("Memory");

            // Ground truth straight from ClrMD, independent of the analyzer's own segment walk.
            int expectedSegmentCount = 0;
            ulong expectedCommitted = 0, expectedReserved = 0;
            foreach (ClrSegment segment in heap.Segments)
            {
                expectedSegmentCount++;
                expectedCommitted += CommittedLength(segment);
                expectedReserved += ReservedLength(segment);
            }

            result.TotalSegmentCount.Should().Be(expectedSegmentCount);
            result.SegmentTable.Should().HaveCount(expectedSegmentCount);
            result.TotalCommittedBytes.Should().Be(expectedCommitted);
            result.TotalReservedBytes.Should().Be(expectedReserved);
            result.ReservationGapBytes.Should().Be(
                expectedReserved > expectedCommitted ? expectedReserved - expectedCommitted : 0);

            result.DumpPointerSize.Should().Be(runtime.DataTarget.DataReader.PointerSize);
            result.IsServerGc.Should().Be(heap.IsServer);

            // Internal consistency, independent of the ClrMD cross-check above.
            ulong sumReservedByKind = 0;
            foreach (ulong v in result.ReservedByKind.Values) sumReservedByKind += v;
            sumReservedByKind.Should().Be(result.TotalReservedBytes);

            ulong sumCommittedByKind = 0;
            foreach (ulong v in result.CommittedByKind.Values) sumCommittedByKind += v;
            sumCommittedByKind.Should().Be(result.TotalCommittedBytes);

            int sumSegmentCountByKind = 0;
            foreach (int v in result.SegmentCountByKind.Values) sumSegmentCountByKind += v;
            sumSegmentCountByKind.Should().Be(result.TotalSegmentCount);

            ulong sumReservedByHeap = 0;
            foreach (ulong v in result.ReservedByLogicalHeap.Values) sumReservedByHeap += v;
            sumReservedByHeap.Should().Be(result.TotalReservedBytes);

            result.LogicalHeapCount.Should().Be(result.ReservedByLogicalHeap.Count);

            // Segment table sorted by ReservedBytes descending.
            for (int i = 1; i < result.SegmentTable.Count; i++)
                result.SegmentTable[i].ReservedBytes.Should().BeLessThanOrEqualTo(result.SegmentTable[i - 1].ReservedBytes);

            foreach (SegmentReservationEntry entry in result.SegmentTable)
                entry.FillPct.Should().BeInRange(0.0, 100.0);

            result.ReservedToCommittedRatio.Should().Be(
                result.TotalCommittedBytes > 0 ? result.TotalReservedBytes / (double)result.TotalCommittedBytes : 0.0);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong CommittedLength(ClrSegment segment) =>
        segment.CommittedMemory.End >= segment.CommittedMemory.Start ? segment.CommittedMemory.End - segment.CommittedMemory.Start : 0;

    private static ulong ReservedLength(ClrSegment segment) =>
        segment.ReservedMemory.End >= segment.ReservedMemory.Start ? segment.ReservedMemory.End - segment.ReservedMemory.Start : 0;
}

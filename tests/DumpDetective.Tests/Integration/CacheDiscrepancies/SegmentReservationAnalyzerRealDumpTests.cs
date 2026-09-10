using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Phase 1 segment-capability retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>SegmentReservationAnalyzerRetypingCharacterizationTests</c>) cross-checks
/// against ClrMD ground truth on a small live process heap; this proves the same on a large real
/// dump — many more segments, real Server/Workstation GC layout, real region-vs-classic segment mix.
/// </summary>
public sealed class SegmentReservationAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SegmentReservationAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        int expectedSegmentCount = 0;
        ulong expectedCommitted = 0, expectedReserved = 0;
        foreach (ClrSegment segment in heap.Segments)
        {
            expectedSegmentCount++;
            expectedCommitted += RangeLength(segment.CommittedMemory);
            expectedReserved += RangeLength(segment.ReservedMemory);
        }

        HeapAnalysisCache cache = new();
        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using SegmentReservationAnalyzerLegacyAdapter adapter = new();
        var result = (SegmentReservationDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Segment Reservation Analysis");
        result.TotalSegmentCount.Should().Be(expectedSegmentCount);
        result.TotalCommittedBytes.Should().Be(expectedCommitted);
        result.TotalReservedBytes.Should().Be(expectedReserved);
        result.DumpPointerSize.Should().Be(runtime.DataTarget.DataReader.PointerSize);
        result.IsServerGc.Should().Be(heap.IsServer);

        ulong sumReservedByKind = 0;
        foreach (ulong v in result.ReservedByKind.Values) sumReservedByKind += v;
        sumReservedByKind.Should().Be(result.TotalReservedBytes);

        for (int i = 1; i < result.SegmentTable.Count; i++)
            result.SegmentTable[i].ReservedBytes.Should().BeLessThanOrEqualTo(result.SegmentTable[i - 1].ReservedBytes);

        foreach (SegmentReservationEntry entry in result.SegmentTable)
            entry.FillPct.Should().BeInRange(0.0, 100.0);

        if (result.IsRegionsBased)
        {
            result.RegionStats.Should().NotBeEmpty();
        }

        _output.WriteLine(
            $"Segments={result.TotalSegmentCount:N0} CommittedBytes={result.TotalCommittedBytes:N0} " +
            $"ReservedBytes={result.TotalReservedBytes:N0} GapBytes={result.ReservationGapBytes:N0} " +
            $"Ratio={result.ReservedToCommittedRatio:F2} IsServerGc={result.IsServerGc} " +
            $"DumpPointerSize={result.DumpPointerSize} IsRegionsBased={result.IsRegionsBased} " +
            $"PressureRisk={result.AddressSpacePressureRisk} ({result.PressureRiskReason})");
    }

    private static ulong RangeLength(MemoryRange range) => range.End >= range.Start ? range.End - range.Start : 0;
}

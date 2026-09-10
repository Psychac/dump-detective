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
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Unlike the self-attached
/// live-heap characterization tests, this exercises the real exact-SOH-derivation branch (a real
/// prebuilt index, not a synthetic injected one) against a large real dump — many more segments,
/// millions of SOH objects never walked per-object, real LOH/POH/Frozen populations.
/// </summary>
public sealed class HeapTopologyAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public HeapTopologyAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentIndexBackedResult()
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
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using HeapTopologyAnalyzerLegacyAdapter adapter = new();
        var result = (HeapTopologyDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Heap Topology");
        result.TotalSegments.Should().Be(expectedSegmentCount);
        result.TotalCommittedBytes.Should().Be(expectedCommitted);
        result.TotalReservedBytes.Should().Be(expectedReserved);
        result.IsServerGc.Should().Be(heap.IsServer);
        result.LogicalHeapCount.Should().Be(heap.SubHeaps.Length);

        // Real dump with a prebuilt index: SOH derivation ran, so its object count is a real
        // non-negative value (the -1 "not computed" sentinel only appears without an index).
        SegmentKindSummary sohSummary = result.KindSummaries.Single(k => k.Kind == HeapSegmentKind.SmallObjectHeap);
        sohSummary.ObjectCount.Should().BeGreaterThanOrEqualTo(0);

        ulong sumKindCommitted = 0;
        foreach (SegmentKindSummary k in result.KindSummaries) sumKindCommitted += k.TotalBytes;
        sumKindCommitted.Should().Be(result.TotalCommittedBytes);

        result.TotalUsedBytes.Should().BeLessThanOrEqualTo(result.TotalCommittedBytes + result.SohFragmentedBytes + result.LohFragmentedBytes + result.PohFragmentedBytes + result.FrozenFragmentedBytes);

        result.TopSegmentsBySize!.Count.Should().BeLessThanOrEqualTo(10);
        for (int i = 1; i < result.TopSegmentsBySize!.Count; i++)
            result.TopSegmentsBySize[i].CommittedBytes.Should().BeLessThanOrEqualTo(result.TopSegmentsBySize[i - 1].CommittedBytes);

        _output.WriteLine(
            $"Segments={result.TotalSegments:N0} SohObjects={sohSummary.ObjectCount:N0} SohBytes={result.SohBytes:N0} " +
            $"LohSegments={result.LohSegmentCount} LohBytes={result.LohBytes:N0} PohSegments={result.PohSegmentCount} " +
            $"FrozenSegments={result.FrozenSegmentCount} IsServerGc={result.IsServerGc} LogicalHeapCount={result.LogicalHeapCount} " +
            $"TotalCommitted={result.TotalCommittedBytes:N0} TotalUsed={result.TotalUsedBytes:N0}");
        if (result.TopPohTypes is { Count: > 0 })
            _output.WriteLine($"Largest POH type: {result.TopPohTypes[0].TypeName} ({result.TopPohTypes[0].TotalBytes:N0} bytes)");
        if (result.TopFrozenTypes is { Count: > 0 })
            _output.WriteLine($"Largest Frozen type: {result.TopFrozenTypes[0].TypeName} ({result.TopFrozenTypes[0].TotalBytes:N0} bytes)");
    }

    private static ulong RangeLength(MemoryRange range) => range.End >= range.Start ? range.End - range.Start : 0;
}

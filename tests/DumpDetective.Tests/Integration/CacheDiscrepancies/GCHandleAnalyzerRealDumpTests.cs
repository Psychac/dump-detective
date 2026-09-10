using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>GCHandleAnalyzerRetypingCharacterizationTests</c>) only exercises the
/// current test host's own small handle table; this proves the same invariants against a large real
/// dump — hundreds of thousands of handles, real pinned/async-pinned SOH classification, real
/// dependent-handle topology, real weak-handle generation breakdown.
/// </summary>
public sealed class GCHandleAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public GCHandleAnalyzerRealDumpTests(ITestOutputHelper output)
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

        int expectedTotalHandles = 0;
        foreach (ClrHandle _ in runtime.EnumerateHandles())
            expectedTotalHandles++;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using GCHandleAnalyzerLegacyAdapter adapter = new();
        var result = (GCHandleDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("GC Handle Analysis");
        result.TotalHandles.Should().Be(expectedTotalHandles);
        result.TotalHandles.Should().BeGreaterThan(0, "a real IIS crash dump has live GC handles");
        result.StrongLikeHandles.Should().Be(result.TotalHandles - result.WeakLikeHandles);

        int sumByKind = 0;
        foreach (NameCountEntry e in result.HandlesByKind!) sumByKind += e.Count;
        sumByKind.Should().Be(result.TotalHandles);

        (result.PinnedSohObjectCount + result.PinnedNonSohObjectCount).Should().BeLessThanOrEqualTo(result.PinnedHandleTargets);
        (result.AsyncPinnedSohObjectCount + result.AsyncPinnedNonSohObjectCount).Should().BeLessThanOrEqualTo(result.PinnedHandleTargets + result.TotalHandles);

        result.DependentUnresolvedPercent.Should().BeInRange(0.0, 100.0);
        (result.DependentResolvedEdgeCount + result.DependentUnresolvedTargetCount).Should().BeLessThanOrEqualTo(result.DependentHandleCount);

        for (int i = 1; i < result.TopPinnedHandleAddresses!.Count; i++)
            result.TopPinnedHandleAddresses[i].Bytes.Should().BeLessThanOrEqualTo(result.TopPinnedHandleAddresses[i - 1].Bytes);

        _output.WriteLine(
            $"TotalHandles={result.TotalHandles:N0} Strong={result.StrongLikeHandles:N0} Weak={result.WeakLikeHandles:N0} " +
            $"PinnedTargets={result.PinnedHandleTargets:N0} PinnedRetainedBytes={result.PinnedRetainedBytes:N0} " +
            $"(exact={result.PinnedRetainedBytesIsExact}) AsyncPinnedRetainedBytes={result.AsyncPinnedRetainedBytes:N0} " +
            $"RefCounted={result.RefCountedHandleCount:N0} Dependent={result.DependentHandleCount:N0} " +
            $"(resolved={result.DependentResolvedEdgeCount:N0}, unresolved={result.DependentUnresolvedTargetCount:N0}) " +
            $"WeakShort[Gen0={result.WeakShortGen0Count},Gen1={result.WeakShortGen1Count},Gen2={result.WeakShortGen2Count},Loh={result.WeakShortLohCount}] " +
            $"WeakLong[Gen0={result.WeakLongGen0Count},Gen1={result.WeakLongGen1Count},Gen2={result.WeakLongGen2Count},Loh={result.WeakLongLohCount}]");
    }
}

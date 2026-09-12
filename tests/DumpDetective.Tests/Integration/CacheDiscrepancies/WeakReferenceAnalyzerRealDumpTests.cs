using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="WeakReferenceAnalyzerLegacyAdapter"/> produces an internally consistent result
/// against a real, large dump's real GC handle table and heap-index scan.
/// </summary>
public sealed class WeakReferenceAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public WeakReferenceAnalyzerRealDumpTests(ITestOutputHelper output)
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

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using WeakReferenceAnalyzerLegacyAdapter adapter = new();
        var result = (WeakReferenceDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Weak Reference Analysis");
        result.TotalWeakHandles.Should().BeGreaterThan(0, "a real 3.5 GB IIS dump has weak GC handles");
        (result.AliveWeakTargets + result.DeadWeakTargets).Should().Be(result.TotalWeakHandles);
        result.DeadTargetRatio.Should().BeInRange(0.0, 1.0);

        for (int i = 1; i < result.TopWeakTargetTypes.Count; i++)
            result.TopWeakTargetTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopWeakTargetTypes[i - 1].Count);

        foreach (var entry in result.WeakHandleKindLiveness ?? [])
            entry.Total.Should().BeGreaterThan(0);

        foreach (var bucket in result.AliveWeakTargetGenerationDistribution ?? [])
            bucket.Count.Should().BeGreaterThan(0);

        // This retyping's own accepted simplification: the degraded-extrapolation case no longer
        // exists under the candidate-streaming mechanism -- exactness should always hold now.
        result.StaleWrapperCountIsExact.Should().BeTrue();
        result.PhaseBFallbackUsed.Should().BeFalse();
        result.PhaseBSkipped.Should().BeFalse();

        if (result.WeakReferenceObjectCount > 0)
            result.StaleWrapperCount.Should().BeLessThanOrEqualTo(result.WeakReferenceObjectCount);

        _output.WriteLine(
            $"TotalWeakHandles={result.TotalWeakHandles:N0} AliveWeakTargets={result.AliveWeakTargets:N0} " +
            $"DeadWeakTargets={result.DeadWeakTargets:N0} DependentHandleDeadKeyCount={result.DependentHandleDeadKeyCount:N0} " +
            $"WeakReferenceObjectCount={result.WeakReferenceObjectCount:N0} StaleWrapperCount={result.StaleWrapperCount:N0} " +
            $"HeldOnlyViaWeakReferenceCount={result.HeldOnlyViaWeakReferenceCount:N0} " +
            $"HeldOnlyViaWeakReferenceDetectionAvailable={result.HeldOnlyViaWeakReferenceDetectionAvailable} " +
            $"AliveWeakTargetsRetainedBytesIsExact={result.AliveWeakTargetsRetainedBytesIsExact}");
    }
}

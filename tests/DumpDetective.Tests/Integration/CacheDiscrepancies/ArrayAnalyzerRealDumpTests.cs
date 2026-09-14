using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="ArrayAnalyzer"/> produces an internally consistent result against
/// a real, large dump — the only test exercising all four steps (population, large-array satellite
/// reads, sparse density via the new <c>dump.array-elements</c> capability, pinned detection)
/// together against real data.
/// </summary>
public sealed class ArrayAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ArrayAnalyzerRealDumpTests(ITestOutputHelper output)
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

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using ArrayAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (ArrayDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Array Analysis");
        result.TotalArrayObjects.Should().BeGreaterThan(0, "a real 3.5 GB dump has a non-trivial array population");
        result.MultiDimArrayCount.Should().BeLessThanOrEqualTo(result.TotalArrayObjects);
        result.LohArrayCount.Should().BeGreaterThanOrEqualTo(0);

        for (int i = 1; i < result.TopArrayTypesBySize.Count; i++)
            result.TopArrayTypesBySize[i].TotalBytes.Should().BeLessThanOrEqualTo(result.TopArrayTypesBySize[i - 1].TotalBytes);

        foreach (var large in result.TopLargeArrays)
        {
            large.Length.Should().BeGreaterThanOrEqualTo(0);
            large.Size.Should().BeGreaterThan(0UL);
        }

        foreach (var sparse in result.TopSparseArrays)
        {
            sparse.SparseRatio.Should().BeInRange(0.5, 1.0, "only arrays at/above the 0.5 threshold are reported");
            sparse.NullOrZeroCount.Should().BeLessThanOrEqualTo(sparse.Length);
        }

        if (result.TopPinnedArrays is { Count: > 0 })
        {
            result.PinnedArrayCount.Should().BeGreaterThan(0);
            for (int i = 1; i < result.TopPinnedArrays.Count; i++)
                result.TopPinnedArrays[i].Size.Should().BeLessThanOrEqualTo(result.TopPinnedArrays[i - 1].Size);
        }

        _output.WriteLine(
            $"TotalArrayObjects={result.TotalArrayObjects:N0} TotalArrayBytes={result.TotalArrayBytes:N0} " +
            $"MultiDim={result.MultiDimArrayCount:N0} Loh={result.LohArrayCount:N0} " +
            $"TopArrayTypes={result.TopArrayTypesBySize.Count} TopLargeArrays={result.TopLargeArrays.Count} " +
            $"TopSparseArrays={result.TopSparseArrays.Count} PinnedArrayCount={result.PinnedArrayCount} TopPinnedArrays={result.TopPinnedArrays?.Count ?? 0}");
    }
}

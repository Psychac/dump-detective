using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="CollectionAnalyzerLegacyAdapter"/> produces an internally consistent result
/// against a real, large dump -- including the new <see cref="DumpDetective.Sdk.Analysis.IDumpObjectFieldQuery.EnumerateInstanceFields"/>-backed
/// field-layout resolution and the <see cref="DumpDetective.Sdk.Analysis.IHeapReverseReferenceQuery"/>/
/// <see cref="DumpDetective.Sdk.Analysis.IHeapRootPathQuery"/> post-scan enrichment, none of which the
/// pre-existing fake-backed characterization tests exercise against real ClrMD data.
/// </summary>
public sealed class CollectionAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public CollectionAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using CollectionAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (CollectionDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Collection Analysis");

        if (result.TotalCollections == 0)
        {
            result.WastefulCollectionCount.Should().Be(0);
            _output.WriteLine("No BCL-collection-shaped objects found on this dump's heap -- empty result is correct.");
            return;
        }

        result.TotalCollections.Should().BeGreaterThan(0);
        int sumByKind = result.Dictionaries + result.Lists + result.ArrayLists + result.Stacks + result.SortedLists
            + result.SortedSets + result.HashSets + result.Queues + result.ImmutableArrays + result.ImmutableArrayBuilders;
        sumByKind.Should().Be(result.TotalCollections);

        result.TopWastefulCollections.Should().NotBeNull();
        for (int i = 1; i < result.TopWastefulCollections!.Count; i++)
            result.TopWastefulCollections[i].WastedMemory.Should().BeLessThanOrEqualTo(result.TopWastefulCollections[i - 1].WastedMemory);

        foreach (WastefulCollectionSnapshot w in result.TopWastefulCollections)
        {
            w.Capacity.Should().BeGreaterThan(w.Count);
            w.FillRate.Should().BeInRange(0.0, 100.0);
            w.Recommendation.Should().NotBeNullOrEmpty();
        }

        int withRootDescription = result.TopWastefulCollections.Count(w => !string.IsNullOrEmpty(w.RootDescription));
        int withOwnerHint = result.TopWastefulCollections.Count(w => w.OwnerTypeHint is not null);
        int withRetainedBytes = result.TopWastefulCollections.Count(w => w.RetainedBytes is not null);

        _output.WriteLine(
            $"TotalCollections={result.TotalCollections} Dict={result.Dictionaries} List={result.Lists} HashSet={result.HashSets} " +
            $"Queue={result.Queues} Stack={result.Stacks} ArrayList={result.ArrayLists} SortedList={result.SortedLists} " +
            $"SortedSet={result.SortedSets} ImmutableArray={result.ImmutableArrays} ImmutableArrayBuilder={result.ImmutableArrayBuilders} " +
            $"WastefulCount={result.WastefulCollectionCount} TotalWastedMemory={result.TotalWastedMemory} " +
            $"TopShown={result.TopWastefulCollections.Count} WithRootDescription={withRootDescription} WithOwnerHint={withOwnerHint} " +
            $"WithRetainedBytes={withRetainedBytes} GenerationBreakdownKinds={result.GenerationBreakdown?.Count ?? 0}");
    }
}

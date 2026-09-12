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
/// Real-dump verification for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="BoxingAnalyzerLegacyAdapter"/> produces an internally consistent result against
/// a real, large dump's real <c>TypeAggregates</c>.
/// </summary>
public sealed class BoxingAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public BoxingAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using BoxingAnalyzerLegacyAdapter adapter = new();
        var result = (BoxingDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Boxing Analysis");
        result.TotalBoxedObjects.Should().BeGreaterThan(0, "a real 3.5 GB dump has boxed value types (at minimum boxed enums/booleans)");
        result.TotalBoxedBytes.Should().BeGreaterThan(0UL);

        for (int i = 1; i < result.TopBoxedTypes.Count; i++)
            result.TopBoxedTypes[i].TotalBoxBytes.Should().BeLessThanOrEqualTo(result.TopBoxedTypes[i - 1].TotalBoxBytes);

        foreach (var t in result.TopBoxedTypes)
        {
            t.Gen2Fraction.Should().BeInRange(0.0, 1.0);
            t.Gen2Count.Should().BeLessThanOrEqualTo(t.BoxCount);
        }

        foreach (var p in result.TopPaddingWasteTypes)
        {
            p.WastedPaddingBytes.Should().BeGreaterThan(0);
            p.WasteRatio.Should().BeInRange(0.0, 1.0);
        }

        for (int i = 1; i < result.TopOversizedTypes.Count; i++)
            result.TopOversizedTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopOversizedTypes[i - 1].Count);

        _output.WriteLine(
            $"TotalBoxedObjects={result.TotalBoxedObjects:N0} TotalBoxedBytes={result.TotalBoxedBytes:N0} " +
            $"TopBoxedTypes={result.TopBoxedTypes.Count} TopPaddingWasteTypes={result.TopPaddingWasteTypes.Count} " +
            $"TopOversizedTypes={result.TopOversizedTypes.Count} TotalGen2BoxedCount={result.TotalGen2BoxedCount:N0}");
    }
}

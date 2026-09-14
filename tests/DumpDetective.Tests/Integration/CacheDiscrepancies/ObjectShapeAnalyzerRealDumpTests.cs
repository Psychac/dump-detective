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
/// Real-dump verification for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="ObjectShapeAnalyzer"/> produces an internally consistent result
/// against a real, large dump's real <c>TypeShapeCache</c>/<c>TypeAggregates</c>.
/// </summary>
public sealed class ObjectShapeAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ObjectShapeAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using ObjectShapeAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (ObjectShapeAnalyzerDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Object Shape Analysis");
        result.TotalTypesAnalyzed.Should().BeGreaterThan(0, "a real 3.5 GB dump has a non-trivial type population");

        int classified = result.TopReferenceHeavyTypes.Count + result.TopValueHeavyTypes.Count + result.TopBalancedTypes.Count;
        classified.Should().BeLessThanOrEqualTo(result.TotalTypesAnalyzed, "Scalar-category types are excluded from all three buckets by design");

        foreach (TypeShapeProfile p in result.TopReferenceHeavyTypes)
        {
            p.ReferenceFieldRatio.Should().BeInRange(0.0, 1.0);
            p.ReferenceFields.Should().BeLessThanOrEqualTo(p.TotalFields);
            p.BaseTypeChainDepth.Should().BeInRange(0, 20);
        }

        for (int i = 1; i < result.TopValueHeavyTypes.Count; i++)
            result.TopValueHeavyTypes[i].TotalSize.Should().BeLessThanOrEqualTo(result.TopValueHeavyTypes[i - 1].TotalSize);

        _output.WriteLine(
            $"TotalTypesAnalyzed={result.TotalTypesAnalyzed} RefHeavy={result.TopReferenceHeavyTypes.Count} " +
            $"ValHeavy={result.TopValueHeavyTypes.Count} Balanced={result.TopBalancedTypes.Count} " +
            $"AvgRefFieldsPerType={result.AvgRefFieldsPerType:F2} TotalGcScanWork={result.TotalGcScanWork:N0}");
    }
}

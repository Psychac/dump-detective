using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Runtime;
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
/// Real-dump verification for <see cref="CrashAnalyzer"/>'s Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — proves
/// <see cref="CrashAnalyzer"/> produces an internally consistent result against a real
/// dump, including the new <see cref="DumpDetective.Sdk.Analysis.IDumpObjectFieldQuery.EnumerateExceptionStackTrace"/>
/// binary-trace-unwind tier, <see cref="DumpDetective.Sdk.Analysis.IRuntimeThreadQuery.ExceptionAddress"/>-based
/// active-thread correlation, and <see cref="DumpDetective.Sdk.Analysis.IHeapRootPathQuery"/>-backed
/// Gen2/LOH retention paths — none of which the fake-backed characterization tests exercise against
/// real ClrMD data. This dump is a "Second Chance Exception" crash dump, so a real,
/// non-empty exception population and at least one active-thread candidate are expected, not just
/// tolerated as an edge case.
/// </summary>
public sealed class CrashAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public CrashAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using CrashAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (CrashDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Crash Analysis");

        // A second-chance-exception crash dump should have a real exception population --
        // an empty result here would itself be the discrepancy, not a tolerated edge case.
        result.TotalExceptions.Should().BeGreaterThan(0);
        result.ExceptionTypeCounts.Values.Sum().Should().Be(result.TotalExceptions);
        result.ActiveExceptionTypeCounts.Values.Sum().Should().Be(result.ActiveExceptions);
        result.ActiveExceptions.Should().BeLessThanOrEqualTo(result.TotalExceptions);

        result.TopExceptionInstances.Should().NotBeNull();
        foreach (ExceptionInstanceSnapshot instance in result.TopExceptionInstances!)
        {
            instance.ChainDepth.Should().BeGreaterThanOrEqualTo(1);
            if (instance.HResult.HasValue)
                instance.HResult.Value.Should().NotBe(0);
        }

        result.TopCrashThreadCandidates.Should().NotBeNull();
        foreach (CrashThreadCandidateSnapshot candidate in result.TopCrashThreadCandidates!)
            candidate.ActiveExceptionCount.Should().BeGreaterThan(0);

        result.InferredTraceCount.Should().BeLessThanOrEqualTo(result.TopCrashThreadCandidates!.Count);

        result.CrashBuckets.Should().NotBeNull();
        for (int i = 1; i < result.CrashBuckets!.Count; i++)
            result.CrashBuckets[i].InstanceCount.Should().BeLessThanOrEqualTo(result.CrashBuckets[i - 1].InstanceCount);
        foreach (CrashBucket bucket in result.CrashBuckets)
            bucket.ActiveInstanceCount.Should().BeLessThanOrEqualTo(bucket.InstanceCount);

        result.Gen2RetentionPaths.Should().NotBeNull();
        foreach (ExceptionRetentionPath path in result.Gen2RetentionPaths!)
        {
            path.RootKind.Should().NotBeNullOrEmpty("TryFindRootPath's \"RootKind: path\" format should always split cleanly");
            path.FormattedPath.Should().NotBeNullOrEmpty();
        }

        int activeWithOriginalTrace = result.TopCrashThreadCandidates.Count(c => c.OriginalStackTrace is { Count: > 0 });
        int withAggregateInnerTypes = result.TopExceptionInstances.Count(i => i.AggregateInnerExceptionTypes is { Count: > 0 });

        _output.WriteLine(
            $"TotalExceptions={result.TotalExceptions} ActiveExceptions={result.ActiveExceptions} " +
            $"DistinctTypes={result.ExceptionTypeCounts.Count} AggregateExceptionCount={result.AggregateExceptionCount} " +
            $"TopExceptionInstances={result.TopExceptionInstances.Count} TopCrashThreadCandidates={result.TopCrashThreadCandidates.Count} " +
            $"ActiveWithOriginalTrace={activeWithOriginalTrace} InferredTraceCount={result.InferredTraceCount} " +
            $"CrashBuckets={result.CrashBuckets.Count} MessageDistributions={result.MessageDistributions?.Count ?? 0} " +
            $"Gen2RetentionPaths={result.Gen2RetentionPaths.Count} WithAggregateInnerTypes={withAggregateInnerTypes}");
    }
}

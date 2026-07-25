using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class DominatorAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task DominatorAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        DominatorDomainResult memResult = await RunThroughPipelineAsync(runtime, memCache);
        string freshDumpPath = dumpPath + ".freshdiskcheck.DominatorAnalyzerDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            DominatorDomainResult diskResult = await RunThroughPipelineAsync(runtime, diskCache);
            diskResult.CandidateCount.Should().Be(memResult.CandidateCount);
            diskResult.AnalyzedCount.Should().Be(memResult.AnalyzedCount);
            diskResult.TotalEstimatedRetainedBytes.Should().Be(memResult.TotalEstimatedRetainedBytes);
            diskResult.HeuristicOnly.Should().Be(memResult.HeuristicOnly);
            diskResult.MaxBreadth.Should().Be(memResult.MaxBreadth);
            diskResult.MaxDepth.Should().Be(memResult.MaxDepth);
            diskResult.TopDominatorTypes.Count.Should().Be(memResult.TopDominatorTypes.Count);
            diskResult.HighlyReferencedObjectCount.Should().Be(memResult.HighlyReferencedObjectCount);
            diskResult.SkippedReferenceAddresses.Should().Be(memResult.SkippedReferenceAddresses);
            diskResult.ObjectScanCapped.Should().Be(memResult.ObjectScanCapped);
            diskResult.ReferenceCountingSkipped.Should().Be(memResult.ReferenceCountingSkipped);
            diskResult.TopHighlyReferencedTotalBytes.Should().Be(memResult.TopHighlyReferencedTotalBytes);
            (diskResult.TopHighlyReferencedObjects?.Count ?? 0).Should().Be(memResult.TopHighlyReferencedObjects?.Count ?? 0);
            (diskResult.TopRetentionTypes?.Count ?? 0).Should().Be(memResult.TopRetentionTypes?.Count ?? 0);

            foreach (HighlyReferencedObjectSnapshot obj in memResult.TopHighlyReferencedObjects ?? Array.Empty<HighlyReferencedObjectSnapshot>())
            {
                obj.Evidence.Should().NotBeNull();
                obj.Evidence!.ContributingSignals.Should().NotBeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }

    // Drives the analyzer through AnalysisPipeline/HeapIndexScanDispatcher instead of calling
    // AnalyzeAsync directly, so this test exercises the same priming path production uses.
    private static async Task<DominatorDomainResult> RunThroughPipelineAsync(ClrRuntime runtime, HeapAnalysisCache cache)
    {
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache };
        AnalysisPipeline pipeline = new([new DominatorAnalyzer()], new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.GetResult<DominatorDomainResult>()!;
    }
}

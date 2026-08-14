using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 6 validation for docs/cache/cache-architecture.md: the 20 T2 call sites across
/// the shared root-path infrastructure (<c>RootPathFinder</c>, <c>IndexBackedBidirectionalSearch</c>,
/// <c>RootPathSearchSupport</c>) and 6 analyzers (<c>StaticRootLeakDetector</c>,
/// <c>DominatorAnalyzer</c>, <c>EventLeakAnalyzer</c>, <c>TimerLeakAnalyzer</c>,
/// <c>CollectionAnalyzer</c>, <c>ReferenceChainAnalyzer</c>, <c>WeakReferenceAnalyzer</c>,
/// <c>GCHandleAnalyzer</c>, <c>CrashAnalyzer</c>, <c>LockGraphAnalyzer</c>) now migrated to
/// <c>IHeapAnalysisCache.TryGetObjectMetadata</c> — end-to-end run against a real dump in both disk
/// and in-memory mode, since these are internal-plumbing changes to shared traversal infrastructure
/// touched by many analyzers, not a single isolated call.
/// </summary>
public sealed class Phase6T2FixesEndToEndDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public Phase6T2FixesEndToEndDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static IAnalyzer[] CreateAnalyzers() =>
    [
        new StaticRootLeakDetector(),
        new DominatorAnalyzer(),
        new EventLeakAnalyzer(),
        new TimerLeakAnalyzer(),
        new CollectionAnalyzer(),
        new ReferenceChainAnalyzer(),
        new WeakReferenceAnalyzer(),
        new GCHandleAnalyzer(),
        new CrashAnalyzer(),
        new LockGraphAnalyzer(),
    ];

    [DiscrepancyFact]
    public async Task Phase6TouchedAnalyzers_RunEndToEnd_DiskMode_WithoutExceptions()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        string freshDumpPath = dumpPath + ".freshdiskcheck.Phase6T2FixesEndToEndDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache cache = new();
            cache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            var options = new AnalysisOptions();

            foreach (IAnalyzer analyzer in CreateAnalyzers())
            {
                var context = new AnalysisContext { Runtime = runtime, Cache = cache, AnalysisOptions = options };
                AnalyzerDomainResult result = await analyzer.AnalyzeAsync(context, CancellationToken.None);
                result.Should().NotBeNull();
                _output.WriteLine($"[disk] {analyzer.Name}: {result.GetType().Name} produced successfully");
            }

            cache.Dispose();
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }

    [DiscrepancyFact]
    public async Task Phase6TouchedAnalyzers_RunEndToEnd_MemoryMode_WithoutExceptions()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        // In-memory mode: no PrebuildHeapIndex call, matching the "memCache" convention used by
        // every other discrepancy test — exercises each site's live heap.GetObject fallback path.
        HeapAnalysisCache cache = new();
        var options = new AnalysisOptions();

        foreach (IAnalyzer analyzer in CreateAnalyzers())
        {
            var context = new AnalysisContext { Runtime = runtime, Cache = cache, AnalysisOptions = options };
            AnalyzerDomainResult result = await analyzer.AnalyzeAsync(context, CancellationToken.None);
            result.Should().NotBeNull();
            _output.WriteLine($"[memory] {analyzer.Name}: {result.GetType().Name} produced successfully");
        }

        cache.Dispose();
    }
}

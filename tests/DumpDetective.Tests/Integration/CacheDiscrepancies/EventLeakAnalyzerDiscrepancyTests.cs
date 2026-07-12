using System.Threading;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Reproduces a real discrepancy observed between memory-backed and disk-backed heap
/// indexing: running <see cref="EventLeakAnalyzer"/> against the exact same loaded
/// <see cref="ClrHeap"/>, once with each backend, produces very different leak counts
/// (empirically ~7x fewer publishers under disk mode on a 3.3 GB production dump).
/// Requires a real dump on disk (set DD_BENCHMARK_DUMP, or use the same fallback path
/// as <see cref="BenchmarkSuite1.AnalyzerBenchmarkBase{TAnalyzer}"/>) — skips locally
/// when unavailable since it isn't runnable in CI.
/// </summary>
public sealed class EventLeakAnalyzerDiscrepancyTests
{
    private static string DumpPath =>
        Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task EventLeakAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
            return; // machine-local repro dump not present — nothing to verify here

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        EventLeakOptions options = new();
        AnalysisOptions analysisOptions = new() { EventLeak = options };
        EventLeakAnalyzer analyzer = new();

        // Memory mode: MemoryBackedObjectIndexWriter has no on-disk cache — always a fresh,
        // uncached full scan of the loaded heap.
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Memory);
        AnalysisContext memContext = new()
        {
            Runtime = runtime,
            Cache = memCache,
            AnalysisOptions = analysisOptions,
        };
        EventLeakDomainResult memResult = (EventLeakDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);

        // Disk mode: point at a scratch sibling path with no pre-existing .dumpindex/ folder,
        // so DiskBackedObjectIndexWriter's TryLoadFromCache fast-path cannot apply and this is
        // also a fresh, uncached full scan of the SAME loaded heap — ruling out stale on-disk
        // cache reuse as an explanation for any divergence.
        string freshDumpPath = dumpPath + ".freshdiskcheck";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Disk);
            AnalysisContext diskContext = new()
            {
                Runtime = runtime,
                Cache = diskCache,
                AnalysisOptions = analysisOptions,
            };
            EventLeakDomainResult diskResult = (EventLeakDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);

            // Same heap, same analyzer, same options — only the index storage backend differs.
            // These must match; a mismatch here reproduces the disk-vs-memory discrepancy.
            diskResult.TotalEventsScanned.Should().Be(memResult.TotalEventsScanned,
                "disk- and memory-backed indexing scan the same heap and must examine the same candidate event fields");
            diskResult.TotalPublisherInstances.Should().Be(memResult.TotalPublisherInstances,
                "disk- and memory-backed indexing scan the same heap and must find the same publisher instances");
            diskResult.TotalEventLeakInstances.Should().Be(memResult.TotalEventLeakInstances);
            diskResult.TotalSubscribers.Should().Be(memResult.TotalSubscribers);
            diskResult.StaticEventLeakCount.Should().Be(memResult.StaticEventLeakCount);
            diskResult.InstanceEventLeakCount.Should().Be(memResult.InstanceEventLeakCount);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}

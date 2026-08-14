using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 7 validation for docs/cache/cache-architecture.md:
/// 1. Lightweight latency comparison — <c>TryGetObjectMetadata</c> vs. live <c>heap.GetObject</c> —
///    at a realistic call volume, reusing a single dump load (a Stopwatch-based signal rather than a
///    full BenchmarkDotNet run, which is written separately in
///    <c>src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs</c> for on-demand rigorous measurement).
/// 2. Backward-compat correctness against a *real* disk build (not a synthetic container): corrupts
///    just the SegmentIndex section's bytes post-build to simulate an old cache, and confirms
///    <c>TryGetObjectMetadata</c> still returns results that agree with live <c>heap.GetObject</c> via
///    its fallback path — the unit-level backward-compat tests (<c>HeapIndexCacheTests</c>,
///    <c>ObjectAddressLookupTests</c>) already cover "doesn't throw"; this covers "still correct."
/// </summary>
public sealed class ObjectAddressLookupPerfAndFallbackDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ObjectAddressLookupPerfAndFallbackDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void TryGetObjectMetadata_LatencyComparison_AndFallback_AfterSegmentIndexCorruption()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        string freshDumpPath = dumpPath + ".freshdiskcheck.ObjectAddressLookupPerfAndFallbackDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);

            // Sample real addresses from the built index (every 100,000th, matching earlier phases'
            // sampling stride) — a realistic call pattern for a migrated T2 site.
            const int SampleStride = 100_000;
            var samples = new List<ulong>();
            int seen = 0;
            foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
            {
                if (entry.Address != 0 && seen % SampleStride == 0)
                    samples.Add(entry.Address);
                seen++;
            }
            samples.Should().NotBeEmpty();
            _output.WriteLine($"sample count: {samples.Count}");

            // ── Part 1: latency comparison (SegmentIndex present, fast path) ──────────────────
            // The first TryGetObjectMetadata call triggers ObjectAddressLookup.TryOpen, which opens
            // three mmap accessors (ObjectAddresses/MethodTables/Sizes) — CacheContainerReader
            // checksum-verifies each section's *entire* byte range (XxHash32) on open, a one-time
            // cost the HeapIndexCache._addressLookupAttempted guard amortizes across every
            // subsequent call for the life of the cache instance. Timed separately from steady-state
            // per-call cost below so a small sample size doesn't make the one-time cost look like a
            // per-lookup regression.
            var swFirstCall = Stopwatch.StartNew();
            bool firstCallFound = diskCache.TryGetObjectMetadata(heap, samples[0], out _, out _);
            swFirstCall.Stop();

            var swIndex = Stopwatch.StartNew();
            int indexHits = firstCallFound ? 1 : 0;
            for (int i = 1; i < samples.Count; i++)
            {
                if (diskCache.TryGetObjectMetadata(heap, samples[i], out _, out _))
                    indexHits++;
            }
            swIndex.Stop();

            var swLive = Stopwatch.StartNew();
            int liveHits = 0;
            foreach (ulong addr in samples)
            {
                ClrObject obj = heap.GetObject(addr);
                if (obj.IsValid)
                    liveHits++;
            }
            swLive.Stop();

            int steadyStateCalls = samples.Count - 1;
            _output.WriteLine($"TryGetObjectMetadata first call (triggers open + full-section checksum verify): {swFirstCall.Elapsed.TotalMilliseconds:F2}ms");
            _output.WriteLine($"TryGetObjectMetadata steady-state: {indexHits}/{steadyStateCalls} hits in {swIndex.Elapsed.TotalMilliseconds:F2}ms "
                + $"({(steadyStateCalls > 0 ? swIndex.Elapsed.TotalMilliseconds / steadyStateCalls : 0):F4}ms/call)");
            _output.WriteLine($"heap.GetObject:       {liveHits}/{samples.Count} hits in {swLive.Elapsed.TotalMilliseconds:F2}ms "
                + $"({swLive.Elapsed.TotalMilliseconds / samples.Count:F4}ms/call)");

            indexHits.Should().Be(liveHits);

            // ── Part 2: corrupt just the SegmentIndex section, verify fallback correctness ────
            CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
            reader!.TryGetSectionInfo(CacheSectionId.SegmentIndex, out CacheTocEntry segIndexEntry).Should().BeTrue();

            using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = segIndexEntry.Offset;
                int b = fs.ReadByte();
                fs.Position = segIndexEntry.Offset;
                fs.WriteByte((byte)~b);
            }

            // Fresh cache instance forced to reopen the (now-corrupted) container from disk — the
            // TypeAggregates "build complete" sentinel is untouched, so PrebuildHeapIndex hits the
            // existing cache.bin rather than rebuilding, exactly like an old cache predating
            // SegmentIndex would be loaded today.
            HeapAnalysisCache fallbackCache = new();
            fallbackCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);

            int fallbackMismatches = 0;
            foreach (ulong addr in samples)
            {
                ClrObject live = heap.GetObject(addr);
                bool fallbackFound = fallbackCache.TryGetObjectMetadata(heap, addr, out ulong fbMt, out ulong fbSize);

                if (fallbackFound != live.IsValid)
                {
                    fallbackMismatches++;
                    continue;
                }
                if (live.IsValid && (fbMt != (live.Type?.MethodTable ?? 0) || fbSize != live.Size))
                    fallbackMismatches++;
            }

            _output.WriteLine($"fallback-after-corruption mismatches: {fallbackMismatches}/{samples.Count}");
            fallbackMismatches.Should().Be(0);

            diskCache.Dispose();
            fallbackCache.Dispose();
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }
}

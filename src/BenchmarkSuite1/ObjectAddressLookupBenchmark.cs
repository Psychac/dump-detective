using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using System;
using System.Collections.Generic;
using System.IO;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Benchmarks <see cref="HeapAnalysisCache.TryGetObjectMetadata"/> (disk-index-backed point
    /// lookup, see docs/cache/cache-architecture.md) against a live
    /// <c>heap.GetObject(address)</c> resolution, at a realistic per-analysis-run call volume.
    /// </summary>
    /// <remarks>
    /// A preliminary Stopwatch-based check on a real dump
    /// (<c>ObjectAddressLookupPerfAndFallbackDiscrepancyTests</c>) found steady-state per-call cost
    /// roughly comparable between the two paths (heap already warm from the immediately-preceding
    /// full-heap index build) — not the clear win the architectural case for the index assumes. This
    /// benchmark exists to get a statistically rigorous answer (proper warmup/iteration counts, GC
    /// isolation) rather than relying on that single small-sample check. Run manually — not part of
    /// the automated test suite — since it loads a full real dump and, per CLAUDE.md's Testing
    /// section, real-dump work must never run concurrently with anything else that also loads one.
    /// </remarks>
    [MemoryDiagnoser]
    [Config(typeof(AnalyzerBenchmarkIterationConfig))]
    [SimpleJob(
        warmupCount: AnalyzerBenchmarkIterationConfig.WarmupCount,
        iterationCount: AnalyzerBenchmarkIterationConfig.IterationCount)]
    public class ObjectAddressLookupBenchmark
    {
        private DataTarget? _dataTarget;
        private ClrRuntime? _runtime;
        private ClrHeap? _heap;
        private HeapAnalysisCache? _cache;
        private List<ulong> _sampleAddresses = new();

        [Params(100, 1_000, 10_000)]
        public int SampleCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            string dumpPath = Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
                ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

            if (!File.Exists(dumpPath))
                throw new InvalidOperationException($"Dump file not found: {dumpPath}");

            _dataTarget = DataTarget.LoadDump(dumpPath);
            _runtime = _dataTarget.ClrVersions[0].CreateRuntime();
            _heap = _runtime.Heap;

            _cache = new HeapAnalysisCache();
            _cache.PrebuildHeapIndex(_heap, dumpPath, cancellationToken: default, progress: null);

            string containerPath = DumpIndexPaths.CacheContainer(dumpPath);
            _sampleAddresses = new List<ulong>(SampleCount);
            int stride = Math.Max(1, 14_000_000 / Math.Max(1, SampleCount));
            int seen = 0;
            foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
            {
                if (_sampleAddresses.Count >= SampleCount)
                    break;
                if (entry.Address != 0 && seen % stride == 0)
                    _sampleAddresses.Add(entry.Address);
                seen++;
            }

            // Pay the one-time ObjectAddressLookup open/checksum-verify cost during setup, not
            // inside the timed benchmark methods — mirrors real usage, where the cache is opened
            // once per analysis run and shared across every analyzer's lookups.
            if (_sampleAddresses.Count > 0)
                _cache.TryGetObjectMetadata(_heap, _sampleAddresses[0], out _, out _);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _cache?.Dispose();
            _dataTarget?.Dispose();
        }

        [Benchmark(Baseline = true, Description = "heap.GetObject(address)")]
        public int LiveHeapGetObject()
        {
            int hits = 0;
            foreach (ulong addr in _sampleAddresses)
            {
                ClrObject obj = _heap!.GetObject(addr);
                if (obj.IsValid)
                    hits++;
            }
            return hits;
        }

        [Benchmark(Description = "TryGetObjectMetadata (disk index)")]
        public int DiskIndexTryGetObjectMetadata()
        {
            int hits = 0;
            foreach (ulong addr in _sampleAddresses)
            {
                if (_cache!.TryGetObjectMetadata(_heap!, addr, out _, out _))
                    hits++;
            }
            return hits;
        }
    }
}

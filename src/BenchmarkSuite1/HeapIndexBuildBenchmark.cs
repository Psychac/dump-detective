using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using System;
using System.IO;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Benchmarks the heap index build path (both memory-backed and disk-backed writers).
    /// Baseline for per-thread List&lt;HeapEntry&gt; resize allocations and ArrayPool byte[] pressure.
    /// </summary>
    [MemoryDiagnoser]
    [Config(typeof(AnalyzerBenchmarkIterationConfig))]
    [SimpleJob(
        warmupCount: AnalyzerBenchmarkIterationConfig.WarmupCount,
        iterationCount: AnalyzerBenchmarkIterationConfig.IterationCount)]
    public class HeapIndexBuildBenchmark
    {
        private DataTarget? _dataTarget;
        private ClrRuntime? _runtime;
        private ClrHeap? _heap;
        private string _dumpPath = string.Empty;

        [GlobalSetup]
        public void Setup()
        {
            _dumpPath = Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
                ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\w3wp__BALTSTPRD__PID__9704__Date__03_24_2026__Time_03_49_19PM__68__Second_Chance_Exception_E0434352.dmp";

            if (!File.Exists(_dumpPath))
                throw new InvalidOperationException($"Dump file not found: {_dumpPath}");

            _dataTarget = DataTarget.LoadDump(_dumpPath);
            _runtime = _dataTarget.ClrVersions[0].CreateRuntime();
            _heap = _runtime.Heap;
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _dataTarget?.Dispose();
        }


        [Benchmark(Description = "DiskBacked index build")]
        public long BuildDiskIndex()
        {
            var writer = new DiskBackedObjectIndexWriter();
            return writer.Build(_heap!, cancellationToken: default, progress: null, dumpPath: _dumpPath).ObjectCount;
        }
    }
}

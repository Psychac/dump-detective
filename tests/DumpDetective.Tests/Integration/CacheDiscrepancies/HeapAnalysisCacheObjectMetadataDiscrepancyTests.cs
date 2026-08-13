using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 3 validation for docs/cache/19-ObjectAddressLookupIndex.md: the correctness oracle for
/// <see cref="IHeapAnalysisCache.TryGetObjectMetadata"/> across both backing modes, against a real
/// dump — disk mode (delegates to <see cref="ObjectAddressLookup"/>) and in-memory mode (falls back
/// to live <c>heap.GetObject</c>) must both agree with a live <c>heap.GetObject</c> resolution for
/// every sampled address.
/// </summary>
public sealed class HeapAnalysisCacheObjectMetadataDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public HeapAnalysisCacheObjectMetadataDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void TryGetObjectMetadata_DiskAndMemoryModes_AgreeWithLiveHeapGetObject()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        // In-memory mode: no PrebuildHeapIndex call at all — matches every other discrepancy
        // test's "memCache" convention (e.g. StaticRootLeakDetectorDiscrepancyTests).
        HeapAnalysisCache memCache = new();

        string freshDumpPath = dumpPath + ".freshdiskcheck.HeapAnalysisCacheObjectMetadataDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);

            const int SampleStride = 100_000;
            int sampled = 0;
            int checkedCount = 0;
            int diskMismatches = 0;
            int memMismatches = 0;
            var mismatchDetails = new List<string>();

            foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
            {
                if (entry.Address == 0 || sampled % SampleStride != 0)
                {
                    sampled++;
                    continue;
                }
                sampled++;
                checkedCount++;

                ClrObject live = heap.GetObject(entry.Address);
                if (!live.IsValid)
                    continue;

                ulong liveMt = live.Type?.MethodTable ?? 0;
                ulong liveSize = live.Size;

                bool diskFound = diskCache.TryGetObjectMetadata(heap, entry.Address, out ulong diskMt, out ulong diskSize);
                if (!diskFound || diskMt != liveMt || diskSize != liveSize)
                {
                    diskMismatches++;
                    if (mismatchDetails.Count < 20)
                        mismatchDetails.Add($"DISK 0x{entry.Address:X}: found={diskFound} got MT=0x{diskMt:X} Size={diskSize}, expected MT=0x{liveMt:X} Size={liveSize}");
                }

                bool memFound = memCache.TryGetObjectMetadata(heap, entry.Address, out ulong memMt, out ulong memSize);
                if (!memFound || memMt != liveMt || memSize != liveSize)
                {
                    memMismatches++;
                    if (mismatchDetails.Count < 20)
                        mismatchDetails.Add($"MEM 0x{entry.Address:X}: found={memFound} got MT=0x{memMt:X} Size={memSize}, expected MT=0x{liveMt:X} Size={liveSize}");
                }
            }

            _output.WriteLine($"sampled: {sampled}, checked: {checkedCount}, diskMismatches: {diskMismatches}, memMismatches: {memMismatches}");
            foreach (string d in mismatchDetails)
                _output.WriteLine(d);

            diskMismatches.Should().Be(0);
            memMismatches.Should().Be(0);
            checkedCount.Should().BeGreaterThan(0);

            diskCache.Dispose();
            memCache.Dispose();
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

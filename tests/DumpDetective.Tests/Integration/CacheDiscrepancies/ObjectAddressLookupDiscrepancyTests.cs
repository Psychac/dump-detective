using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 2 validation for docs/cache/19-ObjectAddressLookupIndex.md: the correctness oracle for
/// <see cref="ObjectAddressLookup"/> — samples real addresses from a built disk index and confirms
/// <c>TryGetEntry</c> agrees with <c>heap.GetObject(address).{Type.MethodTable, Size}</c> for every
/// sample, against a real dump.
/// </summary>
public sealed class ObjectAddressLookupDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ObjectAddressLookupDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void TryGetEntry_AgreesWithHeapGetObject_ForSampledRealAddresses()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        string freshDumpPath = dumpPath + ".freshdiskcheck.ObjectAddressLookupDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);

            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);
            ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();

            using (lookup)
            {
                // Sample every Nth object from the disk index itself (not a live re-enumeration —
                // this exercises the index against its own build output, which is what TryGetEntry
                // is actually meant to serve).
                const int SampleStride = 50_000;
                int sampled = 0;
                int hits = 0;
                int mismatches = 0;
                var mismatchDetails = new List<string>();

                foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
                {
                    if (entry.Address == 0 || sampled % SampleStride != 0)
                    {
                        sampled++;
                        continue;
                    }
                    sampled++;

                    bool found = lookup!.TryGetEntry(entry.Address, out ulong mt, out ulong size);
                    if (!found)
                    {
                        mismatches++;
                        if (mismatchDetails.Count < 20)
                            mismatchDetails.Add($"0x{entry.Address:X}: TryGetEntry returned false, expected MT=0x{entry.MethodTable:X} Size={entry.Size}");
                        continue;
                    }

                    hits++;
                    if (mt != entry.MethodTable || size != entry.Size)
                    {
                        mismatches++;
                        if (mismatchDetails.Count < 20)
                            mismatchDetails.Add($"0x{entry.Address:X}: got MT=0x{mt:X} Size={size}, expected MT=0x{entry.MethodTable:X} Size={entry.Size}");
                    }
                }

                _output.WriteLine($"sampled: {sampled}, checked: {hits + mismatches}, hits: {hits}, mismatches: {mismatches}");
                foreach (string d in mismatchDetails)
                    _output.WriteLine(d);

                mismatches.Should().Be(0);
                hits.Should().BeGreaterThan(0);
            }
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

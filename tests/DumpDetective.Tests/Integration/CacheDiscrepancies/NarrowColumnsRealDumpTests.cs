using System.Diagnostics;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Format v6's narrowed object columns against a real dump — the cases the unit tests can't reach:
/// whether the width the writer picks from its own scan counters round-trips, whether every escaped
/// record still reports what ClrMD reports, and whether the streaming and binary-search decode paths
/// agree with each other.
/// </summary>
/// <remarks>
/// The escaped populations are what make this worth a real dump. Sizes escape on 0.026% of records
/// and addresses on 0.0001% (docs/cache/cache-redesign-measurements.md § 13.2), so the
/// every-100,000th-object sampling in <see cref="HeapAnalysisCacheObjectMetadataDiscrepancyTests"/>
/// expects well under one hit of either. Here every escaped record is checked and none are sampled
/// away.
/// </remarks>
public sealed class NarrowColumnsRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    /// <summary>One in this many records is cross-checked between the two decode paths.</summary>
    private const int CrossCheckStride = 50_000;

    [DiscrepancyFact]
    public void NarrowedColumns_RoundTripEveryEscapedRecordAgainstLiveHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string progressLogPath = Path.Combine(scratchRoot, "narrow-columns-progress-" + Guid.NewGuid().ToString("N") + ".log");
        output.WriteLine($"Live progress log: {progressLogPath}");

        // A fresh, dump-colocated-but-disposable index, so this never overwrites the real cache.bin
        // next to the dump and never reuses one an earlier format wrote.
        string freshDumpPath = dumpPath + ".freshdiskcheck.NarrowColumnsRealDumpTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);

        try
        {
            using var progressLog = new StreamWriter(progressLogPath, append: false) { AutoFlush = true };
            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<AnalyzerProgressReport>(r =>
                progressLog.WriteLine($"[{stopwatch.Elapsed:hh\\:mm\\:ss}] {r.Phase} (scanned {r.ScannedCount:N0})"));

            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            using var diskCache = new HeapAnalysisCache();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress);

            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);
            CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
            reader!.LostSections().Should().BeEmpty("a healthy build closes every section it opens");

            long addressWidth = Report(reader, CacheSectionId.ObjectAddresses, "ObjectAddresses");
            long sizeWidth = Report(reader, CacheSectionId.ObjectSizes, "ObjectSizes");

            reader.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry addressEntry).Should().BeTrue();
            long recordCount = addressEntry.RecordCount;

            reader.TryGetSectionInfo(CacheSectionId.ObjectAddressOverflow, out CacheTocEntry addressOverflow).Should().BeTrue();
            reader.TryGetSectionInfo(CacheSectionId.ObjectSizeOverflow, out CacheTocEntry sizeOverflow).Should().BeTrue();
            reader.TryGetSectionInfo(CacheSectionId.ObjectAddressBlockBases, out CacheTocEntry blockBases).Should().BeTrue();

            addressWidth.Should().Be(BlockDeltaColumn.DeltaWidth);
            sizeWidth.Should().BeLessThan(8, "the measured size distribution on this dump narrows to 2 bytes");
            blockBases.RecordCount.Should().Be(BlockDeltaColumn.BlockCountFor(recordCount));

            output.WriteLine($"escapes — addresses {addressOverflow.RecordCount:N0}, sizes {sizeOverflow.RecordCount:N0}; "
                + $"block bases {blockBases.RecordCount:N0}");

            var sizeSentinel = (ulong)(sizeWidth == sizeof(ushort) ? ushort.MaxValue : uint.MaxValue);
            ObjectAddressLookup.TryOpen(containerPath, out ObjectAddressLookup? lookup).Should().BeTrue();

            int escapedSizesChecked = 0;
            int crossChecked = 0;
            int mismatches = 0;
            long index = -1;
            var details = new List<string>();

            using (lookup)
            {
                foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
                {
                    index++;

                    if (entry.Size >= sizeSentinel)
                    {
                        escapedSizesChecked++;
                        ClrObject live = heap.GetObject(entry.Address);
                        if (live.IsValid && live.Size != entry.Size)
                            Record(details, ref mismatches, $"0x{entry.Address:X}: index size {entry.Size}, heap size {live.Size}");
                    }

                    // Cross-checks the two decode paths against each other: the streamed entry came
                    // through ZeroCopyColumnReader, this one through ObjectAddressLookup's binary
                    // search, and both have to decode the same block delta and escape table.
                    if (index % CrossCheckStride == 0)
                    {
                        crossChecked++;
                        if (!lookup!.TryGetEntry(entry.Address, out ulong lookupMt, out ulong lookupSize))
                            Record(details, ref mismatches, $"0x{entry.Address:X}: streamed but not findable by lookup");
                        else if (lookupMt != entry.MethodTable || lookupSize != entry.Size)
                            Record(details, ref mismatches,
                                $"0x{entry.Address:X}: lookup MT=0x{lookupMt:X} size={lookupSize}, streamed MT=0x{entry.MethodTable:X} size={entry.Size}");
                    }
                }
            }

            output.WriteLine($"records: {index + 1:N0}, escaped sizes checked: {escapedSizesChecked:N0}, "
                + $"cross-checked: {crossChecked:N0}, mismatches: {mismatches}");
            foreach (string detail in details)
                output.WriteLine(detail);

            mismatches.Should().Be(0);
            (index + 1).Should().Be(recordCount);
            escapedSizesChecked.Should().Be((int)sizeOverflow.RecordCount,
                "every record the writer escaped must read back at or above the sentinel");
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }

    private long Report(CacheContainerReader reader, CacheSectionId id, string name)
    {
        reader.TryGetSectionInfo(id, out CacheTocEntry entry).Should().BeTrue();
        long width = entry.Length / entry.RecordCount;
        output.WriteLine($"{name}: {entry.Length:N0} bytes for {entry.RecordCount:N0} records "
            + $"({width} B/record, was 8) — saved {(entry.RecordCount * 8 - entry.Length) / (1024.0 * 1024):N1} MiB");
        return width;
    }

    private static void Record(List<string> details, ref int mismatches, string detail)
    {
        mismatches++;
        if (details.Count < 20)
            details.Add(detail);
    }
}

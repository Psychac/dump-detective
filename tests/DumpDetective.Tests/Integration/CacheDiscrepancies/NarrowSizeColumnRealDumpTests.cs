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
/// Format v6's narrowed <c>ObjectSizes</c> column against a real dump — the case the unit tests
/// can't reach: whether the width the writer picks from its own scan counters actually round-trips,
/// and whether every escaped record still reports the size ClrMD reports.
/// </summary>
/// <remarks>
/// The escaped population is what makes this worth a real dump. It was measured at 0.026% of records
/// (docs/cache/cache-redesign-measurements.md § 13.2), so the every-100,000th-object sampling in
/// <see cref="HeapAnalysisCacheObjectMetadataDiscrepancyTests"/> expects well under one hit. Here
/// every escaped record is checked and none are sampled away.
/// </remarks>
public sealed class NarrowSizeColumnRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void NarrowedSizeColumn_RoundTripsEveryEscapedRecordAgainstLiveHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string progressLogPath = Path.Combine(scratchRoot, "narrow-size-progress-" + Guid.NewGuid().ToString("N") + ".log");
        output.WriteLine($"Live progress log: {progressLogPath}");

        // A fresh, dump-colocated-but-disposable index, so this never overwrites the real cache.bin
        // next to the dump and never reuses one an earlier format wrote.
        string freshDumpPath = dumpPath + ".freshdiskcheck.NarrowSizeColumnRealDumpTests";
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
            reader!.TryGetSectionInfo(CacheSectionId.ObjectSizes, out CacheTocEntry sizes).Should().BeTrue();
            reader.TryGetSectionInfo(CacheSectionId.ObjectSizeOverflow, out CacheTocEntry overflow).Should().BeTrue();

            long sizeWidth = sizes.Length / sizes.RecordCount;
            progressLog.WriteLine($"[{stopwatch.Elapsed:hh\\:mm\\:ss}] ObjectSizes {sizes.Length:N0} B / "
                + $"{sizes.RecordCount:N0} records = {sizeWidth} B per record; "
                + $"ObjectSizeOverflow {overflow.RecordCount:N0} records");
            output.WriteLine($"ObjectSizes: {sizes.Length:N0} bytes for {sizes.RecordCount:N0} records "
                + $"({sizeWidth} B/record, was 8) — saved {(sizes.RecordCount * 8 - sizes.Length) / (1024.0 * 1024):N1} MiB");
            output.WriteLine($"ObjectSizeOverflow: {overflow.RecordCount:N0} escaped records "
                + $"({100.0 * overflow.RecordCount / sizes.RecordCount:N4}% of the column)");

            NarrowColumnWidth.IsSupported((int)sizeWidth).Should().BeTrue();
            sizeWidth.Should().BeLessThan(8, "the measured size distribution on this dump narrows to 2 bytes");

            var sentinel = (ulong)(sizeWidth == sizeof(ushort) ? ushort.MaxValue : uint.MaxValue);
            int escapedChecked = 0;
            int mismatches = 0;
            var details = new List<string>();

            foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(containerPath))
            {
                if (entry.Size < sentinel)
                    continue;

                escapedChecked++;
                ClrObject live = heap.GetObject(entry.Address);
                if (!live.IsValid)
                    continue;

                if (live.Size != entry.Size)
                {
                    mismatches++;
                    if (details.Count < 20)
                        details.Add($"0x{entry.Address:X}: index reports {entry.Size}, heap reports {live.Size}");
                }
            }

            output.WriteLine($"escaped records read back: {escapedChecked:N0}, mismatches: {mismatches}");
            foreach (string detail in details)
                output.WriteLine(detail);

            mismatches.Should().Be(0);
            escapedChecked.Should().Be((int)overflow.RecordCount,
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
}

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Closes a real gap in <see cref="NarrowColumnsRealDumpTests"/>: that test takes each entry's
/// decoded <c>Address</c> as given and checks something else against it (live size at that address,
/// or agreement between the streaming and lookup decode paths). Neither is an independent oracle for
/// the address itself, and the streaming/lookup cross-check is not one either — both paths call the
/// same <see cref="DumpDetective.Analysis.Indexing.Columns.BlockDeltaColumn.Decode(uint, long)"/>, so
/// a systematic bug in that one method would make both paths agree while both are wrong.
/// </summary>
/// <remarks>
/// The oracle here is a live re-enumeration of the heap, independent of the disk index. It relies on
/// disk-mode enumeration order matching a segment walk taken in **ascending <c>Start</c> order** —
/// the order <c>DiskBackedObjectIndexWriter</c> sorts into, which is what makes the persisted column
/// globally monotonic by construction (docs/cache/cache-ideal-design.md §3.1 R1). The per-segment
/// half of that guarantee is validated independently by
/// <see cref="SegmentAddressContiguityDiscrepancyTests"/>, so this test doesn't re-prove it, just
/// relies on it to zip the two sequences positionally.
/// </remarks>
public sealed class BlockDeltaAddressExhaustiveOracleTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void DecodedAddressStream_MatchesLiveHeapEnumerationExactly_EveryRecord()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        string freshDumpPath = dumpPath + ".freshdiskcheck.BlockDeltaAddressExhaustiveOracleTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);

        try
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            using var diskCache = new HeapAnalysisCache();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);

            // The oracle: a second, independent live enumeration. Not the same ClrHeap traversal
            // the index build used — this opens no index, reads no cache.bin, and shares no code
            // path with BlockDeltaColumn at all.
            using IEnumerator<ulong> liveAddresses = EnumerateLiveAddresses(heap).GetEnumerator();
            using IEnumerator<HeapEntry> diskEntries = ObjectIndexReader.ReadDiskEntries(containerPath).GetEnumerator();

            long index = 0;
            long mismatches = 0;
            var details = new List<string>();

            while (true)
            {
                bool liveHasNext = liveAddresses.MoveNext();
                bool diskHasNext = diskEntries.MoveNext();

                if (liveHasNext != diskHasNext)
                {
                    details.Add($"record {index}: sequences diverged in length (live has next={liveHasNext}, disk has next={diskHasNext})");
                    mismatches++;
                    break;
                }

                if (!liveHasNext)
                    break;

                if (liveAddresses.Current != diskEntries.Current.Address)
                {
                    mismatches++;
                    if (details.Count < 20)
                        details.Add($"record {index}: live 0x{liveAddresses.Current:X}, disk-decoded 0x{diskEntries.Current.Address:X}");
                }

                index++;
            }

            output.WriteLine($"records compared: {index:N0}, mismatches: {mismatches}");
            foreach (string detail in details)
                output.WriteLine(detail);

            mismatches.Should().Be(0,
                "the block-delta ObjectAddresses encoding must round-trip to the exact address ClrMD reports, for every record");
            index.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }

    /// <summary>
    /// Mirrors the exact filter <c>DiskBackedObjectIndexWriter</c>'s per-segment scan loop applies
    /// (<c>!obj.IsValid || obj.Type is null || mt == 0</c> are skipped, never written to any
    /// column) — an oracle that used a different filter would report phantom mismatches that are
    /// really just a difference in what counts as "an object", not a decoding bug.
    /// </summary>
    private static IEnumerable<ulong> EnumerateLiveAddresses(ClrHeap heap)
    {
        // Ascending Start, matching DiskBackedObjectIndexWriter's own sort. The two sequences are
        // zipped positionally, so the oracle has to iterate segments in the writer's order or it is
        // comparing different orderings — which would still pass on a dump where ClrMD happens to
        // return segments already sorted, i.e. pass by luck rather than by construction.
        ClrSegment[] segments = heap.Segments.ToArray();
        Array.Sort(segments, static (a, b) => a.Start.CompareTo(b.Start));

        foreach (ClrSegment segment in segments)
        {
            foreach (ClrObject obj in segment.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.MethodTable == 0)
                    continue;

                yield return obj.Address;
            }
        }
    }
}

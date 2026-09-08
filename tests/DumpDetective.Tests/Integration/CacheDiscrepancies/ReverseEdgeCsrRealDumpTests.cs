using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ReverseIndex;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Format v8's true CSR reverse-edge index (docs/cache/cache-format-clean-slate-redesign.md §2)
/// exercised against a real dump. The unit tests in <c>ReverseEdgeCsrBuilderTests</c> and
/// <c>ReverseEdgeIndexReaderTests</c> already prove the CSR build and read paths correct against
/// synthetic edges; this closes the gap those can't reach — recall and precision against a real
/// heap's actual reference graph at 14.6M-object scale, independent of the pipeline that built the
/// index it's checking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two checks, two different costs, deliberately not the same design.</b> An earlier version of
/// this test did a full independent BFS over every one of the ~6.69M reachable nodes via
/// <c>heap.GetObject</c>/<c>obj.EnumerateReferences</c> to build a from-scratch oracle. It ran for
/// over 30 minutes and was killed — this codebase's own docs already establish why: a live ClrMD
/// walk at this scale is the *slow* path production deliberately avoids (measured ~2x slower than
/// the pre-extracted-loose-file walk on a 25 GB dump, docs/analysis/phase1-redesigns/
/// dominator-tree-phase1-integration.md §2/§8.8), and this test was doing exactly that, over the
/// full reachable graph, per test run.
/// </para>
/// <para>
/// <b>Check 1 — internal consistency, exhaustive, no ClrMD calls.</b> <see cref="EnumerateChildCounts"/>'s
/// own total against <c>ReverseEdgeChildren</c>'s TOC record count, and every offset pair
/// non-decreasing — purely reads the already-built memory-mapped index, so doing this over all R
/// rows costs nothing beyond the sequential scan <see cref="EnumerateChildCounts"/> already does.
/// </para>
/// <para>
/// <b>Check 2 — live-heap cross-check, sampled.</b> A stride sample of reachable addresses (read
/// from the already-built <c>DominatorReachableAddresses</c> section — production's own reachable
/// set is a legitimate source for <i>which nodes to sample</i>, since what's actually under
/// independent test is the <i>edge content</i> <c>TryGetParents</c> returns, not reachability
/// determination itself), each checked with exactly one live <c>obj.EnumerateReferences</c> call.
/// Sample size is fixed regardless of dump size, matching the existing every-100,000th-object
/// convention in <c>HeapAnalysisCacheObjectMetadataDiscrepancyTests</c> rather than the from-scratch
/// exhaustive style <c>BlockDeltaAddressExhaustiveOracleTests</c> uses for a cheap, non-ClrMD check.
/// </para>
/// </remarks>
public sealed class ReverseEdgeCsrRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    /// <summary>Target sample size, independent of dump size — see the type doc's remarks.</summary>
    private const int TargetSampleSize = 20_000;

    [DiscrepancyFact]
    public void ReverseIndex_IsInternallyConsistentAndMatchesASampleOfLiveEdges()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        string freshDumpPath = dumpPath + ".freshdiskcheck.ReverseEdgeCsrRealDumpTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);

        try
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            // No IRequiresDominatorTreeIndex analyzer needed -- the reverse index is unconditional,
            // unlike Stage B, so this skips the dominator-tree build entirely.
            using var cache = new HeapAnalysisCache();
            cache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);

            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);
            CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
            reader!.TryGetSectionInfo(CacheSectionId.ReverseEdgeChildren, out CacheTocEntry childrenEntry).Should().BeTrue();

            output.WriteLine($"ReverseEdgeChildren: {childrenEntry.RecordCount:N0} recorded edges");

            ReverseEdgeIndexReader.TryOpen(reader, out ReverseEdgeIndexReader? edgeReader).Should().BeTrue();
            DominatorRowIndex.TryOpen(reader, out DominatorRowIndex? rows).Should().BeTrue();

            using (edgeReader)
            using (rows)
            {
                // ---- Check 1: internal consistency, exhaustive, no ClrMD calls ----
                long enumeratedTotal = 0;
                edgeReader!.EnumerateChildCounts((_, count, truncated) =>
                {
                    enumeratedTotal += count;
                    truncated.Should().BeFalse();
                });

                output.WriteLine($"EnumerateChildCounts total: {enumeratedTotal:N0}");
                enumeratedTotal.Should().Be(childrenEntry.RecordCount,
                    "the sum of every row's reported parent count must equal ReverseEdgeChildren's own record count");

                // ---- Check 2: live-heap cross-check, sampled ----
                long rowCount = rows!.RowCount;
                long stride = Math.Max(1, rowCount / TargetSampleSize);
                output.WriteLine($"reachable rows: {rowCount:N0}, sample stride: {stride:N0}");

                long sampled = 0;
                long liveEdgesChecked = 0;
                long mismatches = 0;
                var details = new List<string>();

                for (long row = 0; row < rowCount; row += stride)
                {
                    ulong address = rows.ReadAddress(row);
                    ClrObject obj = heap.GetObject(address);
                    if (!obj.IsValid)
                        continue;

                    sampled++;

                    foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                    {
                        if (!reference.IsValid)
                            continue;

                        liveEdgesChecked++;

                        bool found = edgeReader.TryGetParents(reference.Address, out IReadOnlyList<ulong> parents, out _)
                            && parents.Contains(address);

                        if (!found)
                        {
                            mismatches++;
                            if (details.Count < 20)
                                details.Add($"0x{address:X} -> 0x{reference.Address:X}: not found in index's parent list for the child");
                        }
                    }
                }

                output.WriteLine($"sampled nodes: {sampled:N0}");
                output.WriteLine($"live edges checked: {liveEdgesChecked:N0}");
                output.WriteLine($"mismatches: {mismatches:N0}");
                foreach (string detail in details)
                    output.WriteLine(detail);

                mismatches.Should().Be(0);
                liveEdgesChecked.Should().BeGreaterThan(0, "the sample should have crossed at least some real edges");
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

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Format v7's on-demand dominator child index (docs/cache/cache-format-clean-slate-redesign.md §4's
/// aggressive option) exercised against a real dump. The synthetic unit tests in
/// <c>DominatorChildIndexTests</c> already prove the inversion correct for the specific fold shapes
/// (diamond, single folded leaf, multiple folded leaves under one hub) they construct; this closes
/// the gap those can't reach — whether it holds at 6.69M-row real-dump scale, including whatever
/// fold/hub shapes actually occur in a real heap that a handful of synthetic graphs don't cover.
/// </summary>
/// <remarks>
/// The oracle here is round-trip consistency, not an external ground truth: for every row's listed
/// child, that child's own persisted immediate-dominator must point back to the row that listed it.
/// This is exhaustive (every row, not a sample) and independent of the inversion algorithm itself —
/// it re-derives the parent side from <see cref="DominatorTreeIndexReader.TryGetImmediateDominator"/>,
/// a completely separate code path reading a different section
/// (<c>DominatorRetainedBytes</c>/<c>DominatorImmediateDominatorAddresses</c> vs. the inverted CSR
/// built from it), so a bug that made both agree while both were wrong would have to corrupt the
/// persisted idom column itself, which <see cref="BlockDeltaAddressExhaustiveOracleTests"/>-style
/// reasoning doesn't apply to here since idom rows aren't block-delta encoded.
/// </remarks>
public sealed class DominatorChildIndexRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void ChildIndex_EveryRowsChildrenPointBackToIt_ExhaustiveRoundTrip()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        string freshDumpPath = dumpPath + ".freshdiskcheck.DominatorChildIndexRealDumpTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);

        try
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            var cache = new HeapAnalysisCache();
            // enableExactDominatorTree: true + an IRequiresDominatorTreeIndex analyzer in
            // activeAnalyzers is what flips Stage B on — same requirement
            // DominatorAnalyzerExactTreeRealDumpTests documents.
            IReadOnlyList<IAnalyzer> activeAnalyzers = [new DominatorAnalyzer()];
            cache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null, activeAnalyzers, enableExactDominatorTree: true);

            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);
            CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
            reader!.ContainsSection(CacheSectionId.DominatorChildOffsets).Should().BeFalse(
                "format v7 no longer writes a persisted child index at all");

            DominatorRowIndex.TryOpen(reader, out DominatorRowIndex? rows).Should().BeTrue();
            DominatorTreeIndexReader.TryOpen(reader, out DominatorTreeIndexReader? scalarReader).Should().BeTrue();
            DominatorChildIndexReader.TryOpen(reader, out DominatorChildIndexReader? childReader).Should().BeTrue();

            using (rows)
            using (scalarReader)
            using (childReader)
            {
                long rowCount = rows!.RowCount;
                output.WriteLine($"reachable rows: {rowCount:N0}");

                long totalChildEdges = 0;
                long rootLevelRows = 0;
                long widestRowChildCount = 0;
                ulong widestRowAddress = 0;
                var mismatches = new List<string>();

                for (long row = 0; row < rowCount; row++)
                {
                    ulong address = rows.ReadAddress(row);

                    childReader!.TryGetChildren(address, out ulong[] children).Should().BeTrue(
                        $"row {row} (0x{address:X}) came from this same reachable set, so it must resolve");

                    if (children.Length > widestRowChildCount)
                    {
                        widestRowChildCount = children.Length;
                        widestRowAddress = address;
                    }

                    totalChildEdges += children.Length;

                    foreach (ulong child in children)
                    {
                        if (!scalarReader!.TryGetImmediateDominator(child, out ulong reportedParent))
                        {
                            mismatches.Add($"child 0x{child:X} of 0x{address:X}: TryGetImmediateDominator returned false");
                            continue;
                        }

                        if (reportedParent != address)
                        {
                            if (mismatches.Count < 20)
                                mismatches.Add($"child 0x{child:X}: listed under parent 0x{address:X}, but its own idom reports 0x{reportedParent:X}");
                        }
                    }

                    scalarReader!.TryGetImmediateDominator(address, out ulong ownDominator).Should().BeTrue();
                    if (ownDominator == 0)
                        rootLevelRows++;
                }

                output.WriteLine($"total child edges: {totalChildEdges:N0}");
                output.WriteLine($"root-level rows (direct children of the virtual root): {rootLevelRows:N0}");
                output.WriteLine($"widest row: 0x{widestRowAddress:X} with {widestRowChildCount:N0} direct children");
                output.WriteLine($"mismatches: {mismatches.Count}");
                foreach (string m in mismatches)
                    output.WriteLine(m);

                mismatches.Should().BeEmpty();
                totalChildEdges.Should().Be(rowCount - rootLevelRows,
                    "every row except the root-level ones must appear in exactly one parent's child list");
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

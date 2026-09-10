using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Phase 1 GC-root capability retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>GCRootAnalyzerRetypingCharacterizationTests</c>) cross-checks
/// internal-consistency invariants on a small live process heap; this proves the same on a large
/// real dump — hundreds of thousands of roots, real disk root/type indices.
///
/// Doesn't request Stage B's exact dominator tree explicitly and reuses this dump's real,
/// already-persisted cache.bin as-is (no scratch-dir redirection, unlike
/// <c>DominatorAnalyzerExactTreeRealDumpTests</c>) — so whether the exact-retained-bytes path
/// fires here depends on whatever Stage B state that cache already carries from earlier work, not
/// anything this test forces either way. Both branches are asserted for internal consistency
/// (see <c>hasExactDominator</c> below) rather than assuming one.
/// </summary>
public sealed class GCRootAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public GCRootAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using GCRootAnalyzerLegacyAdapter adapter = new();
        var result = (GCRootDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("GC Root Analysis");
        result.Category.Should().Be("Memory");

        result.TotalRoots.Should().BeGreaterThan(0, "a real IIS crash dump has live GC roots");

        int nonDroppedRootCount = result.TotalRoots - result.DroppedZeroEstimateRootCount;
        result.TopRootsBySeverity.Should().HaveCount(nonDroppedRootCount);
        result.RootOwnedSubgraphs.Should().HaveCount(nonDroppedRootCount);

        // Whether Stage B's exact dominator tree is available depends on this dump's persisted
        // cache.bin (reused as-is here, not redirected to a scratch dir — this dump's on-disk
        // cache may already carry Stage B from earlier work), not anything this test controls
        // directly — so both branches are checked for internal consistency rather than assuming
        // one. IsExactRetainedBytes is the single upfront gate GCRootAnalyzer computes once per
        // run (see hasExactDominator), so every kind/finding/subgraph should agree on it.
        bool hasExactDominator = result.ByKind.Count > 0 && result.ByKind[0].IsExactRetainedBytes;
        _output.WriteLine($"Stage B dominator tree available for this run: {hasExactDominator}");

        int sumByKindCounts = 0;
        ulong sumByKindBytes = 0;
        foreach (RootKindSummary kind in result.ByKind)
        {
            sumByKindCounts += kind.Count;
            sumByKindBytes += kind.EstimatedRetainedBytes;
            kind.IsExactRetainedBytes.Should().Be(hasExactDominator);
            kind.PctOfManagedHeap.Should().BeInRange(0.0, 100.0);
        }
        sumByKindCounts.Should().Be(nonDroppedRootCount);

        for (int i = 1; i < result.ByKind.Count; i++)
            result.ByKind[i].EstimatedRetainedBytes.Should().BeLessThanOrEqualTo(result.ByKind[i - 1].EstimatedRetainedBytes);

        for (int i = 1; i < result.TopRootsBySeverity.Count; i++)
            result.TopRootsBySeverity[i].SeverityScore.Should().BeLessThanOrEqualTo(result.TopRootsBySeverity[i - 1].SeverityScore);

        foreach (RootFinding finding in result.TopRootsBySeverity)
        {
            finding.RetainedBytesIsExact.Should().Be(hasExactDominator);
            finding.TargetAddress.Should().NotBe(0UL);
        }

        foreach (RootOwnedSubgraphFinding subgraph in result.RootOwnedSubgraphs)
            subgraph.SubgraphNodeCount.Should().Be(subgraph.SubgraphTypeNames.Count);

        _output.WriteLine(
            $"TotalRoots={result.TotalRoots:N0} Dropped={result.DroppedZeroEstimateRootCount:N0} " +
            $"Kinds={result.ByKind.Count:N0} SumRetainedBytes={sumByKindBytes:N0} " +
            $"SubgraphWalkCapped={result.SubgraphWalkCapped} ({result.SubgraphWalkCappedCount:N0} capped)");
        foreach (RootKindSummary kind in result.ByKind)
            _output.WriteLine($"  {kind.Kind}: {kind.Count:N0} roots, {kind.EstimatedRetainedBytes:N0} bytes, {kind.PctOfManagedHeap:F2}% of heap");
    }
}

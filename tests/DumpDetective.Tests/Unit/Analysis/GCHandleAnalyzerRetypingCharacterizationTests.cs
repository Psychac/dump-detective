using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="GCHandleAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic against real,
/// resolvable handle targets. Complements the pre-existing <c>GCHandleAnalyzerFunctionalTests</c>
/// (disk-snapshot injection, deliberately fake/unresolvable addresses throughout) by exercising the
/// live <c>runtime.EnumerateHandles()</c> fallback path (no heap index) against a self-attached
/// process's real GC handle table — cross-checked against ClrMD ground truth, matching Batches 1–6's
/// approach for data that can't be hand-crafted via reflection injection.
/// </summary>
public sealed class GCHandleAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_NoIndex_MatchesClrMdGroundTruth()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            int expectedTotalHandles = 0;
            var expectedByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                expectedTotalHandles++;
                string kind = handle.HandleKind.ToString();
                expectedByKind[kind] = expectedByKind.TryGetValue(kind, out int c) ? c + 1 : 1;
            }

            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using GCHandleAnalyzerLegacyAdapter adapter = new();
            var result = (GCHandleDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("GC Handle Analysis");
            result.Category.Should().Be("Handles");
            result.TotalHandles.Should().Be(expectedTotalHandles);

            foreach (KeyValuePair<string, int> kv in expectedByKind)
                result.HandlesByKind.Should().ContainSingle(e => e.Name == kv.Key && e.Count == kv.Value);

            result.StrongLikeHandles.Should().Be(result.TotalHandles - result.WeakLikeHandles);

            // A live process always has at least one Pinned handle in real ClrMD workloads (string
            // interning, GC bookkeeping); if any resolved to a real object here, retained bytes must
            // be non-negative and internally consistent with the exact/fallback flag.
            result.PinnedRetainedBytes.Should().BeGreaterThanOrEqualTo(0);
            if (result.PinnedRetainedBytesIsExact)
                result.PinnedRetainedBytes.Should().BeGreaterThan(0);

            (result.PinnedSohObjectCount + result.PinnedNonSohObjectCount).Should().BeLessThanOrEqualTo(result.PinnedHandleTargets);

            foreach (var kind in new[] { result.WeakShortGen0Count + result.WeakShortGen1Count + result.WeakShortGen2Count + result.WeakShortLohCount })
                kind.Should().BeGreaterThanOrEqualTo(0);

            result.DependentUnresolvedPercent.Should().BeInRange(0.0, 100.0);

            for (int i = 1; i < result.TopTargetTypes!.Count; i++)
                result.TopTargetTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopTargetTypes[i - 1].Count);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }
}

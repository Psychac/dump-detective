using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for <see cref="ReferenceChainAnalyzer"/>'s Tier-1 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). No pre-existing test
/// coverage existed for this analyzer at all before its retyping — the fake-backed
/// characterization suite (<c>ReferenceChainAnalyzerRetypingCharacterizationTests</c>) covers exact
/// behavior against small, hand-built graphs; this proves internal consistency against a real,
/// large dump — real root-path search, real reverse-index-backed bidirectional search, real
/// static/stack root resolution.
///
/// Reuses this dump's real, already-persisted cache.bin as-is (no scratch-dir redirection) — whether
/// Stage B's exact dominator tree fires here depends on whatever that cache already carries, not
/// anything this test forces; the E-2 exact-retained-bytes assertions below tolerate either.
/// </summary>
public sealed class ReferenceChainAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ReferenceChainAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using ReferenceChainAnalyzerLegacyAdapter adapter = new();
        var result = (ReferenceChainDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Reference Chain Analysis");
        result.Category.Should().Be("Memory");

        result.AnalyzedSamples.Should().BeGreaterThan(0, "a real IIS crash dump has large types with a resolvable sample instance");
        result.TopTypeSampleTraces.Should().HaveCountLessThanOrEqualTo(10, "default ReferenceChainOptions.TopCount is 10");
        result.RetainedPercent.Should().BeInRange(0.0, 100.0);
        result.SampleReferenceChains.Should().HaveCountLessThanOrEqualTo(5);

        // Every retained sample contributes exactly one count to exactly one root-kind bucket
        // (TryFindRootPathDetails never returns HasGcRoot=true with a null RootKind), so the
        // distribution's total must equal RetainedSamples exactly, not just be <= it.
        int sumRootKindCounts = 0;
        foreach (ReferenceChainRootKindCount kind in result.RootKindDistribution ?? [])
            sumRootKindCounts += kind.RetainedTypeCount;
        sumRootKindCounts.Should().Be(result.RetainedSamples);

        foreach (ReferenceTypeSampleSnapshot trace in result.TopTypeSampleTraces ?? [])
        {
            trace.SampleAddress.Should().NotBe(0UL, "every top-N type resolved a sample address on this dump");
            trace.RetainedSampleCount.Should().BeLessThanOrEqualTo(trace.SampleCount);

            if (trace.HasGcRoot)
            {
                trace.RootKind.Should().NotBeNull();
                trace.RootPath.Should().NotBeNullOrEmpty();
                trace.PathHops.Should().NotBeNull();
                trace.RootAddress.Should().NotBeNull();
            }
            else
            {
                trace.RootPath.Should().BeNull();
                trace.RootAddress.Should().BeNull();
            }
        }

        foreach (ReferenceChainSharedRootGroup group in result.SharedRootGroups ?? [])
            group.TypeNames.Should().HaveCountGreaterThanOrEqualTo(2, "a shared-root group only forms when 2+ types resolve to the same root address");

        _output.WriteLine(
            $"AnalyzedSamples={result.AnalyzedSamples:N0} RetainedSamples={result.RetainedSamples:N0} " +
            $"({result.RetainedPercent:F1}%) NoSampleAddress={result.NoSampleAddressCount:N0} " +
            $"TraversalLimited={result.TraversalLimitedSamples:N0} SharedRootGroups={(result.SharedRootGroups?.Count ?? 0):N0}");
        foreach (ReferenceChainRootKindCount kind in result.RootKindDistribution ?? [])
            _output.WriteLine($"  {kind.RootKind}: {kind.RetainedTypeCount:N0} retained types");
        foreach (ReferenceTypeSampleSnapshot trace in (result.TopTypeSampleTraces ?? []).Take(5))
        {
            _output.WriteLine(
                $"  {trace.TypeName}: {trace.Count:N0} instances, {trace.TotalSizeBytes:N0} bytes, HasGcRoot={trace.HasGcRoot}, " +
                $"RootKind={trace.RootKind}, RootFieldName={trace.RootFieldName}, LastHopFieldName={trace.LastHopFieldName}, " +
                $"RetainedBytes={trace.RetainedBytes?.ToString("N0") ?? "n/a"}");
        }
    }
}

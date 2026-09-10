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
/// <see cref="JitAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic and invariants against
/// a real, live self-attached process — thread stack/JIT data can't be hand-crafted via reflection
/// injection any more than segment/root data could (Batches 1–4's same reasoning), so this checks
/// internal-consistency invariants rather than hand-computed fixture values.
/// </summary>
public sealed class JitAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_ProducesInternallyConsistentResult()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

            using JitAnalyzerLegacyAdapter adapter = new();
            var result = (JitDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.AnalyzerName.Should().Be("JIT Analysis");
            result.Category.Should().Be("Performance");

            result.JitManagerCount.Should().BeGreaterThan(0, "a live .NET process always has at least one JIT manager");
            result.TotalJitHeapBytes.Should().BeGreaterThan(0UL, "the running test host has JIT-compiled code");

            result.ActiveMethodsOnStacks.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);
            result.DistinctMethodsOnStacks.Should().BeLessThanOrEqualTo(result.ActiveMethodsOnStacks);
            result.ReadyToRunFrameCount.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);
            result.DynamicMethodFrameCount.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);

            // A live, running process's own threads always resolve at least some managed frames.
            result.ManagedFrameCount.Should().BeGreaterThan(0);
            result.MaxThreadFrameDepth.Should().BeGreaterThan(0);

            for (int i = 1; i < result.TopLargestMethods.Count; i++)
            {
                ulong sizeA = (ulong)result.TopLargestMethods[i - 1].HotSize + result.TopLargestMethods[i - 1].ColdSize;
                ulong sizeB = (ulong)result.TopLargestMethods[i].HotSize + result.TopLargestMethods[i].ColdSize;
                sizeB.Should().BeLessThanOrEqualTo(sizeA);
            }
            foreach (JitMethodSnapshot method in result.TopLargestMethods)
                ((ulong)method.HotSize + method.ColdSize).Should().BeGreaterThanOrEqualTo(result.LargeMethodThresholdBytes);

            for (int i = 1; i < result.TopActiveFrameTypes.Count; i++)
                result.TopActiveFrameTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopActiveFrameTypes[i - 1].Count);
            for (int i = 1; i < result.TopActiveModulesByFrameHits.Count; i++)
                result.TopActiveModulesByFrameHits[i].Count.Should().BeLessThanOrEqualTo(result.TopActiveModulesByFrameHits[i - 1].Count);

            result.LargeMethodThresholdBytes.Should().Be(64u * 1024);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }
}

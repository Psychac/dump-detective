using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the thread-domain quartet retyping
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md): proves
/// <see cref="ThreadStackClusterAnalyzerLegacyAdapter"/> reproduces pre-retyping arithmetic against a
/// self-attached process's real thread set, cross-checked against ClrMD ground truth. Complements
/// <c>ThreadStackClusterAnalyzerOptionsTests</c>' pure-logic coverage of the preserved static helpers
/// (<c>BuildClusterTree</c>, <c>ClassifyFrameworkPattern</c>) and <c>AnalysisPipelineTests</c>' shared-
/// scan gate.
/// </summary>
public sealed class ThreadStackClusterAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_MatchesClrMdGroundTruth()
    {
        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        int expectedAliveThreads = 0;
        foreach (ClrThread thread in runtime.Threads)
        {
            if (thread.IsAlive)
                expectedAliveThreads++;
        }

        HeapAnalysisCache cache = new();
        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using ThreadStackClusterAnalyzerLegacyAdapter adapter = new();
        var result = (ThreadStackClusterDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Thread Stack Signature Clustering");
        result.Category.Should().Be("Threads");
        result.AliveThreadCount.Should().Be(expectedAliveThreads);

        int sumOfClusterCounts = 0;
        foreach (ThreadClusterSnapshot cluster in result.TopClusters ?? [])
            sumOfClusterCounts += cluster.Count;
        sumOfClusterCounts.Should().Be(expectedAliveThreads);

        result.UniqueClusters.Should().Be(result.TopClusterSignatures.Count);
        result.SingletonSignatures.Should().BeLessThanOrEqualTo(result.UniqueClusters);

        foreach (ThreadClusterSnapshot cluster in result.TopClusters ?? [])
            cluster.SampleOsThreadIds.Count.Should().Be(cluster.Count);
    }
}

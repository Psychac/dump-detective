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
/// Real-dump verification for the thread-domain quartet retyping
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md). The self-attached-heap
/// characterization test only exercises the current test host's own small thread set; this proves
/// the same invariants against a large real dump — hundreds of threads, real clustering diversity.
/// </summary>
public sealed class ThreadStackClusterAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ThreadStackClusterAnalyzerRealDumpTests(ITestOutputHelper output)
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
        result.AliveThreadCount.Should().Be(expectedAliveThreads);
        result.UniqueClusters.Should().Be(result.TopClusterSignatures.Count);
        result.UniqueClusters.Should().BeLessThanOrEqualTo(result.AliveThreadCount);

        int sumOfClusterCounts = 0;
        foreach (ThreadClusterSnapshot cluster in result.TopClusters ?? [])
        {
            sumOfClusterCounts += cluster.Count;
            cluster.SampleOsThreadIds.Count.Should().Be(cluster.Count);
        }
        sumOfClusterCounts.Should().Be(expectedAliveThreads);

        _output.WriteLine(
            $"AliveThreads={result.AliveThreadCount:N0} UniqueClusters={result.UniqueClusters:N0} " +
            $"Singletons={result.SingletonSignatures:N0} Diversity={result.DiversityPercent:N1}%");
    }
}

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Async;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="AsyncStateMachineAnalyzerLegacyAdapter"/> produces an internally consistent
/// result against a real, large dump -- including the suspend-state histogram, now built from the
/// pipeline's one shared heap-index scan instead of the pre-retyping analyzer's own separate second
/// full-heap pass.
/// </summary>
public sealed class AsyncStateMachineAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public AsyncStateMachineAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using AsyncStateMachineAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (AsyncStateMachineDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Async State Machine Analysis");

        if (result.TotalStateMachines == 0)
        {
            result.TopStateMachineTypes.Should().BeEmpty();
            _output.WriteLine("No async state machine types found on this dump's heap -- empty result is correct.");
            return;
        }

        result.TotalStateMachines.Should().BeGreaterThan(0);
        result.TotalStateMachineBytes.Should().BeGreaterThan(0UL);

        // Per-type profile counts/bytes can undercount the grand totals (types flagged
        // IsAsyncStateMachineType but never actually streamed by the shared scan, or whose name
        // didn't match the candidate regex), but never exceed them.
        long sumTypeCounts = 0;
        ulong sumTypeBytes = 0;
        foreach (StateMachineTypeProfile p in result.TopStateMachineTypes)
        {
            sumTypeCounts += p.Count;
            sumTypeBytes += p.TotalBytes;
            p.Count.Should().BeGreaterThan(0);
            p.ReferenceFieldCount.Should().BeGreaterThanOrEqualTo(0);
            p.Gen2Fraction.Should().BeInRange(0.0, 1.0);
            foreach ((int _, int stateCount) in p.StateDistribution)
                stateCount.Should().BeGreaterThan(0);
        }
        sumTypeCounts.Should().BeLessThanOrEqualTo(result.TotalStateMachines);
        sumTypeBytes.Should().BeLessThanOrEqualTo(result.TotalStateMachineBytes);

        for (int i = 1; i < result.TopStateMachineTypes.Count; i++)
            result.TopStateMachineTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopStateMachineTypes[i - 1].Count);

        foreach (HighCaptureStateMachine capture in result.TopByCapturedSize)
            capture.TotalCapturedRefBytes.Should().BeGreaterThan(0UL);
        for (int i = 1; i < result.TopByCapturedSize.Count; i++)
            result.TopByCapturedSize[i].TotalCapturedRefBytes.Should().BeLessThanOrEqualTo(result.TopByCapturedSize[i - 1].TotalCapturedRefBytes);

        long sumSuspended = 0;
        foreach (SuspendedMethodEntry entry in result.SuspendedMethodMap)
        {
            sumSuspended += entry.SuspendedCount;
            entry.SuspendedCount.Should().BeGreaterThan(0);
        }
        sumSuspended.Should().Be(sumTypeCounts);

        _output.WriteLine(
            $"TotalStateMachines={result.TotalStateMachines} TotalBytes={result.TotalStateMachineBytes} " +
            $"TotalGen2Count={result.TotalGen2Count} TypeCount={result.TopStateMachineTypes.Count} " +
            $"HighCaptureCount={result.TopByCapturedSize.Count} SuspendedMethodCount={result.SuspendedMethodMap.Count}");
    }
}

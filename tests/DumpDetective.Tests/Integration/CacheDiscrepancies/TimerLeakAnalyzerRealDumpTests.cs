using System.Linq;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Infra;
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
/// proves <see cref="TimerLeakAnalyzer"/> produces an internally consistent result
/// against a real, large dump -- including the interval histogram now built in the same shared scan
/// as the main tally, rather than the pre-retyping analyzer's own separate second full-heap pass.
/// </summary>
public sealed class TimerLeakAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public TimerLeakAnalyzerRealDumpTests(ITestOutputHelper output)
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

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using TimerLeakAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (TimerLeakDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Timer Leak Analysis");

        if (!result.TimersFound)
        {
            result.TotalTimers.Should().Be(0);
            _output.WriteLine("No timer-shaped objects found on this dump's heap -- empty result is correct.");
            return;
        }

        result.TotalTimers.Should().BeGreaterThan(0);
        int sumByCategory = result.ThreadingTimerCount + result.TimersTimerCount + result.TimerQueueTimerCount
            + result.TimerHolderCount + result.PeriodicTimerCount + result.OtherTimerCount;
        sumByCategory.Should().Be(result.TotalTimers);

        int sumByType = 0;
        foreach (TimerObjectTypeSummary t in result.ByType) sumByType += t.Count;
        sumByType.Should().Be(result.TotalTimers);

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].Count.Should().BeLessThanOrEqualTo(result.ByType[i - 1].Count);

        int sumHistogram = 0;
        foreach ((string _, int count) in result.IntervalHistogram) sumHistogram += count;
        sumHistogram.Should().Be(result.TimerQueueTimerCount);

        int withEvidence = 0, withCallbackOwner = 0;
        foreach (TimerObjectTypeSummary t in result.ByType)
        {
            if (t.Evidence is not null) withEvidence++;
            if (t.Samples is { Count: > 0 } samples && samples[0].CallbackOwnerType is not null) withCallbackOwner++;
        }

        _output.WriteLine(
            $"TotalTimers={result.TotalTimers} Threading={result.ThreadingTimerCount} Timers={result.TimersTimerCount} " +
            $"TimerQueue={result.TimerQueueTimerCount} TimerHolder={result.TimerHolderCount} Periodic={result.PeriodicTimerCount} " +
            $"Other={result.OtherTimerCount} ByTypeCount={result.ByType.Count} WithEvidence={withEvidence} WithCallbackOwner={withCallbackOwner} " +
            $"IntervalHistogram=[{string.Join(", ", result.IntervalHistogram.Select(b => $"{b.Bucket}={b.Count}"))}]");
    }
}

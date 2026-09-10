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
/// Real-dump verification for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>JitAnalyzerRetypingCharacterizationTests</c>) only exercises the
/// current test-host process's own small thread set; this proves the same invariants against a
/// large real dump — hundreds of threads, real ReadyToRun/dynamic-module code, real tiering.
/// </summary>
public sealed class JitAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public JitAnalyzerRealDumpTests(ITestOutputHelper output)
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

        HeapAnalysisCache cache = new();
        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using JitAnalyzerLegacyAdapter adapter = new();
        var result = (JitDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("JIT Analysis");
        result.JitManagerCount.Should().BeGreaterThan(0);
        result.TotalJitHeapBytes.Should().BeGreaterThan(0UL);
        result.ManagedFrameCount.Should().BeGreaterThan(0, "a real IIS crash dump has live managed thread stacks");

        result.ActiveMethodsOnStacks.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);
        result.DistinctMethodsOnStacks.Should().BeLessThanOrEqualTo(result.ActiveMethodsOnStacks);
        result.ReadyToRunFrameCount.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);
        result.DynamicMethodFrameCount.Should().BeLessThanOrEqualTo(result.ManagedFrameCount);

        for (int i = 1; i < result.TopLargestMethods.Count; i++)
        {
            ulong sizeA = (ulong)result.TopLargestMethods[i - 1].HotSize + result.TopLargestMethods[i - 1].ColdSize;
            ulong sizeB = (ulong)result.TopLargestMethods[i].HotSize + result.TopLargestMethods[i].ColdSize;
            sizeB.Should().BeLessThanOrEqualTo(sizeA);
        }

        for (int i = 1; i < result.TopActiveFrameTypes.Count; i++)
            result.TopActiveFrameTypes[i].Count.Should().BeLessThanOrEqualTo(result.TopActiveFrameTypes[i - 1].Count);
        for (int i = 1; i < result.TopActiveModulesByFrameHits.Count; i++)
            result.TopActiveModulesByFrameHits[i].Count.Should().BeLessThanOrEqualTo(result.TopActiveModulesByFrameHits[i - 1].Count);

        _output.WriteLine(
            $"JitManagers={result.JitManagerCount} TotalJitHeapBytes={result.TotalJitHeapBytes:N0} " +
            $"ManagedFrames={result.ManagedFrameCount:N0} UnmanagedFrames={result.UnmanagedFrameCount:N0} " +
            $"ActiveMethodsOnStacks={result.ActiveMethodsOnStacks:N0} DistinctMethodsOnStacks={result.DistinctMethodsOnStacks:N0} " +
            $"ReadyToRunFrames={result.ReadyToRunFrameCount:N0} DynamicFrames={result.DynamicMethodFrameCount:N0} " +
            $"TieredMethodCount={result.TieredMethodCount:N0} MaxThreadFrameDepth={result.MaxThreadFrameDepth:N0} " +
            $"(OSThread {result.MaxThreadFrameDepthOSThreadId})");
        if (result.TopLargestMethods.Count > 0)
            _output.WriteLine($"Largest method: {result.TopLargestMethods[0].Signature} ({(ulong)result.TopLargestMethods[0].HotSize + result.TopLargestMethods[0].ColdSize:N0} bytes)");
    }
}

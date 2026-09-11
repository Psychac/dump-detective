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
/// proves <see cref="WcfChannelAnalyzerLegacyAdapter"/> produces an internally consistent result
/// against a real, large .NET Framework dump -- the one place that can exercise the remote-address
/// fix's "m_String" (.NET Framework <c>System.Uri</c>) field name for real, rather than only the
/// "_string" (modern .NET) name <c>WcfChannelAnalyzerLiveHeapTests</c> exercises against this
/// process's own runtime.
/// </summary>
public sealed class WcfChannelAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public WcfChannelAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using WcfChannelAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (WcfChannelDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("WCF Channel Analysis");

        if (!result.WcfPresent)
        {
            result.TotalChannels.Should().Be(0);
            result.FactoryCount.Should().Be(0);
            _output.WriteLine("No WCF-shaped objects found on this dump's heap -- empty result is correct.");
            return;
        }

        int sumByState = result.OpeningChannels + result.OpenedChannels + result.FaultedChannels
            + result.ClosingChannels + result.ClosedChannels + result.OtherChannels + result.InvalidStateCount;
        sumByState.Should().BeLessThanOrEqualTo(result.TotalChannels); // unreadable-state instances count toward Total but no bucket

        int sumByType = 0;
        foreach (WcfChannelTypeSummary t in result.ByType) sumByType += t.TotalCount;
        sumByType.Should().Be(result.TotalChannels);

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].TotalCount.Should().BeLessThanOrEqualTo(result.ByType[i - 1].TotalCount);

        foreach (WcfChannelSnapshot faulted in result.TopFaultedChannels)
            faulted.StateValue.Should().Be(5);

        _output.WriteLine(
            $"TotalChannels={result.TotalChannels} Opening={result.OpeningChannels} Opened={result.OpenedChannels} " +
            $"Faulted={result.FaultedChannels} Closing={result.ClosingChannels} Closed={result.ClosedChannels} " +
            $"Other={result.OtherChannels} InvalidState={result.InvalidStateCount} FactoryCount={result.FactoryCount} " +
            $"Duplex={result.DuplexChannelCount} Session={result.SessionChannelCount} " +
            $"TopFaultedChannels={result.TopFaultedChannels.Count} " +
            $"WithRemoteAddress={result.TopFaultedChannels.Count(s => s.RemoteAddress is not null)}");
    }
}

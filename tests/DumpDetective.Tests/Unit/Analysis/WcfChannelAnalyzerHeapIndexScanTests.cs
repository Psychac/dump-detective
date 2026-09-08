using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class WcfChannelAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshWcfChannelAnalyzer()
    {
        WcfChannelAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<WcfChannelAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsStateChangeCounts_PerMethodTable()
    {
        WcfChannelAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("SomeChannel", 100, opening: 1, opened: 10, faulted: 3, closing: 2, closed: 5, other: 2, invalidState: 1, bytes: 999)
        });

        WcfChannelAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("SomeChannel", 100, opening: 0, opened: 7, faulted: 2, closing: 1, closed: 4, other: 1, invalidState: 2, bytes: 999)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var merged = GetTypeStats(primary)[0x1000];
        merged.Total.Should().Be(100); // not summed — pre-seeded from TypeAggregates
        merged.Opening.Should().Be(1);
        merged.Opened.Should().Be(17);
        merged.Faulted.Should().Be(5);
        merged.Closing.Should().Be(3);
        merged.Closed.Should().Be(9);
        merged.Other.Should().Be(3);
        merged.InvalidState.Should().Be(3);
        merged.Bytes.Should().Be(999); // not summed
    }

    [Fact]
    public void MergePartial_AddsNewKeyFromWorker_WhenNotPresentInPrimary()
    {
        WcfChannelAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("TypeA", 50, 1, 10, 2, 0, 5, 1, 0, 100)
        });

        WcfChannelAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>
        {
            [0x2000] = ("TypeB", 30, 0, 5, 1, 2, 3, 0, 0, 200)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var stats = GetTypeStats(primary);
        stats.Should().ContainKey(0x1000UL);
        stats.Should().ContainKey(0x2000UL);
        stats[0x2000].Faulted.Should().Be(1);
    }

    [Theory]
    [InlineData(0, true)]  // Created
    [InlineData(1, true)]  // Opening
    [InlineData(5, true)]  // Faulted
    [InlineData(-1, false)]
    [InlineData(6, false)]
    [InlineData(int.MaxValue, false)]
    public void IsValidCommunicationState_MatchesTheDefinedEnumRange(int stateVal, bool expected)
    {
        WcfChannelAnalyzer.IsValidCommunicationState(stateVal).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.HttpsChannelFactory+HttpsRequestChannel", false)]
    public void IsDuplexChannelType_MatchesDuplexToken(string typeName, bool expected)
    {
        WcfChannelAnalyzer.IsDuplexChannelType(typeName).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.SecurityChannelFactory+SecurityRequestSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.HttpsChannelFactory+HttpsRequestChannel", false)]
    public void IsSessionChannelType_MatchesSessionToken(string typeName, bool expected)
    {
        WcfChannelAnalyzer.IsSessionChannelType(typeName).Should().Be(expected);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static void SeedTypeStats(
        WcfChannelAnalyzer analyzer,
        Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, int InvalidState, ulong Bytes)> stats)
    {
        typeof(WcfChannelAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, stats);

        // Seed an empty sampler so MergePartial doesn't null-deref.
        var sampler = new InstanceStateSampler<WcfChannelSnapshot>();
        typeof(WcfChannelAnalyzer)
            .GetField("_sampler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, sampler);
    }

    private static Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, int InvalidState, ulong Bytes)>
        GetTypeStats(WcfChannelAnalyzer analyzer) =>
        (Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>)typeof(WcfChannelAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}

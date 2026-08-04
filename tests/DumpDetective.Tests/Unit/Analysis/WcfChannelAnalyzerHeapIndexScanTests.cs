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
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("SomeChannel", 100, opening: 1, opened: 10, faulted: 3, closing: 2, closed: 5, other: 2, bytes: 999)
        });

        WcfChannelAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("SomeChannel", 100, opening: 0, opened: 7, faulted: 2, closing: 1, closed: 4, other: 1, bytes: 999)
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
        merged.Bytes.Should().Be(999); // not summed
    }

    [Fact]
    public void MergePartial_AddsNewKeyFromWorker_WhenNotPresentInPrimary()
    {
        WcfChannelAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, int, int, int, ulong)>
        {
            [0x1000] = ("TypeA", 50, 1, 10, 2, 0, 5, 1, 100)
        });

        WcfChannelAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, int, int, int, ulong)>
        {
            [0x2000] = ("TypeB", 30, 0, 5, 1, 2, 3, 0, 200)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var stats = GetTypeStats(primary);
        stats.Should().ContainKey(0x1000UL);
        stats.Should().ContainKey(0x2000UL);
        stats[0x2000].Faulted.Should().Be(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static void SeedTypeStats(
        WcfChannelAnalyzer analyzer,
        Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, ulong Bytes)> stats)
    {
        typeof(WcfChannelAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, stats);

        // Seed an empty sampler so MergePartial doesn't null-deref.
        Type samplerType = typeof(InstanceStateSampler<WcfChannelSnapshot>);
        var sampler = samplerType.GetConstructor(new[] { typeof(int), typeof(int) })!
            .Invoke(new object[] { 500, 50 });
        typeof(WcfChannelAnalyzer)
            .GetField("_sampler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, sampler);
    }

    private static Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, ulong Bytes)>
        GetTypeStats(WcfChannelAnalyzer analyzer) =>
        (Dictionary<ulong, (string, int, int, int, int, int, int, int, ulong)>)typeof(WcfChannelAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}

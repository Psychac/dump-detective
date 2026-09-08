using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class SqlCommandAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshSqlCommandAnalyzer()
    {
        SqlCommandAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<SqlCommandAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsStateChangeCounts_PerMethodTable()
    {
        SqlCommandAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("SqlCommand", 100, active: 40, disposed: 10, other: 1, bytes: 999)
        });

        SqlCommandAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("SqlCommand", 100, active: 30, disposed: 7, other: 0, bytes: 999)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var merged = GetTypeStats(primary)[0x1000];
        merged.Total.Should().Be(100); // not summed — pre-seeded TypeAggregates
        merged.Active.Should().Be(70);
        merged.Disposed.Should().Be(17);
        merged.Other.Should().Be(1);
        merged.Bytes.Should().Be(999);
    }

    [Fact]
    public void MergePartial_AddsNewKeyFromWorker_WhenNotPresentInPrimary()
    {
        SqlCommandAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("TypeA", 50, 1, 10, 2, 100)
        });

        SqlCommandAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x2000] = ("TypeB", 30, 5, 3, 0, 200)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var stats = GetTypeStats(primary);
        stats.Should().ContainKey(0x1000UL);
        stats.Should().ContainKey(0x2000UL);
        stats[0x2000].Active.Should().Be(5);
    }

    private static void SeedTypeStats(
        SqlCommandAnalyzer analyzer,
        Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)> stats)
    {
        typeof(SqlCommandAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, stats);

        var sampler = new InstanceStateSampler<SqlCommandSnapshot>();
        typeof(SqlCommandAnalyzer)
            .GetField("_sampler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, sampler);
    }

    private static Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)>
        GetTypeStats(SqlCommandAnalyzer analyzer) =>
        (Dictionary<ulong, (string, int, int, int, int, ulong)>)typeof(SqlCommandAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}

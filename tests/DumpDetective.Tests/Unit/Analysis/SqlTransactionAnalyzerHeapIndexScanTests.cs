using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class SqlTransactionAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshSqlTransactionAnalyzer()
    {
        SqlTransactionAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<SqlTransactionAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsStateChangeCounts_PerMethodTable()
    {
        SqlTransactionAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("SqlTransaction", 100, active: 4, disposed: 10, other: 1, bytes: 999)
        });

        SqlTransactionAnalyzer worker = new();
        SeedTypeStats(worker, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("SqlTransaction", 100, active: 3, disposed: 7, other: 0, bytes: 999)
        });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var merged = GetTypeStats(primary)[0x1000];
        merged.Total.Should().Be(100); // not summed — pre-seeded TypeAggregates
        merged.Active.Should().Be(7);
        merged.Disposed.Should().Be(17);
        merged.Other.Should().Be(1);
        merged.Bytes.Should().Be(999);
    }

    [Fact]
    public void MergePartial_AddsNewKeyFromWorker_WhenNotPresentInPrimary()
    {
        SqlTransactionAnalyzer primary = new();
        SeedTypeStats(primary, new Dictionary<ulong, (string, int, int, int, int, ulong)>
        {
            [0x1000] = ("TypeA", 50, 1, 10, 2, 100)
        });

        SqlTransactionAnalyzer worker = new();
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
        SqlTransactionAnalyzer analyzer,
        Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)> stats)
    {
        typeof(SqlTransactionAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, stats);

        var sampler = new InstanceStateSampler<SqlTransactionSnapshot>();
        typeof(SqlTransactionAnalyzer)
            .GetField("_sampler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(analyzer, sampler);
    }

    private static Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)>
        GetTypeStats(SqlTransactionAnalyzer analyzer) =>
        (Dictionary<ulong, (string, int, int, int, int, ulong)>)typeof(SqlTransactionAnalyzer)
            .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}

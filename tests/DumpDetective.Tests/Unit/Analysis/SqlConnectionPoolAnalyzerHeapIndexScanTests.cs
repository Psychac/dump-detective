using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class SqlConnectionPoolAnalyzerHeapIndexScanTests
{
    [Fact]
    public void IsCandidateType_MatchesBothProviderPoolTypeNames()
    {
        SqlConnectionPoolAnalyzer analyzer = new();
        ITypedResourceCandidateSource source = analyzer;

        source.IsCandidateType("System.Data.ProviderBase.DbConnectionPool").Should().BeTrue();
        source.IsCandidateType("Microsoft.Data.ProviderBase.DbConnectionPool").Should().BeTrue();
        source.IsCandidateType("Microsoft.Data.SqlClient.SqlConnection").Should().BeFalse();
        source.IsCandidateType("System.Data.ProviderBase.DbConnectionPoolGroup").Should().BeFalse();
    }

    [Fact]
    public void CreateWorkerInstance_ReturnsFreshSqlConnectionPoolAnalyzer()
    {
        SqlConnectionPoolAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<SqlConnectionPoolAnalyzer>();
    }

    [Fact]
    public void MergePartial_ConcatenatesPoolsFromAllWorkers()
    {
        SqlConnectionPoolAnalyzer primary = new();
        SeedPools(primary, [new SqlConnectionPoolSnapshot("Microsoft.Data.ProviderBase.DbConnectionPool", 0x1000, 40, 100, 0, "Server=A")]);

        SqlConnectionPoolAnalyzer worker = new();
        SeedPools(worker, [new SqlConnectionPoolSnapshot("Microsoft.Data.ProviderBase.DbConnectionPool", 0x2000, 95, 100, 0, "Server=B")]);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetPools(primary).Should().HaveCount(2);
        GetPools(primary).Should().Contain(p => p.Address == 0x1000);
        GetPools(primary).Should().Contain(p => p.Address == 0x2000);
    }

    [Theory]
    [InlineData(40, 100, 40.0)]
    [InlineData(95, 100, 95.0)]
    [InlineData(50, -1, -1.0)]
    [InlineData(50, 0, -1.0)]
    public void UtilizationPercent_ComputesExpectedRatio(int current, int max, double expected)
    {
        var pool = new SqlConnectionPoolSnapshot("Microsoft.Data.ProviderBase.DbConnectionPool", 0x1, current, max, 0, null);

        double actual = (double)typeof(SqlConnectionPoolAnalyzer)
            .GetMethod("UtilizationPercent", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pool])!;

        actual.Should().Be(expected);
    }

    private static void SeedPools(SqlConnectionPoolAnalyzer analyzer, List<SqlConnectionPoolSnapshot> pools)
    {
        var field = typeof(SqlConnectionPoolAnalyzer).GetField("_pools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<SqlConnectionPoolSnapshot>)field.GetValue(analyzer)!;
        list.AddRange(pools);
    }

    private static List<SqlConnectionPoolSnapshot> GetPools(SqlConnectionPoolAnalyzer analyzer) =>
        (List<SqlConnectionPoolSnapshot>)typeof(SqlConnectionPoolAnalyzer)
            .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}

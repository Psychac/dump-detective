using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Ported from testing <c>SqlConnectionPoolAnalyzer</c> directly to testing
/// <see cref="SqlConnectionPoolAnalyzerLegacyAdapter"/> — the Tier-2 pilot
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) moved candidate discovery
/// (<see cref="ITypedResourceCandidateSource"/>/<see cref="IParallelHeapIndexScanParticipant"/>) off
/// the inner SDK analyzer onto the adapter, since the pipeline only ever sees the registered
/// <c>Core.Abstractions.IAnalyzer</c>. Discovery now only accumulates address + type name
/// (<c>SqlConnectionPoolCandidate</c>) — full <see cref="SqlConnectionPoolSnapshot"/> construction
/// (which needs field reads) moved to <c>SqlConnectionPoolAnalyzer.AnalyzeAsync</c>.
/// </summary>
public sealed class SqlConnectionPoolAnalyzerHeapIndexScanTests
{
    [Fact]
    public void IsCandidateType_MatchesBothProviderPoolTypeNames()
    {
        SqlConnectionPoolAnalyzerLegacyAdapter adapter = new();
        ITypedResourceCandidateSource source = adapter;

        source.IsCandidateType("System.Data.ProviderBase.DbConnectionPool").Should().BeTrue();
        source.IsCandidateType("Microsoft.Data.ProviderBase.DbConnectionPool").Should().BeTrue();
        source.IsCandidateType("Microsoft.Data.SqlClient.SqlConnection").Should().BeFalse();
        source.IsCandidateType("System.Data.ProviderBase.DbConnectionPoolGroup").Should().BeFalse();
    }

    [Fact]
    public void CreateWorkerInstance_ReturnsFreshAdapter()
    {
        SqlConnectionPoolAnalyzerLegacyAdapter primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<SqlConnectionPoolAnalyzerLegacyAdapter>();
    }

    [Fact]
    public void MergePartial_ConcatenatesCandidatesFromAllWorkers()
    {
        SqlConnectionPoolAnalyzerLegacyAdapter primary = new();
        SeedCandidates(primary, [new SqlConnectionPoolCandidate(0x1000, "Microsoft.Data.ProviderBase.DbConnectionPool")]);

        SqlConnectionPoolAnalyzerLegacyAdapter worker = new();
        SeedCandidates(worker, [new SqlConnectionPoolCandidate(0x2000, "Microsoft.Data.ProviderBase.DbConnectionPool")]);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetCandidates(primary).Should().HaveCount(2);
        GetCandidates(primary).Should().Contain(c => c.Address == 0x1000);
        GetCandidates(primary).Should().Contain(c => c.Address == 0x2000);
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

    private static void SeedCandidates(SqlConnectionPoolAnalyzerLegacyAdapter adapter, List<SqlConnectionPoolCandidate> candidates)
    {
        var field = typeof(SqlConnectionPoolAnalyzerLegacyAdapter).GetField("_candidates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<SqlConnectionPoolCandidate>)field.GetValue(adapter)!;
        list.AddRange(candidates);
    }

    private static List<SqlConnectionPoolCandidate> GetCandidates(SqlConnectionPoolAnalyzerLegacyAdapter adapter) =>
        (List<SqlConnectionPoolCandidate>)typeof(SqlConnectionPoolAnalyzerLegacyAdapter)
            .GetField("_candidates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;
}

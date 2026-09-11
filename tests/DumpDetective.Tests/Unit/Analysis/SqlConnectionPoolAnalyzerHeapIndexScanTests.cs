using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Ported twice now: first from testing <c>SqlConnectionPoolAnalyzer</c> directly to testing
/// <see cref="SqlConnectionPoolAnalyzerLegacyAdapter"/> (candidate discovery moved off the inner SDK
/// analyzer, since the pipeline only ever sees the registered <c>Core.Abstractions.IAnalyzer</c>);
/// then again once discovery itself moved onto the shared
/// <see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/> base, and
/// collected candidates became <see cref="HeapObjectRef"/> (the existing Tier-1 shape) instead of a
/// bespoke <c>SqlConnectionPoolCandidate</c> record. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
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
        SeedCandidates(primary, [PoolCandidate(0x1000)]);

        SqlConnectionPoolAnalyzerLegacyAdapter worker = new();
        SeedCandidates(worker, [PoolCandidate(0x2000)]);

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

    private static HeapObjectRef PoolCandidate(ulong address) => new(
        address, SdkTypeRefFactory.FromName("Microsoft.Data.ProviderBase.DbConnectionPool", null),
        Size: 0, TypeDisplayName: "Microsoft.Data.ProviderBase.DbConnectionPool");

    // "_candidates" is declared on the shared TypedResourceCandidateStreamingLegacyAdapter<,> base,
    // not on SqlConnectionPoolAnalyzerLegacyAdapter itself -- GetField on the derived type does not
    // see a base class's private instance field, so these walk .BaseType explicitly.
    private static void SeedCandidates(SqlConnectionPoolAnalyzerLegacyAdapter adapter, List<HeapObjectRef> candidates)
    {
        var field = adapter.GetType().BaseType!.GetField("_candidates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<HeapObjectRef>)field.GetValue(adapter)!;
        list.AddRange(candidates);
    }

    private static List<HeapObjectRef> GetCandidates(SqlConnectionPoolAnalyzerLegacyAdapter adapter) =>
        (List<HeapObjectRef>)adapter.GetType().BaseType!
            .GetField("_candidates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;
}

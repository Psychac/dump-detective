using DumpDetective.Analysis.Analyzers.Infra;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Tier-2 wave retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="SqlTransactionAnalyzerLegacyAdapter.CreateWorkerInstance"/>/<c>MergePartial</c> are now
/// pure generic base-class behavior (<c>TypedResourceCandidateStreamingLegacyAdapter</c>), already
/// covered once by <c>SqlConnectionPoolAnalyzerHeapIndexScanTests</c> — re-testing the identical
/// generic logic per adapter would be pure duplication. Only <see cref="SqlTransactionAnalyzerLegacyAdapter.IsCandidateType"/>
/// is adapter-specific (its own namespace-prefix/suffix wiring), so that's what's covered here.
/// </summary>
public sealed class SqlTransactionAnalyzerHeapIndexScanTests
{
    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SqlTransaction", true)]
    [InlineData("System.Data.SqlClient.SqlTransaction", true)]
    [InlineData("Npgsql.NpgsqlTransaction", true)]
    [InlineData("Microsoft.Data.SqlClient.SqlCommand", false)]
    [InlineData("System.Data.ProviderBase.DbConnectionPool", false)]
    public void IsCandidateType_MatchesTransactionSuffixAcrossKnownProviders(string typeName, bool expected)
    {
        SqlTransactionAnalyzerLegacyAdapter adapter = new();

        adapter.IsCandidateType(typeName).Should().Be(expected);
    }
}

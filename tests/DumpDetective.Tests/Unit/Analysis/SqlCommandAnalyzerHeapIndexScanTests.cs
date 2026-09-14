using DumpDetective.Analysis.Analyzers.Infra;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Tier-2 wave retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="SqlCommandAnalyzer.CreateWorkerInstance"/>/<c>MergePartial</c> are now
/// pure generic base-class behavior (<c>TypedResourceCandidateStreaming</c>), already
/// covered once by <c>SqlConnectionPoolAnalyzerHeapIndexScanTests</c> — re-testing the identical
/// generic logic per adapter would be pure duplication. Only <see cref="SqlCommandAnalyzer.IsCandidateType"/>
/// is adapter-specific (its own namespace-prefix/suffix wiring), so that's what's covered here.
/// </summary>
public sealed class SqlCommandAnalyzerHeapIndexScanTests
{
    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SqlCommand", true)]
    [InlineData("System.Data.SqlClient.SqlCommand", true)]
    [InlineData("Npgsql.NpgsqlCommand", true)]
    [InlineData("Microsoft.Data.SqlClient.SqlConnection", false)]
    [InlineData("System.Data.ProviderBase.DbConnectionPool", false)]
    public void IsCandidateType_MatchesCommandSuffixAcrossKnownProviders(string typeName, bool expected)
    {
        SqlCommandAnalyzer adapter = new();

        adapter.IsCandidateType(typeName).Should().Be(expected);
    }
}

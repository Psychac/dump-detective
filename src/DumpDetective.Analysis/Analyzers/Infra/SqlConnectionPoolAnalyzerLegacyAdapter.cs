using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="SqlConnectionPoolAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 pilot.
/// </summary>
public sealed class SqlConnectionPoolAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<SqlConnectionPoolAnalyzerLegacyAdapter, SqlConnectionPoolAnalyzer>
{
    /// <summary>
    /// Both legacy and current SqlClient packages share the exact same pool-manager implementation
    /// (copied internal type, same field names) under
    /// <c>System.Data.ProviderBase.DbConnectionPool</c> / <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>
    /// respectively — no per-provider pool subclass, matched by namespace prefix; other providers
    /// (Npgsql, MySql, Oracle, Sqlite) use unrelated internal pooling designs, out of scope.
    /// </summary>
    private static readonly HashSet<string> PoolTypeNames = new(StringComparer.Ordinal)
    {
        "System.Data.ProviderBase.DbConnectionPool",
        "Microsoft.Data.ProviderBase.DbConnectionPool",
    };

    public SqlConnectionPoolAnalyzerLegacyAdapter() : base(new SqlConnectionPoolAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => PoolTypeNames.Contains(typeName);
}

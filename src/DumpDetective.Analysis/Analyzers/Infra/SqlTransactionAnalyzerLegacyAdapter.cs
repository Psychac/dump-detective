using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="SqlTransactionAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
public sealed class SqlTransactionAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<SqlTransactionAnalyzerLegacyAdapter, SqlTransactionAnalyzer>
{
    // Same provider namespaces as DbConnectionAnalyzer -- transaction types live alongside
    // connection types in each provider's assembly.
    private static readonly string[] TransactionNamespacePrefixes =
    [
        "System.Data.SqlClient.",
        "Microsoft.Data.SqlClient.",
        "System.Data.OleDb.",
        "System.Data.Odbc.",
        "Oracle.",
        "Npgsql.",
        "MySql.",
        "Microsoft.Data.Sqlite.",
        "System.Data.SQLite.",
    ];

    public SqlTransactionAnalyzerLegacyAdapter() : base(new SqlTransactionAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, TransactionNamespacePrefixes, "Transaction", null);
}

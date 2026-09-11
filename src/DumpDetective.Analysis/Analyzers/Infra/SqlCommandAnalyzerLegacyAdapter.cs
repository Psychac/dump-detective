using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="SqlCommandAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
public sealed class SqlCommandAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<SqlCommandAnalyzerLegacyAdapter, SqlCommandAnalyzer>
{
    // Same provider namespaces as DbConnectionAnalyzer/SqlTransactionAnalyzer.
    private static readonly string[] CommandNamespacePrefixes =
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

    public SqlCommandAnalyzerLegacyAdapter() : base(new SqlCommandAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, CommandNamespacePrefixes, "Command", null);
}

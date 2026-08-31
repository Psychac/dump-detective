using System.Text.RegularExpressions;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Shared connection-string redaction used by <c>DbConnectionAnalyzer</c> and
/// <c>SqlConnectionPoolAnalyzer</c> before a connection string read off the heap is ever
/// surfaced in a finding, report section, or trend metric.
/// </summary>
internal static class ConnectionStringAnonymiser
{
    public static string Anonymise(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        // Remove common sensitive keywords: password, pwd, user id, uid
        return Regex.Replace(
            connectionString,
            @"(?i)(password|pwd|user\s?id|uid|secret)\s*=\s*[^;]*",
            "$1=***",
            RegexOptions.IgnoreCase);
    }
}

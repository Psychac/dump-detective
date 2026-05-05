namespace DumpDetective.Core.Models;

internal sealed record ReportArtifact(
    string Analyzer,
    string FileName,
    string? Content,
    string ContentType,
    string? FilePath = null);

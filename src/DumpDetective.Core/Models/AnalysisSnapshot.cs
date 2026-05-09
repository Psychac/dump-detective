namespace DumpDetective.Core.Models;

internal sealed record AnalysisSnapshot(
int Index,
string DumpPath,
IReadOnlyList<InsightFinding> Findings,
IReadOnlyDictionary<string, AnalyzerDomainResult> DomainResults,
DateTime GeneratedAtUtc,
AnalysisIncidentContext? IncidentContext = null);

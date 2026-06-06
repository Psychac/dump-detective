namespace DumpDetective.Core.Models;

/// <summary>
/// Lightweight snapshot of the entire analysis state at a specific point in time, 
/// captured for each analyzer run and included in the final report.
/// </summary>
/// <param name="Index"></param>
/// <param name="DumpPath"></param>
/// <param name="Runs"></param>
/// <param name="Findings"></param>
/// <param name="DomainResults"></param>
/// <param name="GeneratedAtUtc"></param>
/// <param name="IncidentContext"></param>
/// TODO: Seems to only be used in trend and this shit aint lightweight.
internal sealed record AnalysisSnapshot(
	int Index,
	string DumpPath,
	// TODO: analyzer run result already seems to have DomainResult per analyzer
	IReadOnlyList<AnalyzerRunResult> Runs,
	IReadOnlyList<InsightFinding> Findings,
	IReadOnlyDictionary<string, AnalyzerDomainResult> DomainResults,
	DateTime GeneratedAtUtc,
	AnalysisIncidentContext? IncidentContext = null);

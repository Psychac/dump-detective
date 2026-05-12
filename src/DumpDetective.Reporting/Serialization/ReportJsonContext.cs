using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

[JsonSerializable(typeof(AnalysisReportDocument))]
[JsonSerializable(typeof(SingleDumpReportDocument))]
[JsonSerializable(typeof(TrendReportDocument))]
[JsonSerializable(typeof(DumpDetective.Core.Models.AnalysisIncidentContext))]
[JsonSerializable(typeof(DumpDetective.Core.Models.TrendSnapshotContext))]
[JsonSerializable(typeof(List<DumpDetective.Core.Models.TrendSnapshotContext>))]
[JsonSerializable(typeof(List<FindingRecord>))]
[JsonSerializable(typeof(List<AnalyzerDetailSection>))]
[JsonSerializable(typeof(List<AnalyzerRunStatusRecord>))]
[JsonSerializable(typeof(EvidenceRef))]
[JsonSerializable(typeof(List<EvidenceRef>))]
[JsonSerializable(typeof(List<SectionBlock>))]
[JsonSerializable(typeof(ScoreBreakdown))]
[JsonSerializable(typeof(ScoreContributor))]
[JsonSerializable(typeof(List<ScoreBreakdown>))]
[JsonSerializable(typeof(List<ScoreContributor>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ReportJsonContext : JsonSerializerContext { }

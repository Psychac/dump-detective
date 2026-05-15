using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

[JsonSerializable(typeof(AnalysisReportDocument))]
[JsonSerializable(typeof(SingleDumpReportDocument))]
[JsonSerializable(typeof(TrendReportDocument))]
[JsonSerializable(typeof(HealthScorecard))]
[JsonSerializable(typeof(DomainHealthEntry))]
[JsonSerializable(typeof(List<DomainHealthEntry>))]
[JsonSerializable(typeof(ReportDomainSection))]
[JsonSerializable(typeof(List<ReportDomainSection>))]
[JsonSerializable(typeof(ReportAppendix))]
[JsonSerializable(typeof(AnalyzerMemoryDiagnosticRecord))]
[JsonSerializable(typeof(List<AnalyzerMemoryDiagnosticRecord>))]
[JsonSerializable(typeof(DumpDetective.Core.Models.AnalysisIncidentContext))]
[JsonSerializable(typeof(DumpDetective.Core.Models.TrendSnapshotContext))]
[JsonSerializable(typeof(List<DumpDetective.Core.Models.TrendSnapshotContext>))]
[JsonSerializable(typeof(List<FindingRecord>))]
[JsonSerializable(typeof(List<AnalyzerDetailSection>))]
[JsonSerializable(typeof(List<AnalyzerRunStatusRecord>))]
[JsonSerializable(typeof(EvidenceRef))]
[JsonSerializable(typeof(List<EvidenceRef>))]
[JsonSerializable(typeof(List<SectionBlock>))]
[JsonSerializable(typeof(ConfidenceBandBlock))]
[JsonSerializable(typeof(ScoreBreakdown))]
[JsonSerializable(typeof(ScoreContributor))]
[JsonSerializable(typeof(List<ScoreBreakdown>))]
[JsonSerializable(typeof(List<ScoreContributor>))]
// Section contract-slot types
[JsonSerializable(typeof(SectionLeadFinding))]
[JsonSerializable(typeof(SectionKeyMetric))]
[JsonSerializable(typeof(SectionTable))]
[JsonSerializable(typeof(SectionProvenance))]
[JsonSerializable(typeof(List<SectionKeyMetric>))]
[JsonSerializable(typeof(List<SectionTable>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
internal sealed partial class ReportJsonContext : JsonSerializerContext { }

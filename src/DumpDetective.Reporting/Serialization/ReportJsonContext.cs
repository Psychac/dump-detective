using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

[JsonSerializable(typeof(AnalysisReportDocument))]
[JsonSerializable(typeof(SingleDumpReportDocument))]
[JsonSerializable(typeof(TrendReportDocument))]
[JsonSerializable(typeof(TrendStoryRecord))]
[JsonSerializable(typeof(HealthScorecard))]
[JsonSerializable(typeof(DomainHealthEntry))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, DomainHealthEntry>))]
[JsonSerializable(typeof(DomainSeverity))]
[JsonSerializable(typeof(List<DomainSeverity>))]
[JsonSerializable(typeof(DomainSeverityChange))]
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
[JsonSerializable(typeof(ActionPriorityFactors))]
[JsonSerializable(typeof(ActionConfidenceRecord))]
[JsonSerializable(typeof(RankedActionRecord))]
[JsonSerializable(typeof(List<RankedActionRecord>))]
[JsonSerializable(typeof(CorrelationEventRecord))]
[JsonSerializable(typeof(List<CorrelationEventRecord>))]
// Section contract-slot types
[JsonSerializable(typeof(SectionLeadFinding))]
[JsonSerializable(typeof(SectionKeyMetric))]
[JsonSerializable(typeof(MetricValue))]
[JsonSerializable(typeof(NumericMetricValue))]
[JsonSerializable(typeof(TextMetricValue))]
[JsonSerializable(typeof(EnumMetricValue))]
[JsonSerializable(typeof(CompactHeader))]
[JsonSerializable(typeof(CompactRow))]
[JsonSerializable(typeof(CompactTable))]
[JsonSerializable(typeof(List<CompactRow>))]
[JsonSerializable(typeof(List<CompactHeader>))]
[JsonSerializable(typeof(SectionProvenance))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, MetricValue>))]
[JsonSerializable(typeof(List<CompactTable>))]
[JsonSerializable(typeof(SparklineBlock))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal sealed partial class ReportJsonContext : JsonSerializerContext { }

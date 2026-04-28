using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

[JsonSerializable(typeof(AnalysisReportDocument))]
[JsonSerializable(typeof(List<FindingRecord>))]
[JsonSerializable(typeof(List<AnalyzerDetailSection>))]
[JsonSerializable(typeof(List<SectionBlock>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ReportJsonContext : JsonSerializerContext { }

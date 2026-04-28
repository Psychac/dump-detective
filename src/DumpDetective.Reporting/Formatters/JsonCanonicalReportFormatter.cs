using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

namespace DumpDetective.Reporting.Formatters;

internal sealed class JsonCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Json;

    public string Render(AnalysisReportDocument doc) =>
        JsonSerializer.Serialize(doc,
            new JsonSerializerOptions(ReportJsonContext.Default.Options) { WriteIndented = true });
}

using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Reporting.Formatters;

internal sealed class JsonCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Json;

    public string Render(AnalysisReportDocument doc) =>
        // Rely on canonical model attributes for compact JSON naming.
        JsonSerializer.Serialize(EntityCellPass.Apply(doc), new JsonSerializerOptions(ReportJsonContext.Default.Options) { WriteIndented = true });
}

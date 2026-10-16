using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Reporting.Formatters;

internal sealed class JsonCanonicalReportFormatter : IReportFormatter
{
    public ReportFormat Format => ReportFormat.Json;

    // Rely on canonical model attributes for compact JSON naming. Indented, except that table rows and observation-id lists stay on one line each.
    private static readonly JsonSerializerOptions IndentedOptions = new(ReportJsonContext.Default.Options)
    {
        WriteIndented = true,
        TypeInfoResolver = ReportJsonContext.Default.WithAddedModifier(InlineJson.InlineObservationIdLists),
    };

    private static JsonSerializerOptions Options() => IndentedOptions;

    public string Render(AnalysisReportDocument doc) =>
        JsonSerializer.Serialize(EntityCellPass.Apply(doc), Options());

    /// <summary>
    /// Writes the same JSON as <see cref="Render"/> straight to a stream. A report with millions of table rows is far past the length of
    /// the longest .NET string, so the file writer must not build the text first.
    /// </summary>
    public Task WriteAsync(AnalysisReportDocument doc, Stream output, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(output, EntityCellPass.Apply(doc), Options(), cancellationToken);
}

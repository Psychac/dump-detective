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

    /// <summary>
    /// Rely on canonical model attributes for compact JSON naming. Indented, except that table rows stay on one line each. Findings cite
    /// observations by position in a lineage table the document carries, so the options are built for the one document.
    /// </summary>
    private static (AnalysisReportDocument Document, JsonSerializerOptions Options) Prepare(AnalysisReportDocument doc)
    {
        AnalysisReportDocument rewritten = EntityCellPass.Apply(doc);
        LineageIndex lineage = LineageIndex.Build(rewritten);
        var options = new JsonSerializerOptions(ReportJsonContext.Default.Options)
        {
            WriteIndented = true,
            TypeInfoResolver = ReportJsonContext.Default.WithAddedModifier(LineageJson.ReferenceLists(lineage)),
        };

        return (lineage.IsEmpty ? rewritten : rewritten with { Lineage = lineage.Table }, options);
    }

    public string Render(AnalysisReportDocument doc)
    {
        (AnalysisReportDocument document, JsonSerializerOptions options) = Prepare(doc);
        return JsonSerializer.Serialize(document, options);
    }

    /// <summary>
    /// Writes the same JSON as <see cref="Render"/> straight to a stream. A report with millions of table rows is far past the length of
    /// the longest .NET string, so the file writer must not build the text first.
    /// </summary>
    public Task WriteAsync(AnalysisReportDocument doc, Stream output, CancellationToken cancellationToken)
    {
        (AnalysisReportDocument document, JsonSerializerOptions options) = Prepare(doc);
        return JsonSerializer.SerializeAsync(output, document, options, cancellationToken);
    }
}

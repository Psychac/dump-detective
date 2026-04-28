using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

namespace DumpDetective.Reporting.Formatters;

/// <summary>
/// Template-based HTML renderer. Replaces <see cref="HtmlCanonicalReportFormatter"/> in Phase F.
/// Requires embedded resources report.html / report.css / report.js (created in Phase E).
/// </summary>
internal sealed class HtmlReportRenderer : IReportFormatter
{
    // Loaded once at first use; reused for every subsequent Render call.
    private static readonly string _template = EmbeddedResourceLoader.LoadText("report.html");
    private static readonly string _css      = EmbeddedResourceLoader.LoadText("report.css");
    private static readonly string _js       = EmbeddedResourceLoader.LoadText("report.js");

    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        return _template
            .Replace("{{CSS}}", _css)
            .Replace("{{REPORT_JSON}}", json)
            .Replace("{{JS}}", _js);
    }
}

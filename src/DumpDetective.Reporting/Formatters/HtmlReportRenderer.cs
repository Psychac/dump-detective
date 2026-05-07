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
    public static bool ForcePreRender { get; set; } = false;

    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        // Optionally pre-render findings and analyzer sections server-side to improve
        // initial paint for large reports. Templates can include placeholders
        // {{PRE_RENDERED_FINDINGS}} and {{PRE_RENDERED_ANALYZER_SECTIONS}}.
        string preFindings = string.Empty;
        string preAnalyzers = string.Empty;
        bool shouldPreRender = ForcePreRender;
        // Auto-fallback: pre-render when JSON is large or many findings present
        if (!shouldPreRender && (json.Length > 2_000_000 || doc.Findings?.Count >= 1000)) shouldPreRender = true;
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_FINDINGS}}"))
        {
            preFindings = ReportHtmlShared.RenderFindings(doc.Findings);
        }
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_ANALYZER_SECTIONS}}"))
        {
            preAnalyzers = ReportHtmlShared.RenderAnalyzerSections(doc.AnalyzerSections);
        }

        return _template
            .Replace("{{CSS}}", _css)
            .Replace("{{REPORT_JSON}}", json)
            .Replace("{{JS}}", _js)
            .Replace("{{PRE_RENDERED_FINDINGS}}", preFindings)
            .Replace("{{PRE_RENDERED_ANALYZER_SECTIONS}}", preAnalyzers);
    }
}

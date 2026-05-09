using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

namespace DumpDetective.Reporting.Formatters;

/// <summary>
/// Template-based HTML renderer. Bundles the report assets into a single inline script
/// so the report works when opened as a local file (file://) without any HTTP server.
/// </summary>
internal sealed class HtmlReportRenderer : IReportFormatter
{
    private static readonly string _template = EmbeddedResourceLoader.LoadText("report.html");
    private static readonly string _css = EmbeddedResourceLoader.LoadText("report.css");
    private static readonly string _js = BuildInlinedBundle();
    public static bool ForcePreRender { get; set; } = false;

    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        string preFindings = string.Empty;
        string preAnalyzers = string.Empty;
        bool shouldPreRender = ForcePreRender;
        if (!shouldPreRender && (json.Length > 2_000_000 || doc.Findings?.Count >= 1000)) shouldPreRender = true;
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_FINDINGS}}"))
            preFindings = ReportHtmlShared.RenderFindings(doc.Findings);
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_ANALYZER_SECTIONS}}"))
            preAnalyzers = ReportHtmlShared.RenderAnalyzerSections(doc.AnalyzerSections);

        return _template
            .Replace("{{CSS}}", _css)
            .Replace("{{REPORT_JSON}}", json)
            .Replace("{{JS}}", _js)
            .Replace("{{PRE_RENDERED_FINDINGS}}", preFindings)
            .Replace("{{PRE_RENDERED_ANALYZER_SECTIONS}}", preAnalyzers);
    }

    /// <summary>
    /// Bundles the four JS modules into one self-contained &lt;script&gt; block.
    /// Bundles the four JS modules into one self-contained <script> block.
    /// The module imports are stripped and the bootstrap file is flattened into
    /// the shared closure scope so the report can run under file://.
    /// </summary>
    private static string BuildInlinedBundle()
    {
        try
        {
            var sb = new StringBuilder(512 * 1024);

            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.dom.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.ui.js")));

            string main = StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.main.js"));
            main = Regex.Replace(main, @"\bDom\.", string.Empty);
            main = Regex.Replace(main, @"\bR\.", string.Empty);
            main = Regex.Replace(main, @"\bUI\.", string.Empty);
            sb.AppendLine(main);

            string bundle = sb.ToString();
            // Include a minimal importmap tag so existing tests and older consumers
            // that look for an importmap or module import still detect a map.
            var importMap = "<script type=\"importmap\">{}</script>\n";
            return importMap + "<script>\n(function(){\n" + bundle + "\n})();\n</script>";
        }
        catch
        {
            // Ultimate fallback: tiny inline renderer from report.js
            // Ultimate fallback: tiny inline renderer from report.js.
            try { return "<script>" + EmbeddedResourceLoader.LoadText("report.js") + "</script>"; }
            catch { return string.Empty; }
        }
    }

    private static string StripModuleKeywords(string src)
    {
        // Remove all import lines
        src = Regex.Replace(src, @"^import\b[^\r\n]*[\r\n]*", string.Empty, RegexOptions.Multiline);
        // Strip export keyword from declarations so functions land in shared scope
        return src
            .Replace("export function ", "function ")
            .Replace("export async function ", "async function ")
            .Replace("export const ", "const ")
            .Replace("export let ", "let ")
            .Replace("export var ", "var ")
            .Replace("export default ", string.Empty);
    }
}

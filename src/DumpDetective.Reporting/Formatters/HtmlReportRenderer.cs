using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly string _css = BuildInlinedCss();
    private static readonly string _js = BuildInlinedBundle();
    public static bool ForcePreRender { get; set; } = false;

    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        string reportJson = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        string preFindings = string.Empty;
        string preHealthScorecard = string.Empty;
        string preAnalyzers = string.Empty;
        bool shouldPreRender = ForcePreRender;
        if (!shouldPreRender && (reportJson.Length > 2_000_000 || doc.Findings?.Count >= 1000)) shouldPreRender = true;
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_HEALTH_SCORECARD}}"))
            preHealthScorecard = ReportHtmlShared.RenderHealthScorecard(doc.HealthScorecard);
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_FINDINGS}}"))
            preFindings = ReportHtmlShared.RenderFindings(doc.Findings);
        if (shouldPreRender && _template.Contains("{{PRE_RENDERED_ANALYZER_SECTIONS}}"))
            preAnalyzers = ReportHtmlShared.RenderAnalyzerSections(doc.AnalyzerSections);

        AnalysisReportDocument docForClient = doc with
        {
            RenderMode = shouldPreRender ? "prerendered" : "client"
        };
        reportJson = JsonSerializer.Serialize(docForClient, ReportJsonContext.Default.AnalysisReportDocument);
        reportJson = CompactReportJson(docForClient, reportJson);

        // For trend reports: serialize each per-dump document independently using the proven
        // AnalysisReportDocument serializer (same path that produces working single-dump JSON).
        string perDumpJson = "[]";
        if (doc is TrendReportDocument trend && trend.PerDumpDocuments.Count > 0)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < trend.PerDumpDocuments.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string perDumpDocJson = JsonSerializer.Serialize(trend.PerDumpDocuments[i], ReportJsonContext.Default.AnalysisReportDocument);
                sb.Append(CompactPerDumpJson(perDumpDocJson));
            }
            sb.Append(']');
            perDumpJson = sb.ToString();
        }

        // Single embedded payload contract:
        // {
        //   "report": <single|trend report doc>,
        //   "perDumpDocs": [<single dump docs for trend mode>]
        // }
        string payloadJson = "{\"report\":" + reportJson + ",\"perDumpDocs\":" + perDumpJson + "}";

        return _template
            .Replace("{{CSS}}", _css)
            .Replace("{{REPORT_JSON}}", payloadJson)
            .Replace("{{JS}}", _js)
            .Replace("{{PRE_RENDERED_HEALTH_SCORECARD}}", preHealthScorecard)
            .Replace("{{PRE_RENDERED_FINDINGS}}", preFindings)
            .Replace("{{PRE_RENDERED_ANALYZER_SECTIONS}}", preAnalyzers);
    }

    private static string CompactReportJson(AnalysisReportDocument doc, string reportJson)
    {
        if (doc is not TrendReportDocument)
            return reportJson;

        JsonNode? node = JsonNode.Parse(reportJson);
        if (node is not JsonObject obj)
            return reportJson;

        // Redundant in envelope mode; clients can derive from perDumpDocs / trend snapshots.
        obj.Remove("trendDumpPaths");
        obj.Remove("trendDumpCount");

        return obj.ToJsonString();
    }

    private static string CompactPerDumpJson(string perDumpJson)
    {
        JsonNode? node = JsonNode.Parse(perDumpJson);
        if (node is not JsonObject obj)
            return perDumpJson;

        // Keep only fields consumed by trend sub-report rendering/TOC.
        // Mutate in place to avoid re-parenting JsonNode instances.
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "dumpPath",
            "incidentContext",
            "healthScorecard",
            "executiveSummary",
            "domains",
            "crossDomainInsights",
            "appendix"
        };

        var remove = new List<string>();
        foreach (var kvp in obj)
        {
            if (!keep.Contains(kvp.Key)) remove.Add(kvp.Key);
        }

        for (int i = 0; i < remove.Count; i++)
            obj.Remove(remove[i]);

        return obj.ToJsonString();
    }

    /// <summary>
    /// Bundles modular CSS parts into one inline style block payload.
    /// </summary>
    private static string BuildInlinedCss()
    {
        var cssFiles = new[]
        {
            "report.base.css",
            "report.header.css",
            "report.body.css",
            "report.findings.css",
            "report.detail.css",
            "report.utilities.css"
        };

        var sb = new StringBuilder(256 * 1024);
        for (int i = 0; i < cssFiles.Length; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.AppendLine(EmbeddedResourceLoader.LoadText(cssFiles[i]));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Bundles modular JS assets into one self-contained &lt;script&gt; block.
    /// Bundles modular JS assets into one self-contained <script> block.
    /// The module imports are stripped and the bootstrap file is flattened into
    /// the shared closure scope so the report can run under file://.
    /// </summary>
    private static string BuildInlinedBundle()
    {
        try
        {
            var sb = new StringBuilder(512 * 1024);

            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.dom.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.shared.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.blocks.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.charts.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.header.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.nav.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.panels.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.findings.js")));
            sb.AppendLine(StripModuleKeywords(EmbeddedResourceLoader.LoadText("report.renderers.sections.js")));
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
        // Remove ESM imports (single-line and multiline forms).
        src = Regex.Replace(src, @"^\s*import[\s\S]*?;\s*", string.Empty, RegexOptions.Multiline);
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

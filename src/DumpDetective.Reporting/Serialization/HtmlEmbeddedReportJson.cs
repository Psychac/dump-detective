using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// How the HTML report embeds its document. Observation lineage and the observations side-car reference
/// exist for tools reading <c>report.json</c>; the HTML script never reads them, and the lineage GUIDs alone
/// more than doubled a real report's size, so they are left out of the embedded copy.
/// </summary>
internal static class HtmlEmbeddedReportJson
{
    private static readonly HashSet<string> LineagePropertyNames = new(StringComparer.Ordinal) { "derivedFrom", "confidenceBreakdown", "rule", "assertion", "support", "counter", "falsifiedBy" };
    private const string ObservationsReferencePropertyName = "observations";

    private static readonly JsonSerializerOptions Options = new(ReportJsonContext.Default.Options)
    {
        TypeInfoResolver = ReportJsonContext.Default.WithAddedModifier(OmitLineage),
    };

    public static JsonTypeInfo<AnalysisReportDocument> DocumentTypeInfo { get; } =
        (JsonTypeInfo<AnalysisReportDocument>)Options.GetTypeInfo(typeof(AnalysisReportDocument));

    private static void OmitLineage(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            string name = typeInfo.Properties[i].Name;
            bool omit = typeInfo.Type == typeof(FindingRecord)
                ? LineagePropertyNames.Contains(name)
                : typeof(AnalysisReportDocument).IsAssignableFrom(typeInfo.Type) && name == ObservationsReferencePropertyName;
            if (omit)
                typeInfo.Properties.RemoveAt(i);
        }
    }
}

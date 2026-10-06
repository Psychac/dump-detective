using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// How the HTML report embeds its document. Observation lineage, the raw structured claim fields and the
/// observations side-car reference exist for tools reading <c>report.json</c>; the lineage GUIDs alone more
/// than doubled a real report's size, so they are left out of the embedded copy. Each finding carries a slim
/// <c>claim</c> instead — generated wording and counts, no ids — which is what the claim card renders.
/// </summary>
internal static class HtmlEmbeddedReportJson
{
    private static readonly HashSet<string> LineagePropertyNames = new(StringComparer.Ordinal)
    {
        "derivedFrom", "confidenceBreakdown", "rule", "assertion", "support", "counter", "falsifiedBy", "evidenceSources",
    };

    private const string ObservationsReferencePropertyName = "observations";
    private const string ClaimViewPropertyName = "claim";

    private static readonly JsonSerializerOptions Options = new(ReportJsonContext.Default.Options)
    {
        TypeInfoResolver = ReportJsonContext.Default.WithAddedModifier(ShapeForHtml),
    };

    public static JsonTypeInfo<AnalysisReportDocument> DocumentTypeInfo { get; } =
        (JsonTypeInfo<AnalysisReportDocument>)Options.GetTypeInfo(typeof(AnalysisReportDocument));

    private static void ShapeForHtml(JsonTypeInfo typeInfo)
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

        if (typeInfo.Type == typeof(FindingRecord))
            typeInfo.Properties.Add(CreateClaimViewProperty(typeInfo));
    }

    private static JsonPropertyInfo CreateClaimViewProperty(JsonTypeInfo findingRecordTypeInfo)
    {
        JsonPropertyInfo property = findingRecordTypeInfo.CreateJsonPropertyInfo(typeof(ClaimView), ClaimViewPropertyName);
        property.Get = static finding => ClaimProse.Build((FindingRecord)finding);
        property.ShouldSerialize = static (_, view) => view is not null;
        return property;
    }
}

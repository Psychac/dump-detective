using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>The embedded <c>&lt;script&gt;</c> elements for a segmented report: the manifest first, then one element per segment.</summary>
internal sealed record EncodedSegmentSet(string ManifestElement, string SegmentElements);

/// <summary>
/// Turns <see cref="ReportSegment"/>s into the HTML elements the viewer reads (docs/ReportStructure/m4-payload-plan.md step 5; ReportSystemVision § 14.1, § 14.3).
/// A segment at or below <see cref="CompressionThresholdBytes"/> stays plain JSON, so a small report is still greppable; a larger one is gzip then base64 and its
/// element says so in <c>data-encoding</c>. The <c>dd-manifest</c> element lists every segment's id, kind, encoding and sizes, so the viewer can announce a large
/// inflate before it starts. Output depends only on the input segments, so the same report always encodes to the same bytes.
/// </summary>
internal static class ReportSegmentEncoder
{
    // Same threshold the single-blob embedding used, so a segment is compressed under the same rule the whole payload was.
    public const int CompressionThresholdBytes = 200_000;

    public const string ManifestElementId = "dd-manifest";
    public const string SegmentElementIdPrefix = "dd-seg-";
    public const string JsonEncoding = "json";
    public const string GzipBase64Encoding = "gzip-base64";

    public static EncodedSegmentSet Encode(IReadOnlyList<ReportSegment> segments)
    {
        var elements = new StringBuilder();
        var manifest = new List<ManifestEntry>(segments.Count);

        // One segment at a time, appended straight to the output: no encoded copy of a segment outlives its own iteration.
        foreach (ReportSegment segment in segments)
        {
            int rawBytes = Encoding.UTF8.GetByteCount(segment.Json);
            bool compress = rawBytes > CompressionThresholdBytes;
            string encoding = compress ? GzipBase64Encoding : JsonEncoding;
            string body = compress ? GzipToBase64(segment.Json) : EscapeForScriptElement(segment.Json);

            AppendElement(elements, SegmentElementIdPrefix + segment.Id, encoding, body);
            manifest.Add(new ManifestEntry(segment.Id, KindName(segment.Kind), encoding, rawBytes, body.Length));
        }

        var manifestElement = new StringBuilder();
        AppendElement(manifestElement, ManifestElementId, JsonEncoding, EscapeForScriptElement(ManifestJson(manifest)));
        return new EncodedSegmentSet(manifestElement.ToString(), elements.ToString());
    }

    private readonly record struct ManifestEntry(string Id, string Kind, string Encoding, int Bytes, int EncodedBytes);

    private static void AppendElement(StringBuilder output, string elementId, string encoding, string body) =>
        output.Append("<script id=\"").Append(WebUtility.HtmlEncode(elementId))
            .Append("\" type=\"application/json\" data-encoding=\"").Append(encoding).Append("\">")
            .Append(body).Append("</script>\n");

    // JSON never has a '<' outside a string, and inside one < means the same character, so this cannot change what the JSON says
    // while ensuring no "</script" or "<!--" inside a string can end or confuse the element.
    private static string EscapeForScriptElement(string json) => json.Contains('<') ? json.Replace("<", "\\u003c", StringComparison.Ordinal) : json;

    private static string GzipToBase64(string text)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);

        return Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);
    }

    private static string KindName(ReportSegmentKind kind) => kind switch
    {
        ReportSegmentKind.Section => "section",
        ReportSegmentKind.Table => "table",
        ReportSegmentKind.Entities => "entities",
        ReportSegmentKind.EntityDossier => "dossier",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown segment kind."),
    };

    private static string ManifestJson(List<ManifestEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (ManifestEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id);
                writer.WriteString("kind", entry.Kind);
                writer.WriteString("encoding", entry.Encoding);
                writer.WriteNumber("bytes", entry.Bytes);
                writer.WriteNumber("encodedBytes", entry.EncodedBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}

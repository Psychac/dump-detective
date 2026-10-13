using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ReportSegmentEncoderTests
{
    private static readonly Regex ElementPattern = new(
        "<script id=\"(?<id>[^\"]*)\" type=\"application/json\" data-encoding=\"(?<encoding>[^\"]*)\">(?<body>.*?)</script>",
        RegexOptions.Singleline);

    private static string LargeJson() => "[\"" + new string('x', ReportSegmentEncoder.CompressionThresholdBytes + 10) + "\"]";

    private static Match ElementFor(string html, string elementId) =>
        ElementPattern.Matches(html).Single(match => match.Groups["id"].Value == WebUtility.HtmlEncode(elementId));

    private static string Inflate(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonElement ManifestOf(EncodedSegmentSet set) =>
        JsonDocument.Parse(ElementFor(set.ManifestElement, ReportSegmentEncoder.ManifestElementId).Groups["body"].Value).RootElement;

    [Fact]
    public void Encode_SmallSegment_StaysPlainJson()
    {
        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("s-A1", ReportSegmentKind.Section, "{\"a\":1}")]);

        Match element = ElementFor(set.SegmentElements, "dd-seg-s-A1");
        element.Groups["encoding"].Value.Should().Be("json");
        element.Groups["body"].Value.Should().Be("{\"a\":1}");
    }

    [Fact]
    public void Encode_SegmentOverTheThreshold_IsGzipBase64AndInflatesToTheOriginal()
    {
        string json = LargeJson();

        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("t-A1-0", ReportSegmentKind.Table, json)]);

        Match element = ElementFor(set.SegmentElements, "dd-seg-t-A1-0");
        element.Groups["encoding"].Value.Should().Be("gzip-base64");
        Inflate(element.Groups["body"].Value).Should().Be(json);
    }

    [Fact]
    public void Encode_SegmentExactlyAtTheThreshold_StaysPlain()
    {
        string json = "\"" + new string('x', ReportSegmentEncoder.CompressionThresholdBytes - 2) + "\"";

        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("s-A1", ReportSegmentKind.Section, json)]);

        ElementFor(set.SegmentElements, "dd-seg-s-A1").Groups["encoding"].Value.Should().Be("json");
    }

    [Fact]
    public void Encode_ScriptEndTagInsideAString_CannotEndTheElementAndStillParsesToTheSameText()
    {
        const string text = "</script><!-- <b>";
        string json = JsonSerializer.Serialize(new[] { text });

        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("s-A1", ReportSegmentKind.Section, json)]);

        set.SegmentElements.Should().NotContain("</script><!--");
        ElementPattern.Matches(set.SegmentElements).Should().ContainSingle();
        string body = ElementFor(set.SegmentElements, "dd-seg-s-A1").Groups["body"].Value;
        JsonSerializer.Deserialize<string[]>(body)!.Single().Should().Be(text);
    }

    [Fact]
    public void Encode_Manifest_ListsEverySegmentWithItsKindEncodingAndSizes()
    {
        string large = LargeJson();
        EncodedSegmentSet set = ReportSegmentEncoder.Encode(
        [
            new ReportSegment("s-A1", ReportSegmentKind.Section, "{\"a\":1}"),
            new ReportSegment("t-A1-0", ReportSegmentKind.Table, large),
            new ReportSegment(ReportSegmenter.EntitiesSegmentId, ReportSegmentKind.Entities, "[]"),
            new ReportSegment(ReportSegmenter.EntityDossierSegmentId, ReportSegmentKind.EntityDossier, "{}"),
        ]);

        JsonElement manifest = ManifestOf(set);

        manifest.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).Should().Equal("s-A1", "t-A1-0", "e-entities", "e-dossier");
        manifest.EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()).Should().Equal("section", "table", "entities", "dossier");
        manifest[0].GetProperty("encoding").GetString().Should().Be("json");
        manifest[0].GetProperty("bytes").GetInt32().Should().Be(7);
        manifest[0].GetProperty("encodedBytes").GetInt32().Should().Be(7);
        manifest[1].GetProperty("encoding").GetString().Should().Be("gzip-base64");
        manifest[1].GetProperty("bytes").GetInt32().Should().Be(Encoding.UTF8.GetByteCount(large));
        manifest[1].GetProperty("encodedBytes").GetInt32().Should().Be(ElementFor(set.SegmentElements, "dd-seg-t-A1-0").Groups["body"].Length);
    }

    [Fact]
    public void Encode_ManifestBytes_CountUtf8BytesNotCharacters()
    {
        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("s-A1", ReportSegmentKind.Section, "[\"é—\"]")]);

        ManifestOf(set)[0].GetProperty("bytes").GetInt32().Should().Be(Encoding.UTF8.GetByteCount("[\"é—\"]"));
    }

    [Fact]
    public void Encode_SegmentIdWithMarkup_IsEscapedInTheElementIdButKeptInTheManifest()
    {
        EncodedSegmentSet set = ReportSegmentEncoder.Encode([new ReportSegment("s-\"><x", ReportSegmentKind.Section, "{}")]);

        set.SegmentElements.Should().Contain("id=\"dd-seg-s-&quot;&gt;&lt;x\"");
        ManifestOf(set)[0].GetProperty("id").GetString().Should().Be("s-\"><x");
    }

    [Fact]
    public void Encode_NoSegments_StillProducesAnEmptyManifest()
    {
        EncodedSegmentSet set = ReportSegmentEncoder.Encode([]);

        set.SegmentElements.Should().BeEmpty();
        ManifestOf(set).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Encode_SameSegmentsTwice_ProducesIdenticalOutput()
    {
        ReportSegment[] segments =
        [
            new("s-A1", ReportSegmentKind.Section, "{\"a\":1}"),
            new("t-A1-0", ReportSegmentKind.Table, LargeJson()),
        ];

        EncodedSegmentSet first = ReportSegmentEncoder.Encode(segments);
        EncodedSegmentSet second = ReportSegmentEncoder.Encode(segments);

        second.Should().Be(first);
    }

    [Fact]
    public void Encode_SegmentsFromTheSegmenter_RoundTripThroughTheElements()
    {
        var table = new CompactTable(
            "Large",
            [new CompactHeader("Size", "bytes")],
            Enumerable.Range(0, ReportSegmenter.LazyTableRowThreshold + 5).Select(row => new CompactRow([(long)row])).ToArray());
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [], "A1") { CompactTables = [table] };
        var document = new SingleDumpReportDocument { Domains = [new ReportDomainSection("Memory", null, [section], [])] };
        SegmentedReport segmented = ReportSegmenter.Segment(document);

        EncodedSegmentSet set = ReportSegmentEncoder.Encode(segmented.Segments);

        foreach (ReportSegment segment in segmented.Segments)
        {
            Match element = ElementFor(set.SegmentElements, "dd-seg-" + segment.Id);
            string body = element.Groups["body"].Value;
            string decoded = element.Groups["encoding"].Value == "gzip-base64" ? Inflate(body) : body;
            JsonDocument.Parse(decoded).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }
}

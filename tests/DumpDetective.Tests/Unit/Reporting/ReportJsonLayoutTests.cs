using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ReportJsonLayoutTests
{
    private static readonly ObservationId[] Ids =
        Enumerable.Range(0, 50).Select(i => new ObservationId(new Guid($"00000000-0000-0000-0000-{i:D12}"))).ToArray();

    private static SingleDumpReportDocument Document()
    {
        var rows = new List<CompactRow>();
        for (int i = 0; i < 25; i++)
        {
            rows.Add(new CompactRow(
            [
                $"0x{0x1000 + i:X}",
                i % 2 == 0 ? "Type \"quoted\" é ü <b>" : "plain",
                (long)(i * 1000),
                i * 0.123456789,
                new UnknownCell(UnknownReason.NotApplicable),
                new UnknownCell(UnknownReason.Ambiguous),
                new EntityCell(i, i % 3 == 0 ? "Exact.Name" : null),
                null,
                true,
            ]));
        }

        var table = new CompactTable(
            "Sample table",
            [new CompactHeader("A"), new CompactHeader("B"), new CompactHeader("C", "number"), new CompactHeader("D", "number"), new CompactHeader("E"),
             new CompactHeader("F"), new CompactHeader("G"), new CompactHeader("H"), new CompactHeader("I")],
            rows);
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [new TextBlock("narrative")], "A1") { CompactTables = [table] };

        var finding = new FindingRecord("f1", "Analyzer", "Cat", "Critical", "A finding", ["detail"], "Fix it", ["tag"])
        {
            DerivedFrom = Ids,
            Support = [new EvidenceStatement(Ids, "because")],
            Counter = new Counter { Statements = [new EvidenceStatement(Ids.Take(3).ToArray(), "but")] },
        };

        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/layout.dmp",
            Domains = [new ReportDomainSection("Memory", null, [section], [finding])],
        };
    }

    private static string Indented(AnalysisReportDocument document) => new JsonCanonicalReportFormatter().Render(document);

    // The same document with the formatter's own options but no indentation: what the file would hold if it were written compact.
    private static string Compact(AnalysisReportDocument document) =>
        JsonSerializer.Serialize(
            DumpDetective.Reporting.Services.EntityCellPass.Apply(document),
            new JsonSerializerOptions(ReportJsonContext.Default.Options) { WriteIndented = false });

    private static string Normalized(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void IndentedOutput_IsTheSameDocumentAsTheCompactOutput()
    {
        SingleDumpReportDocument document = Document();

        Normalized(Indented(document)).Should().Be(Normalized(Compact(document)));
    }

    [Fact]
    public void EachTableRowIsOneLine()
    {
        string[] lines = Indented(Document()).Split('\n');
        int rowsKey = Array.FindIndex(lines, line => line.TrimStart().StartsWith("\"rows\": [", StringComparison.Ordinal));

        rowsKey.Should().BeGreaterThan(0);
        for (int i = 1; i <= 25; i++)
        {
            string row = lines[rowsKey + i].Trim().TrimEnd(',', '\r');
            row.Should().StartWith("[").And.EndWith("]");
            JsonDocument.Parse(row).RootElement.GetArrayLength().Should().Be(9);
        }
    }

    [Fact]
    public void UnknownCellsAreWrittenInline()
    {
        string json = Indented(Document());

        json.Should().Contain("{\"u\":\"na\"}").And.Contain("{\"u\":\"amb\"}");
        json.Should().NotContain("\"u\": \"na\"");
    }

    [Fact]
    public void EveryObservationIdListIsOneLine()
    {
        string json = Indented(Document());

        Regex.Matches(json, "\"derivedFrom\": \\[(\"[0-9a-f]{32}\",?){50}\\]").Should().HaveCount(1);
        Regex.Matches(json, "\"observations\": \\[(\"[0-9a-f]{32}\",?){50}\\]").Should().HaveCount(1);
        Regex.Matches(json, "\"observations\": \\[(\"[0-9a-f]{32}\",?){3}\\]").Should().HaveCount(1);
    }

    [Fact]
    public void TheStructureAroundThemStaysIndented()
    {
        string json = Indented(Document());

        json.Should().Contain("\n  \"domains\": [");
        json.Should().Contain("\n      \"sections\": [");
        json.Should().Contain("\n              \"headers\": [");
    }

    [Fact]
    public void SpecialCharactersInACellAreEscapedTheSameWayAsBefore()
    {
        string json = Indented(Document());

        JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement firstRow = parsed.RootElement.GetProperty("domains")[0].GetProperty("sections")[0]
            .GetProperty("compactTables")[0].GetProperty("rows")[0];
        firstRow[1].GetString().Should().Be("Type \"quoted\" é ü <b>");
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        SingleDumpReportDocument document = Document();

        Indented(document).Should().Be(Indented(document));
    }

    [Fact]
    public async Task StreamedOutputIsTheSameBytesAsTheStringPath()
    {
        SingleDumpReportDocument document = Document();
        using var stream = new MemoryStream();

        await new JsonCanonicalReportFormatter().WriteAsync(document, stream, CancellationToken.None);

        stream.ToArray().Should().Equal(Encoding.UTF8.GetBytes(Indented(document)));
    }

    [Fact]
    public void AnEmptyAndANullIdListStillSerialize()
    {
        var finding = new FindingRecord("f", "A", "C", "Info", "T", null, "R", [])
        {
            DerivedFrom = [],
            Support = [new EvidenceStatement([], "none")],
        };
        var document = new SingleDumpReportDocument { DumpPath = "x", Domains = [new ReportDomainSection("Memory", null, [], [finding])] };

        string json = Indented(document);

        json.Should().Contain("\"derivedFrom\": []");
        Normalized(json).Should().Be(Normalized(Compact(document)));
    }

    [Fact]
    public void AnObservationIdListReadsBackWhatItWrote()
    {
        var options = new JsonSerializerOptions(ReportJsonContext.Default.Options) { WriteIndented = true };
        EvidenceStatement statement = new(Ids.Take(5).ToArray(), "why");

        string json = JsonSerializer.Serialize(statement, options);
        EvidenceStatement? back = JsonSerializer.Deserialize<EvidenceStatement>(json, options);

        back!.Observations.Should().Equal(statement.Observations);
    }
}

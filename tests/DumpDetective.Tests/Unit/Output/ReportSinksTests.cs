using DumpDetective.Cli.Output;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class ReportSinksTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dd-sinks-{Guid.NewGuid():N}");
    private readonly ReportBuilderFacade _facade;

    public ReportSinksTests()
    {
        Directory.CreateDirectory(_tempDir);
        _facade = new ReportBuilderFacade(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlReportRenderer(),
            new JsonCanonicalReportFormatter()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer(new ReportSerializer()));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Theory]
    [InlineData("Text", "report.txt")]
    [InlineData("Markdown", "report.md")]
    [InlineData("Html", "report.html")]
    [InlineData("Json", "analysis.json")]
    public async Task WriteAsync_WritesPrimaryOutputAndReportJson_ForEveryFormat(string formatName, string outputFileName)
    {
        ReportFormat format = Enum.Parse<ReportFormat>(formatName);
        string outputPath = Path.Combine(_tempDir, outputFileName);
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/sink-test.dmp", [], TimeSpan.FromSeconds(1));
        var resolved = ResolvedExecutionOptionsFactory.Create(outputPath) with { Report = new ReportOptions { Format = format } };
        var writer = new ReportOutputWriter([new FileReportSink(_facade), new ReportJsonSink(_facade)]);

        await writer.WriteAsync(resolved, document, observations: [], artifacts: null, CancellationToken.None);

        File.ReadAllText(outputPath).Should().Be(_facade.RenderDocument(document, format, new HtmlRenderSettings(resolved.Report.StyleVersion)));
        File.ReadAllText(Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(outputFileName) + ReportJsonSink.Suffix)).Should().Be(_facade.RenderDocument(document, ReportFormat.Json));
    }

    private async Task<(AnalysisReportDocument Document, string JsonPath)> WriteReportJson(string outputName)
    {
        string outputPath = Path.Combine(_tempDir, outputName);
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/sink-test.dmp", [], TimeSpan.FromSeconds(1));
        var resolved = ResolvedExecutionOptionsFactory.Create(outputPath) with { Report = new ReportOptions { Format = ReportFormat.Html } };

        await new ReportJsonSink(_facade).PublishAsync(resolved, document, CancellationToken.None);

        return (document, Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(outputName) + ReportJsonSink.Suffix));
    }

    [Fact]
    public async Task ReportJsonSink_StreamedFile_IsByteForByteTheTextTheStringPathProduces()
    {
        (AnalysisReportDocument document, string jsonPath) = await WriteReportJson("streamed.html");

        File.ReadAllBytes(jsonPath).Should().Equal(System.Text.Encoding.UTF8.GetBytes(_facade.RenderDocument(document, ReportFormat.Json)));
    }

    [Fact]
    public async Task ReportJsonSink_Success_LeavesNoPartialFileBehind()
    {
        (_, string jsonPath) = await WriteReportJson("clean.html");

        File.Exists(jsonPath).Should().BeTrue();
        File.Exists(jsonPath + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task ReportJsonSink_ReplacesAnExistingReport()
    {
        (_, string jsonPath) = await WriteReportJson("again.html");
        File.WriteAllText(jsonPath, "stale");

        (AnalysisReportDocument document, _) = await WriteReportJson("again.html");

        File.ReadAllText(jsonPath).Should().Be(_facade.RenderDocument(document, ReportFormat.Json));
    }
}

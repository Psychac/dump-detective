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

        await writer.WriteAsync(resolved, document, artifacts: null, CancellationToken.None);

        File.ReadAllText(outputPath).Should().Be(_facade.RenderDocument(document, format, new HtmlRenderSettings(false, resolved.Report.StyleVersion)));
        File.ReadAllText(Path.Combine(_tempDir, ReportJsonSink.FileName)).Should().Be(_facade.RenderDocument(document, ReportFormat.Json));
    }

    [Fact]
    public async Task WriteAsync_PrimaryOutputIsReportJson_WritesItOnce()
    {
        string outputPath = Path.Combine(_tempDir, ReportJsonSink.FileName);
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/sink-test.dmp", [], TimeSpan.FromSeconds(1));
        var resolved = ResolvedExecutionOptionsFactory.Create(outputPath) with { Report = new ReportOptions { Format = ReportFormat.Json } };
        var writer = new ReportOutputWriter([new FileReportSink(_facade), new ReportJsonSink(_facade)]);

        await writer.WriteAsync(resolved, document, artifacts: null, CancellationToken.None);

        File.ReadAllText(outputPath).Should().Be(_facade.RenderDocument(document, ReportFormat.Json));
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DumpDetective.Cli.Output;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class ObservationsSideCarTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dd-sidecar-{Guid.NewGuid():N}");
    private readonly ReportBuilderFacade _facade;

    public ObservationsSideCarTests()
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

    [Fact]
    public async Task WriteAsync_Gzipped_WritesSiblingFileWhoseHashAndCountMatchTheReference()
    {
        string outputPath = Path.Combine(_tempDir, "Crash.html");
        Observation[] observations = [MakeObservation(), MakeObservation()];

        ObservationsReference reference = await ObservationsSideCarWriter.WriteAsync(outputPath, observations, compressed: true, CancellationToken.None);

        string sideCarPath = Path.Combine(_tempDir, "Crash.observations.json.gz");
        reference.File.Should().Be("Crash.observations.json.gz");
        reference.Count.Should().Be(2);
        reference.Compressed.Should().BeTrue();
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sideCarPath))).Should().Be(reference.Sha256);

        using var gzip = new GZipStream(File.OpenRead(sideCarPath), CompressionMode.Decompress);
        using JsonDocument parsed = JsonDocument.Parse(gzip);
        parsed.RootElement.GetArrayLength().Should().Be(2);
        parsed.RootElement[0].GetProperty("id").GetString().Should().Be(observations[0].Id.ToString());
    }

    [Fact]
    public async Task WriteAsync_Uncompressed_WritesPlainJsonSibling()
    {
        string outputPath = Path.Combine(_tempDir, "Crash.html");

        ObservationsReference reference = await ObservationsSideCarWriter.WriteAsync(outputPath, [MakeObservation()], compressed: false, CancellationToken.None);

        reference.File.Should().Be("Crash.observations.json");
        reference.Compressed.Should().BeFalse();
        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, reference.File)));
        parsed.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task OutputWriter_ReferencesSideCarFromReportJson_AndSkipsItWhenNoObservations()
    {
        string outputPath = Path.Combine(_tempDir, "Crash.html");
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/Crash.dmp", [], TimeSpan.FromSeconds(1));
        var resolved = ResolvedExecutionOptionsFactory.Create(outputPath) with { Report = new ReportOptions { Format = ReportFormat.Html } };
        var writer = new ReportOutputWriter([new FileReportSink(_facade), new ReportJsonSink(_facade)]);

        await writer.WriteAsync(resolved, document, [MakeObservation()], artifacts: null, CancellationToken.None);

        using JsonDocument reportJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "Crash.report.json")));
        JsonElement reference = reportJson.RootElement.GetProperty("observations");
        reference.GetProperty("file").GetString().Should().Be("Crash.observations.json.gz");
        reference.GetProperty("count").GetInt64().Should().Be(1);

        string otherOutput = Path.Combine(_tempDir, "Empty.html");
        await writer.WriteAsync(ResolvedExecutionOptionsFactory.Create(otherOutput), document, [], artifacts: null, CancellationToken.None);

        File.Exists(Path.Combine(_tempDir, "Empty.observations.json.gz")).Should().BeFalse();
        using JsonDocument emptyReportJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "Empty.report.json")));
        emptyReportJson.RootElement.TryGetProperty("observations", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildReportDocument_AttachesLineageAndBreakdownToTheMatchingFinding()
    {
        var finding = new InsightFinding("Stub Analyzer", "Leak", DumpDetective.Core.Enums.FindingSeverity.Warning, "title", "evidence", "rec", [], Fingerprint: "stub-fingerprint");
        var run = new AnalyzerRunResult("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null)
        {
            Findings = [finding],
        };
        ObservationId derivedFrom = ObservationId.NewId();
        ConfidenceBreakdown breakdown = new()
        {
            Composite = 0.6,
            EvidenceStrength = 0.6,
            IdentityFidelityCap = 1.0,
            TemporalAlignmentCap = 1.0,
            CapabilityFidelity = 1.0,
            ConflictPenalty = 1.0,
            LimitingFactors = ["sampled"],
        };
        var lineage = new Dictionary<string, FindingLineage> { ["stub-fingerprint"] = new([derivedFrom], breakdown) };

        AnalysisReportDocument document = _facade.BuildReportDocument(
            "C:/dumps/Crash.dmp", [run], TimeSpan.FromSeconds(1), lineageByFingerprint: lineage);

        FindingRecord record = document.Findings.Should().ContainSingle().Which;
        record.DerivedFrom.Should().Equal(derivedFrom);
        record.ConfidenceBreakdown.Should().BeEquivalentTo(breakdown);
    }

    [Fact]
    public void Render_LineageAppearsInReportJsonButNotInEmbeddedHtml()
    {
        ObservationId derivedFrom = ObservationId.NewId();
        var record = new FindingRecord("f1", "Stub Analyzer", "Leak", "Warning", "title", ["evidence"], "rec", [])
        {
            DerivedFrom = [derivedFrom],
            ConfidenceBreakdown = ConfidenceBreakdown.MeasurementCertain,
        };
        AnalysisReportDocument document = new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            CrossDomainInsights = [record],
            Observations = new ObservationsReference("Crash.observations.json.gz", "abc", 1, Compressed: true),
        };

        string json = _facade.RenderDocument(document, ReportFormat.Json);
        string html = _facade.RenderDocument(document, ReportFormat.Html);

        json.Should().Contain(derivedFrom.ToString()).And.Contain("\"confidenceBreakdown\"").And.Contain("Crash.observations.json.gz");
        html.Should().Contain("\"title\":\"title\"");
        html.Should().NotContain(derivedFrom.ToString()).And.NotContain("confidenceBreakdown").And.NotContain("Crash.observations.json.gz");
    }

    [Fact]
    public void StructuredClaimFields_ReachReportJson_AndTheEmbeddedHtmlGetsOnlyTheSlimClaimView()
    {
        ObservationId cited = ObservationId.NewId();
        var finding = new InsightFinding("Stub Analyzer", "Leak", DumpDetective.Core.Enums.FindingSeverity.Warning, "title", "evidence", "rec", [], Fingerprint: "stub-fingerprint");
        var run = new AnalyzerRunResult("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null)
        {
            Findings = [finding],
        };
        var claim = new Claim
        {
            Fingerprint = "stub-fingerprint",
            Severity = Severity.Warning,
            Title = "title",
            Narrative = "evidence",
            Recommendation = "rec",
            DerivedFrom = [cited],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Analyzer = "Stub Analyzer",
            Category = "Leak",
            Assertion = new Assertion { Predicate = AssertionPredicate.Fragments },
            Support = [new EvidenceStatement([cited], "STRUCTURED_SUPPORT_WHY")],
            Counter = Counter.None("STRUCTURED_COUNTER_REASON"),
            FalsifiedBy = [new FalsificationTest { Kind = FalsificationKind.Windbg, Test = "STRUCTURED_TEST", Command = "!cmd" }],
        };
        var lineage = new Dictionary<string, FindingLineage> { ["stub-fingerprint"] = FindingLineage.From(claim) };

        AnalysisReportDocument built = _facade.BuildReportDocument(
            "C:/dumps/Crash.dmp", [run], TimeSpan.FromSeconds(1), lineageByFingerprint: lineage);
        FindingRecord record = built.Findings.Should().ContainSingle().Which;
        AnalysisReportDocument document = new SingleDumpReportDocument { DumpPath = "C:/dumps/Crash.dmp", CrossDomainInsights = [record] };

        string json = _facade.RenderDocument(document, ReportFormat.Json);
        string html = _facade.RenderDocument(document, ReportFormat.Html);

        json.Should().Contain("\"predicate\": \"Fragments\"").And.Contain("STRUCTURED_SUPPORT_WHY")
            .And.Contain("STRUCTURED_COUNTER_REASON").And.Contain("STRUCTURED_TEST").And.Contain("\"kind\": \"Windbg\"");
        html.Should().Contain("\"claim\":").And.Contain("STRUCTURED_SUPPORT_WHY").And.Contain("STRUCTURED_COUNTER_REASON").And.Contain("STRUCTURED_TEST");
        html.Should().NotContain(cited.ToString()).And.NotContain("derivedFrom").And.NotContain("evidenceSources");
    }

    private static Observation MakeObservation() => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "stub.type",
        Subjects = [new TypeRef { CanonicalName = "System.Object", Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>(),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "Stub Analyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };
}

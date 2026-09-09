using System.Text.Json;

using DumpDetective.Cli.Output;
using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class TraceReportWriterTests : IDisposable
{
    private readonly List<string> _writtenFiles = [];

    public void Dispose()
    {
        foreach (string path in _writtenFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ResolveOutputPath_NoRequestedPath_DefaultsNextToTraceFile()
    {
        string resolved = TraceReportWriter.ResolveOutputPath(@"D:\Traces\capture.etl", requestedOutputPath: null);

        resolved.Should().Be(@"D:\Traces\capture.report.json");
    }

    [Fact]
    public void ResolveOutputPath_RequestedPathGiven_UsesItVerbatim()
    {
        string resolved = TraceReportWriter.ResolveOutputPath(@"D:\Traces\capture.etl", requestedOutputPath: @"C:\out\custom.json");

        resolved.Should().Be(@"C:\out\custom.json");
    }

    [Fact]
    public async Task WriteAsync_RealObservation_ProducesParseableJsonWithSubjectFieldsIntact()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trace-report-writer-test-{Guid.NewGuid():N}.json");
        _writtenFiles.Add(path);

        var methodRef = new MethodRef
        {
            DeclaringType = new TypeRef { CanonicalName = "My.Type", Fidelity = MatchFidelity.Exact },
            Name = "MyMethod",
            NormalizedSignature = "()",
            Fidelity = MatchFidelity.Exact,
        };
        var observation = new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "cpu.sample-attribution",
            Subjects = [methodRef],
            When = new TemporalExtent
            {
                Kind = TemporalKind.Interval,
                Start = new TimeAnchor { ProcessUptime = TimeSpan.Zero, Confidence = AnchorConfidence.Exact },
                End = new TimeAnchor { ProcessUptime = TimeSpan.FromSeconds(1), Confidence = AnchorConfidence.Exact },
            },
            Measures = new Dictionary<string, Measure> { ["cpu.sample-count"] = new Measure(5, MeasureUnit.Count, MeasureSemantics.Absolute) },
            Provenance = new Provenance
            {
                Artifact = new ArtifactId("trace.etl"),
                AnalyzerKey = "CpuHotspotAnalyzer",
                CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceCpuSamples },
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        };
        var report = new TraceSessionReport("trace.etl", DateTime.UtcNow, [observation]);

        await TraceReportWriter.WriteAsync(report, path, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        JsonElement observations = document.RootElement.GetProperty("observations");
        observations.GetArrayLength().Should().Be(1);

        JsonElement subject = observations[0].GetProperty("subjects")[0];
        subject.GetProperty("$kind").GetString().Should().Be("method");
        subject.GetProperty("name").GetString().Should().Be("MyMethod");
    }
}

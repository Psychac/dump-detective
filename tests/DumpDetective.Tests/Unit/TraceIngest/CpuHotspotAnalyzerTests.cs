using System.Linq;

using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.TraceIngest;

/// <summary>
/// Thin mapping test for the retyped <see cref="CpuHotspotAnalyzer"/> (now <c>IAnalyzer</c>,
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b) — address-range
/// resolution itself is <see cref="TraceCpuSampleQuery"/>'s job, covered by
/// <c>TraceCpuSampleQueryTests</c>; this only verifies the analyzer turns a
/// <see cref="CpuSampleAttribution"/> into the expected <see cref="Observation"/> shape and
/// declares the right capability.
/// </summary>
public sealed class CpuHotspotAnalyzerTests
{
    [Fact]
    public void RequiresCapability_DeclaresTraceCpuSamples()
    {
        var attribute = typeof(CpuHotspotAnalyzer).GetCustomAttributes(typeof(RequiresCapabilityAttribute), inherit: false)
            .Cast<RequiresCapabilityAttribute>().Single();

        attribute.Capabilities.Should().ContainSingle().Which.Should().Be(CapabilityVocabulary.TraceCpuSamples);
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsOneObservationPerAttribution()
    {
        var methodRef = new MethodRef
        {
            DeclaringType = new TypeRef { CanonicalName = "Some.Type", Fidelity = MatchFidelity.Exact },
            Name = "MethodA",
            NormalizedSignature = "()",
            Fidelity = MatchFidelity.Exact,
        };
        var attribution = new CpuSampleAttribution(methodRef, SampleCount: 7, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));

        var sink = new CollectingObservationSink();
        var context = new AnalysisContext
        {
            Observations = sink,
            ArtifactId = new ArtifactId("test"),
            TraceCpuSamples = new StubTraceCpuSampleQuery([attribution]),
        };

        await new CpuHotspotAnalyzer().AnalyzeAsync(context, CancellationToken.None);

        Observation observation = sink.Observations.Should().ContainSingle().Subject;
        observation.ObservationType.Should().Be("cpu.sample-attribution");
        observation.Subjects.Should().ContainSingle().Which.Should().Be(methodRef);
        observation.Measures["cpu.sample-count"].Value.Should().Be(7);
        observation.When.Kind.Should().Be(TemporalKind.Interval);
        observation.Provenance.Artifact.Should().Be(new ArtifactId("test"));
        observation.Provenance.CapabilitiesUsed.Should().ContainSingle().Which.Should().Be(new Capability(CapabilityVocabulary.TraceCpuSamples));
    }

    [Fact]
    public async Task AnalyzeAsync_NoTraceCpuSamplesCapability_EmitsNothing()
    {
        var sink = new CollectingObservationSink();
        var context = new AnalysisContext { Observations = sink, ArtifactId = new ArtifactId("test") };

        await new CpuHotspotAnalyzer().AnalyzeAsync(context, CancellationToken.None);

        sink.Observations.Should().BeEmpty();
    }

    private sealed class StubTraceCpuSampleQuery(IReadOnlyList<CpuSampleAttribution> attributions) : ITraceCpuSampleQuery
    {
        public IEnumerable<CpuSampleAttribution> EnumerateSampleAttributions() => attributions;
    }
}

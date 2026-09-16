using DumpDetective.Orchestration.Session;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

public sealed class SessionBuilderTests
{
    [Fact]
    public void Build_Throws_WhenNoArtifactsGiven()
    {
        Action act = () => SessionBuilder.Build([]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_ExposesArtifactAndItsCapabilities_ForSingleArtifactSession()
    {
        ArtifactDescriptor dump = CreateArtifact("dump-1", CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapTypes);

        AnalysisSession session = SessionBuilder.Build([dump]);

        session.Artifacts.Should().ContainSingle().Which.Should().Be(dump);
        session.AvailableCapabilities.Should().BeEquivalentTo(dump.Provides);
    }

    [Fact]
    public void Build_UnionsCapabilities_AcrossMultipleArtifacts()
    {
        ArtifactDescriptor dump = CreateArtifact("dump-1", CapabilityVocabulary.HeapObjects);
        ArtifactDescriptor trace = CreateArtifact("trace-1", CapabilityVocabulary.TraceGcEvents);

        AnalysisSession session = SessionBuilder.Build([dump, trace]);

        session.AvailableCapabilities.Should().BeEquivalentTo(
        [
            new Capability(CapabilityVocabulary.HeapObjects),
            new Capability(CapabilityVocabulary.TraceGcEvents),
        ]);
    }

    private static ArtifactDescriptor CreateArtifact(string id, params string[] capabilities) => new()
    {
        Id = new ArtifactId(id),
        SourceKind = "clr-dump",
        Path = $"/dumps/{id}.dmp",
        CapturedAt = new TimeAnchor { Confidence = AnchorConfidence.Unknown },
        Process = new ProcessIdentity { ProcessId = 1, ImageName = "test.exe" },
        Provides = capabilities.Select(c => new Capability(c)).ToHashSet(),
    };
}

using DumpDetective.Cli.Execution;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

/// <summary>
/// Verifies <see cref="TraceArtifactFactory"/>'s shape — docs/refactor/modularity/
/// phase-4-session-orchestration.md migration step 5c. Mirrors the same assertions
/// <c>SingleDumpArtifactFactory</c> would need, adjusted for the trace side's own known-gap
/// simplification (static "every trace capability" set, not a per-trace probe).
/// </summary>
public sealed class TraceArtifactFactoryTests : IDisposable
{
    private readonly string _tracePath;

    public TraceArtifactFactoryTests()
    {
        _tracePath = Path.Combine(Path.GetTempPath(), $"trace-artifact-factory-test-{Guid.NewGuid():N}.nettrace");
        File.WriteAllBytes(_tracePath, []);
    }

    public void Dispose()
    {
        if (File.Exists(_tracePath))
            File.Delete(_tracePath);
    }

    [Fact]
    public void Create_SetsNetTraceSourceKind()
    {
        ArtifactDescriptor artifact = TraceArtifactFactory.Create(_tracePath);

        artifact.SourceKind.Should().Be("nettrace");
        artifact.Path.Should().Be(_tracePath);
        artifact.Id.Should().Be(new ArtifactId(_tracePath));
    }

    [Fact]
    public void Create_ProvidesOnlyTraceCapabilities()
    {
        ArtifactDescriptor artifact = TraceArtifactFactory.Create(_tracePath);

        artifact.Provides.Should().NotBeEmpty();
        artifact.Provides.Should().OnlyContain(c => c.Key.StartsWith("trace.", StringComparison.Ordinal));
        artifact.Provides.Should().Contain(new Capability(CapabilityVocabulary.TraceCpuSamples));
        artifact.Provides.Should().Contain(new Capability(CapabilityVocabulary.TraceGcEvents));
        artifact.Provides.Should().Contain(new Capability(CapabilityVocabulary.TraceContentionEvents));
    }
}

using DumpDetective.Orchestration.Session;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

public sealed class SessionValidatorTests
{
    [Fact]
    public void Validate_Throws_WhenNoArtifactsGiven()
    {
        Action act = () => SessionValidator.Validate([]);

        act.Should().Throw<SessionValidationException>();
    }

    [Fact]
    public void Validate_DoesNotThrow_ForOneHomogeneousDumpArtifact()
    {
        ArtifactDescriptor dump = CreateArtifact("dump-1", "clr-dump");

        Action act = () => SessionValidator.Validate([dump]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DoesNotThrow_ForNHomogeneousDumpArtifacts()
    {
        ArtifactDescriptor[] dumps =
        [
            CreateArtifact("dump-1", "clr-dump"),
            CreateArtifact("dump-2", "clr-dump"),
            CreateArtifact("dump-3", "clr-dump"),
        ];

        Action act = () => SessionValidator.Validate(dumps);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Widened in migration step 5c: a session of exactly one dump plus one trace is the new
    /// combined-session shape this rule exists to allow — see <c>SessionValidator</c>'s own remarks.
    /// </summary>
    [Fact]
    public void Validate_DoesNotThrow_ForOneDumpPlusOneTraceArtifact()
    {
        ArtifactDescriptor dump = CreateArtifact("dump-1", "clr-dump");
        ArtifactDescriptor trace = CreateArtifact("trace-1", "nettrace");

        Action act = () => SessionValidator.Validate([dump, trace]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Throws_WhenArtifactsHaveMixedSourceKinds_AndAreNotOneDumpPlusOneTrace()
    {
        ArtifactDescriptor dump = CreateArtifact("dump-1", "clr-dump");
        ArtifactDescriptor gcDump = CreateArtifact("gcdump-1", "gcdump");

        Action act = () => SessionValidator.Validate([dump, gcDump]);

        act.Should().Throw<SessionValidationException>();
    }

    [Fact]
    public void Validate_Throws_WhenTwoDumpsPlusOneTrace()
    {
        ArtifactDescriptor[] artifacts =
        [
            CreateArtifact("dump-1", "clr-dump"),
            CreateArtifact("dump-2", "clr-dump"),
            CreateArtifact("trace-1", "nettrace"),
        ];

        Action act = () => SessionValidator.Validate(artifacts);

        act.Should().Throw<SessionValidationException>();
    }

    [Fact]
    public void Validate_Throws_WhenOneDumpPlusTwoTraces()
    {
        ArtifactDescriptor[] artifacts =
        [
            CreateArtifact("dump-1", "clr-dump"),
            CreateArtifact("trace-1", "nettrace"),
            CreateArtifact("trace-2", "nettrace"),
        ];

        Action act = () => SessionValidator.Validate(artifacts);

        act.Should().Throw<SessionValidationException>();
    }

    [Fact]
    public void Validate_Throws_WhenArtifactsHaveDuplicatePaths()
    {
        ArtifactDescriptor dump1 = CreateArtifact("dump-1", "clr-dump");
        ArtifactDescriptor dump2 = CreateArtifact("dump-1", "clr-dump");

        Action act = () => SessionValidator.Validate([dump1, dump2]);

        act.Should().Throw<SessionValidationException>();
    }

    private static ArtifactDescriptor CreateArtifact(string id, string sourceKind) => new()
    {
        Id = new ArtifactId(id),
        SourceKind = sourceKind,
        Path = $"/dumps/{id}.dmp",
        CapturedAt = new TimeAnchor { Confidence = AnchorConfidence.Unknown },
        Process = new ProcessIdentity { ProcessId = 1, ImageName = "test.exe" },
        Provides = new HashSet<Capability>(),
    };
}

using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// Metadata describing one input artifact — what kind it is, where it came from, and what
/// capabilities it can provide to analyzers. See docs/refactor/modularity/source-model.md § 2.
/// </summary>
public sealed record ArtifactDescriptor
{
    public required ArtifactId Id { get; init; }

    /// <summary>"clr-dump", "nettrace", "gcdump", ... — a source-neutral tag, not a file extension.</summary>
    public required string SourceKind { get; init; }

    public required string Path { get; init; }

    public required TimeAnchor CapturedAt { get; init; }

    public required ProcessIdentity Process { get; init; }

    public required IReadOnlySet<Capability> Provides { get; init; }
}

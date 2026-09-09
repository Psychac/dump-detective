using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// A pointer into an artifact's own disk-backed index — never a copy of the underlying data. This
/// is what lets an observation cite the millions of heap objects or trace events behind it without
/// ever materializing them. <see cref="Locator"/> is opaque to the SDK and meaningful only to the
/// artifact's own index reader (e.g. a heap address for a dump, an event offset for a trace).
/// </summary>
public sealed record EvidenceRef
{
    public required ArtifactId Artifact { get; init; }

    public required string Locator { get; init; }

    public string? Description { get; init; }
}

using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A specific heap object. Artifact-scoped by nature — a raw address is meaningless outside the
/// artifact it was read from, so this is never used as a cross-source join key. <see cref="JoinKey"/>
/// still uniquely identifies the object *within* its own artifact (for evidence/drill-down
/// linking), but correlation logic must never treat two <see cref="ObjectRef"/>s from different
/// artifacts as comparable. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
public sealed record ObjectRef : EntityRef
{
    public override EntityKind Kind => EntityKind.Object;

    public required ulong Address { get; init; }

    public required ArtifactId Artifact { get; init; }

    public override string JoinKey => $"{Artifact.Value}:0x{Address:X}";
}

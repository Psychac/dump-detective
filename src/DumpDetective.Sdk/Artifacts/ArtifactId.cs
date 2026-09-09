namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// Session-unique, stable identifier for one input artifact (a dump, a trace, ...). Never reused
/// within a session; carried by <see cref="Identity.ObjectRef"/> and
/// <see cref="Observations.Provenance"/> so every source-local fact traces back to exactly one
/// input. See docs/refactor/modularity/source-model.md § 2.
/// </summary>
public readonly record struct ArtifactId(string Value)
{
    public override string ToString() => Value;
}

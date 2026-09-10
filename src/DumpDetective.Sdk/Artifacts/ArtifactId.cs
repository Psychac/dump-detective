namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// Session-unique, stable identifier for one input artifact (a dump, a trace, ...). Never reused
/// within a session; carried by <see cref="Identity.ObjectRef"/> and
/// <see cref="Observations.Provenance"/> so every source-local fact traces back to exactly one
/// input. See docs/refactor/modularity/source-model.md § 2.
/// </summary>
/// <remarks>
/// Deliberately no <c>implicit operator ArtifactId(string)</c>, unlike <see cref="Capability"/>.
/// The two look symmetric but aren't used that way: a capability is written as a literal dozens of
/// times across analyzer declarations (`CapabilityVocabulary.TraceGcEvents` inside a
/// `HashSet&lt;Capability&gt;` initializer, for instance), so an implicit conversion pays for
/// itself at every one of those call sites. An artifact id is constructed exactly once per real
/// artifact, at whatever component first probes/discovers it (see
/// <c>TraceAnalysisRunner</c>) — there's no repeated-literal ergonomics to buy, and an implicit
/// conversion there would instead risk a stray string silently becoming a session identity by
/// accident. See docs/refactor/modularity/phase-1-sdk-review-findings.md item 7.
/// </remarks>
public readonly record struct ArtifactId(string Value)
{
    public override string ToString() => Value;
}

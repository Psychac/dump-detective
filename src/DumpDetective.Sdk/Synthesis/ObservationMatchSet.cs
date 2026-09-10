using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>The observations that matched an <see cref="ISynthesisRule"/>'s
/// <see cref="ISynthesisRule.Query"/>, handed to <see cref="ISynthesisRule.SynthesizeAsync"/>.</summary>
public sealed record ObservationMatchSet(IReadOnlyList<Observation> Matched);

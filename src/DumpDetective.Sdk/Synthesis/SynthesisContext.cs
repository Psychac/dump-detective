namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Context handed to a rule alongside its matched observations.
/// </summary>
/// <remarks>
/// Deliberately minimal for this trimmed Phase 1 pass — a session-scoped concept (carrying e.g. a
/// session id, or a handle back to the full observation store for a rule that needs to look beyond
/// its own match set) is a Phase 4/5 concern, not yet built. Grows as those phases land rather than
/// being speculatively shaped now.
/// </remarks>
public sealed record SynthesisContext;

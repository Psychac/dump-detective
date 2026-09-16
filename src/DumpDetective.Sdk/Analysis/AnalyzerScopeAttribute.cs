namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Whether an analyzer runs once per artifact in a session, or once for the whole session — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5. Reassigned to this
/// phase after Phase 1/3 confirmed neither actually built it: every analyzer today (dump-side and,
/// once retyped in step 5b, trace-side) genuinely runs against one artifact's own data, so
/// <see cref="PerArtifact"/> is the only value any real analyzer declares right now.
/// <see cref="PerSession"/> exists for the cross-artifact correlation/synthesis analyzers Phase 5/7
/// will add, not built speculatively ahead of that need.
/// </summary>
public enum AnalyzerScope
{
    PerArtifact,
    PerSession,
}

/// <summary>
/// Declares an analyzer's <see cref="Analysis.AnalyzerScope"/> — see
/// <see cref="Analysis.AnalyzerScope"/>'s own remarks. Undeclared defaults to
/// <see cref="AnalyzerScope.PerArtifact"/> (see <see cref="Scope"/>'s default), matching every
/// analyzer in today's catalog without requiring each one to add a no-op attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AnalyzerScopeAttribute(AnalyzerScope scope) : Attribute
{
    public AnalyzerScope Scope { get; } = scope;
}

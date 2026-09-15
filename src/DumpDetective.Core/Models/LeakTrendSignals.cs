namespace DumpDetective.Core.Models;

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes its top highly-referenced
/// objects as (type name, size) pairs for the shared new-leak-signal trend heuristic in
/// <c>DumpDetective.Analysis.Trend.TrendAnalyzer</c>. A structural interface rather than a
/// concrete type reference, so <c>TrendAnalyzer</c> (which must stay source-neutral, referenced by
/// no <c>Plugins.*</c> package) never needs to reference the producing analyzer's package.
/// </summary>
public interface IRetainedObjectsByTypeSignal
{
    IReadOnlyList<NameBytesEntry> TopHighlyReferencedObjectsByType { get; }
}

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes its top static roots as
/// (root description, retained bytes) pairs for the same trend heuristic as
/// <see cref="IRetainedObjectsByTypeSignal"/>.
/// </summary>
public interface IStaticRootsByDescriptionSignal
{
    IReadOnlyList<NameBytesEntry> TopRootsByRetainedBytes { get; }
}

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes its top leak candidates as
/// (type name, size) pairs for the same trend heuristic as <see cref="IRetainedObjectsByTypeSignal"/>.
/// </summary>
public interface ILeakCandidatesByTypeSignal
{
    IReadOnlyList<NameBytesEntry> TopCandidatesByType { get; }
}

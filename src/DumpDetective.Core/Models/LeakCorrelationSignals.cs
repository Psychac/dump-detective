namespace DumpDetective.Core.Models;

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes GC-handle pinned/dependent
/// target-type data for cross-analyzer correlation (see <c>LeakCandidateAnalyzer</c>'s remarks in
/// <c>DumpDetective.Analysis</c>). A structural interface rather than a concrete type reference, so
/// the producing analyzer's package and the consuming analyzer's package never need to reference
/// each other.
/// </summary>
public interface IHandleRetentionSignal
{
    IReadOnlyList<NameCountEntry> TopPinnedTargetTypes { get; }
    IReadOnlyList<NameCountEntry> DependentTopTargetTypes { get; }
}

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes a direct-GC-root-by-address
/// signal for cross-analyzer correlation. Deliberately thinner than the producing analyzer's own
/// root-finding record — carries only the fields a correlating consumer needs.
/// </summary>
public interface IDirectRootSignal
{
    IReadOnlyList<DirectRootEntry> TopRootsBySeverity { get; }
}

public sealed record DirectRootEntry(ulong TargetAddress, string RootKind, string? FieldDescription);

/// <summary>
/// Implemented by an <see cref="AnalyzerDomainResult"/> that exposes a de-duplicated logical count
/// signal for cross-analyzer correlation.
/// </summary>
public interface ILogicalTimerCountSignal
{
    int LogicalTimerCount { get; }
}

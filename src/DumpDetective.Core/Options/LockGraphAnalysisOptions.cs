namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>LockGraphAnalyzer</c>.
/// </summary>
public sealed class LockGraphAnalysisOptions
{
    public int MaxContestedLocksToShow { get; init; } = 15;
}

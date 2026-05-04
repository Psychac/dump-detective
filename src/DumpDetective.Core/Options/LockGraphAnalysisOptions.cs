namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>LockGraphAnalyzer</c>.
/// </summary>
public sealed class LockGraphAnalysisOptions
{
    public int MaxContestedLocksToShow { get; init; } = 15;

    public static LockGraphAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new LockGraphAnalysisOptions { MaxContestedLocksToShow = 8 },
        AnalysisProfile.Full => new LockGraphAnalysisOptions { MaxContestedLocksToShow = 40 },
        _ => new LockGraphAnalysisOptions(),
    };

    public static LockGraphAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

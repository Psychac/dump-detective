namespace DumpDetective.Core.Options;

/// <summary>
/// Runtime-injectable options for segment analysis behavior.
/// </summary>
public sealed class HeapTopologyAnalysisOptions
{
    /// <summary>
    /// When <see langword="false"/> (default), per-object counting is skipped for all
    /// SOH segments. Only LOH and POH segments are counted exactly.
    ///
    /// Set to <see langword="true"/> when exact SOH object counts are required.
    /// </summary>
    public bool CountSohObjects { get; init; } = false;

    public static HeapTopologyAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new HeapTopologyAnalysisOptions { CountSohObjects = false },
        AnalysisProfile.Full => new HeapTopologyAnalysisOptions { CountSohObjects = true },
        _ => new HeapTopologyAnalysisOptions(),
    };

    public static HeapTopologyAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

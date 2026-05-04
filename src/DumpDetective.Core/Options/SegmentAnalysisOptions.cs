namespace DumpDetective.Core.Options;

/// <summary>
/// Runtime-injectable options for segment analysis behavior.
/// </summary>
public sealed class SegmentAnalysisOptions
{
    /// <summary>
    /// When <see langword="false"/> (default), per-object counting is skipped for all
    /// SOH segments. Only LOH and POH segments are counted exactly.
    ///
    /// Set to <see langword="true"/> when exact SOH object counts are required.
    /// </summary>
    public bool CountSohObjects { get; init; } = false;
}

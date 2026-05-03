namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runtime-injectable options for <see cref="SegmentAnalyzer"/>.
/// Inject via <c>context.Options[typeof(SegmentAnalysisOptions)]</c> from the CLI pipeline
/// or read via <c>context.GetOption&lt;SegmentAnalysisOptions&gt;()</c>.
/// </summary>
public sealed class SegmentAnalysisOptions
{
    /// <summary>
    /// When <see langword="false"/> (default), per-object counting is skipped for all
    /// SOH segments — the dominant cost on large dumps (87 M objects / 120 s).
    /// Only LOH and POH segments are counted exactly; they hold far fewer objects
    /// but are diagnostically the most important for fragmentation and size analysis.
    ///
    /// Set to <see langword="true"/> when exact SOH object counts are required (adds
    /// significant scan time on large dumps).
    /// </summary>
    public bool CountSohObjects { get; init; } = false;
}

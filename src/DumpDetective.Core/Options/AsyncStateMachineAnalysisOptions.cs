namespace DumpDetective.Core.Options;

public sealed class AsyncStateMachineAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int TypeCandidateLimit { get; init; } = 200;
    public int SuspendedMethodMapLimit { get; init; } = 20;
    public ulong LargeCaptureThresholdBytes { get; init; } = 1_024 * 1_024;
    public int TopCapturedSizeEntries { get; init; } = 10;

    /// <summary>
    /// Max instances sampled per state-machine type when building the suspend-state
    /// histogram (bounds the second heap pass; see <see cref="HistogramTopTypeLimit"/>).
    /// </summary>
    public int HistogramInstanceCapPerType { get; init; } = 1_000;

    /// <summary>
    /// Only the top-N state machine types (by instance count) get a suspend-state
    /// histogram; the rest keep <c>StateDistribution</c> empty. Bounds the number of
    /// method tables tracked during the histogram heap pass.
    /// </summary>
    public int HistogramTopTypeLimit { get; init; } = 10;

    public static AsyncStateMachineAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AsyncStateMachineAnalysisOptions { TopTypeLimit = 10, TypeCandidateLimit = 100, SuspendedMethodMapLimit = 10, LargeCaptureThresholdBytes = 2 * 1024 * 1024, TopCapturedSizeEntries = 5, HistogramInstanceCapPerType = 500, HistogramTopTypeLimit = 5 },
        AnalysisProfile.Full => new AsyncStateMachineAnalysisOptions { TopTypeLimit = 40, TypeCandidateLimit = 500, SuspendedMethodMapLimit = 40, LargeCaptureThresholdBytes = 512 * 1024, TopCapturedSizeEntries = 20, HistogramInstanceCapPerType = 2_000, HistogramTopTypeLimit = 20 },
        _ => new AsyncStateMachineAnalysisOptions(),
    };

    public static AsyncStateMachineAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

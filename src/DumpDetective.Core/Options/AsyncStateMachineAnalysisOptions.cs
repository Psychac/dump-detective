namespace DumpDetective.Core.Options;

public sealed class AsyncStateMachineAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int TypeCandidateLimit { get; init; } = 200;
    public int SuspendedMethodMapLimit { get; init; } = 20;
    public ulong LargeCaptureThresholdBytes { get; init; } = 1_024 * 1_024;
    public int TopCapturedSizeEntries { get; init; } = 10;

    public static AsyncStateMachineAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AsyncStateMachineAnalysisOptions { TopTypeLimit = 10, TypeCandidateLimit = 100, SuspendedMethodMapLimit = 10, LargeCaptureThresholdBytes = 2 * 1024 * 1024, TopCapturedSizeEntries = 5 },
        AnalysisProfile.Full => new AsyncStateMachineAnalysisOptions { TopTypeLimit = 40, TypeCandidateLimit = 500, SuspendedMethodMapLimit = 40, LargeCaptureThresholdBytes = 512 * 1024, TopCapturedSizeEntries = 20 },
        _ => new AsyncStateMachineAnalysisOptions(),
    };

    public static AsyncStateMachineAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

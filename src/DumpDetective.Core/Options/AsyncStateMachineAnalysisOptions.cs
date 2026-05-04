namespace DumpDetective.Core.Options;

public sealed class AsyncStateMachineAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int TypeCandidateLimit { get; init; } = 200;
    public int SuspendedMethodMapLimit { get; init; } = 20;
    public ulong LargeCaptureThresholdBytes { get; init; } = 1_024 * 1_024;
    public int TopCapturedSizeEntries { get; init; } = 10;
}

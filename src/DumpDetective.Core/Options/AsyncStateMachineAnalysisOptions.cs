namespace DumpDetective.Core.Options;

public sealed class AsyncStateMachineAnalysisOptions
{
    public ulong LargeCaptureThresholdBytes { get; init; } = 1_024 * 1_024;
}

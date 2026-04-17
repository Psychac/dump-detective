namespace DumpDetective.Analysis.Configuration;

// TEMP-REFRACTOR-BRIDGE: Replace with Spec 02 options binding (IOptions<T> + System.CommandLine binder).
internal sealed class AnalysisConfiguration
{
    public int HighReferenceThreshold { get; init; } = 50;
    public int MaxDuplicateStringLength { get; init; } = 500;
    public int MinDuplicateStringCount { get; init; } = 10;
    public int MaxReferenceAddressesToTrack { get; init; } = 1_000_000;
    public int ReferenceChainTopCount { get; init; } = 5;
    public int ReferenceChainMaxPathSearchObjects { get; init; } = 5_000;
    public int EventLeakMinSubscribers { get; init; } = 0;
}
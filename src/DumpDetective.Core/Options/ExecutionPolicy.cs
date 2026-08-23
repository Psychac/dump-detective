namespace DumpDetective.Core.Options;

public sealed record ExecutionPolicy
{
    public int MaxLeakScanObjects { get; init; } = 2_000_000;
    public int MaxReferenceAddresses { get; init; } = 1_000_000;

    public static ExecutionPolicy Default { get; } = new();
}
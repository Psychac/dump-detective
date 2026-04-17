namespace DumpDetective.Core.Options;

internal sealed class MemoryLeakOptions
{
    public int HighReferenceThreshold { get; init; } = 50;
    public int MaxDuplicateStringLength { get; init; } = 500;
    public int MinDuplicateStringCount { get; init; } = 10;
    public int MaxReferenceAddresses { get; init; } = 1_000_000;
}
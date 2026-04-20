namespace DumpDetective.Core.Options;

internal sealed class ReferenceChainOptions
{
    public int TopCount { get; init; } = 5;
    public int MaxPathSearchObjects { get; init; } = 5_000;
}
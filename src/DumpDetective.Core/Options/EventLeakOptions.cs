namespace DumpDetective.Core.Options;

public sealed class EventLeakOptions
{
    public int MinSubscribers { get; init; } = 0;
}
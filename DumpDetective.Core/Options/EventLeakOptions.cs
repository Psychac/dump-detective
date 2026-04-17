namespace DumpDetective.Core.Options;

internal sealed class EventLeakOptions
{
    public int MinSubscribers { get; init; } = 0;
}
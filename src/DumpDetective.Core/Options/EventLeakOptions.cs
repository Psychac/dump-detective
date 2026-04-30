namespace DumpDetective.Core.Options;

public sealed class EventLeakOptions
{
    public int MinSubscribers { get; init; } = 0;

    /// <summary>
    /// When true, scan all MulticastDelegate event fields regardless of subscriber count.
    /// Enables the full subscription graph view (§12.1). Default off for performance.
    /// </summary>
    public bool IncludeNonLeakingEvents { get; init; } = false;
}
namespace DumpDetective.Core.Options;

public sealed class EventLeakOptions
{
    public int MinSubscribers { get; init; } = 0;

    /// <summary>
    /// When true, scan all MulticastDelegate event fields regardless of subscriber count.
    /// Enables the full subscription graph view (§12.1). Default off for performance.
    /// </summary>
    public bool IncludeNonLeakingEvents { get; init; } = false;

    // Presentation / analysis tuning
    public int TopSubscriberTypesToShow { get; init; } = 5;
    public int TopDetailedInstancesPerGroup { get; init; } = 5;

    // Severity scoring
    public int SeveritySubscriberThreshold { get; init; } = 10;
    public int SeveritySubscriberBonus { get; init; } = 5;
    public int SeverityStaticPublisherBonus { get; init; } = 10;
    public int SeverityRootHintBonus { get; init; } = 5;
}
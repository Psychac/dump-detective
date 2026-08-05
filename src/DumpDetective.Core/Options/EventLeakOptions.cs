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

    // Severity scoring — existing signals
    public int SeverityStaticPublisherBonus { get; init; } = 10;
    public int SeverityRootHintBonus { get; init; } = 5;

    // Severity scoring — new heuristic signals
    public int SeverityGen2PublisherBonus { get; init; } = 5;
    public int SeverityDuplicateSubscriptionBonus { get; init; } = 8;
    public int SeverityDisposedButSubscribedBonus { get; init; } = 15;
    public int SeverityLifetimeMismatchBonus { get; init; } = 8;

    // Continuous replacement (design §9) for the old subscriber-count step bonus
    // (score += subscriberCount * log2(subscriberCount + 1) scale factor). Tuned so the
    // AddFindings thresholds (>= 35 Critical, >= 20 Warning) land on roughly the same
    // subscriber-count boundaries as the old step function.
    public double SeveritySubscriberLogScale { get; init; } = 1.45;

    // bonus for subscribers that appear to have very few incoming references
    public int SeverityLowIncomingRefsBonus { get; init; } = 8;

    // Partial reverse-validation (Step 6): heap-scan per subscriber to count incoming refs.
    // DISABLED by default — extremely expensive on large heaps (25 GB+, 87 M objects).
    // Only enable for targeted post-analysis on small suspect sets.
    public bool EnableLowIncomingRefsCheck { get; init; } = false;

    // Lifetime mismatch: max subscriber objects to probe for generation per event instance
    public int LifetimeMismatchProbeLimit { get; init; } = 50;
    // Minimum fraction (0.0–1.0) of probed Gen0/Gen1 subscribers to declare a mismatch
    public double LifetimeMismatchGen01Threshold { get; init; } = 0.5;

    // Diagnostics: when true, the analyzer will emit timing counters for hotspots.
    public bool EnableDiagnostics { get; init; } = true;

    // Publisher qualification: minimum subscribers for an object to be considered a publisher
    public int PublisherSubscriberThreshold { get; init; } = 1;

    // Bounded evidence enrichment (design §4.2): only the top-N groups by TotalSubscribers
    // get a root-path BFS attempt; the rest keep their cheap RootHint only.
    public int MaxGroupsToEnrich { get; init; } = 25;
    // Wall-clock budget for the entire enrichment loop across all enriched instances.
    public int MaxEvidenceEnrichmentMs { get; init; } = 2000;

    public static EventLeakOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new EventLeakOptions
        {
            MinSubscribers = 3,
            IncludeNonLeakingEvents = false,
            TopSubscriberTypesToShow = 3,
            TopDetailedInstancesPerGroup = 3,
            EnableDiagnostics = false,
            PublisherSubscriberThreshold = 2,
            MaxGroupsToEnrich = 10
        },
        AnalysisProfile.Full => new EventLeakOptions
        {
            MinSubscribers = 0,
            IncludeNonLeakingEvents = true,
            TopSubscriberTypesToShow = 20,
            TopDetailedInstancesPerGroup = 20,
            EnableDiagnostics = true,
            PublisherSubscriberThreshold = 1,
            MaxGroupsToEnrich = 100
        },
        _ => new EventLeakOptions(),
    };

    public static EventLeakOptions Default { get; } = Preset(AnalysisProfile.Balanced);

}
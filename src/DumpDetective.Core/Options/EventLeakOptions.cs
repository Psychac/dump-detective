namespace DumpDetective.Core.Options;

public sealed class EventLeakOptions
{
    /// <summary>
    /// Bounded top-K capacity used during the streaming heap scan itself (see
    /// <c>EventLeakAnalyzer.AddToAccumulator</c>) — not a post-hoc display truncation of an
    /// already-complete list. Determines which instances of a group survive in
    /// <c>GroupAccumulator.TopInstances</c> as the scan progresses; the group-level roll-ups
    /// (<c>AllSubscriberTypeCounts</c> etc.) already cover every instance regardless of this cap.
    /// Kept as a real work-scoping threshold — see §9.19 implementation notes in
    /// docs/refactor/analysis-profile-removal-plan.md for why (same pattern as Collection's
    /// <c>TopWastefulCollectionsToShow</c> and Dominator's <c>TopHighlyReferencedObjectsToShow</c>).
    /// </summary>
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

    /// <summary>
    /// Partial reverse-validation (Step 6): per-subscriber incoming-reference check. DISABLED by
    /// default — <c>EventLeakAnalyzer.CountIncomingRefs</c> still samples the first ~500 objects
    /// from <c>heap.EnumerateObjects()</c> rather than using the disk-backed reverse-edge index,
    /// so on a large heap it is both expensive and largely inaccurate (the sampled objects are
    /// essentially arbitrary relative to the target). See §9.19 implementation notes in
    /// docs/refactor/analysis-profile-removal-plan.md — fixing this to use
    /// <c>IBackwardReferenceProvider</c> for an exact O(1) lookup is a real, scoped follow-up, not
    /// done in this pass (it requires threading the provider through
    /// <c>EventLeakFastScanner</c>'s per-object hot path, which currently has no cache reference).
    /// </summary>
    public bool EnableLowIncomingRefsCheck { get; init; } = false;

    /// <summary>
    /// Max subscribers probed for the (deferred, see <see cref="EnableLowIncomingRefsCheck"/>)
    /// incoming-refs signal only — <c>EventLeakAnalyzer.CheckLifetimeMismatch</c>'s generation-based
    /// lifetime-mismatch check probes every subscriber unconditionally now (an O(1) segment lookup
    /// per subscriber, cheap regardless of scale, so no cap was needed there).
    /// </summary>
    public int LifetimeMismatchProbeLimit { get; init; } = 50;
    // Minimum fraction (0.0–1.0) of probed Gen0/Gen1 subscribers to declare a mismatch
    public double LifetimeMismatchGen01Threshold { get; init; } = 0.5;

    // Publisher qualification: minimum subscribers for an object to be considered a publisher
    public int PublisherSubscriberThreshold { get; init; } = 1;

    /// <summary>
    /// Wall-clock budget for the root-path evidence-enrichment loop across all leak instances.
    /// The only time-based budget in the options surface — see §9.19 implementation notes in
    /// docs/refactor/analysis-profile-removal-plan.md. Every instance is now eligible for
    /// enrichment (in severity-priority order, since the caller pre-sorts descending); this budget
    /// alone governs how much of that work actually runs, replacing the deleted
    /// <c>MaxGroupsToEnrich</c> group-count pre-filter.
    /// </summary>
    public int MaxEvidenceEnrichmentMs { get; init; } = 2000;
}
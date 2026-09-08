using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Event Leaks

/// <summary>One row in the "Top Publisher Events" summary table.</summary>
internal sealed record PublisherEventSummary(
    string PublisherType,
    string EventFieldName,
    int TotalSubscribers,
    int InstanceCount,
    ulong EstimatedRetainedBytes);

internal sealed record EventLeakDomainResult(
    int TotalEventLeakInstances,
    int TotalSubscribers,
    int StaticEventLeakCount,
    int InstanceEventLeakCount,
    IReadOnlyList<NameCountEntry>? TopPublisherEventsBySubscribers = null,
    IReadOnlyList<EventLeakGroupSnapshot>? TopLeakGroups = null,
    IReadOnlyList<EventLeakInstanceSnapshot>? TopLeakInstances = null,
    int TotalEventsScanned = 0,
    int TotalPublisherInstances = 0,
    IReadOnlyList<PublisherEventSummary>? TopPublisherEvents = null,
    // Tier 1 (design §4.4): TotalSubscribers × avgSubscriberSizeByMT, folded across all groups.
    // Not dominator-exact — see EventLeakGroupSnapshot.EstimatedSubscriberRetainedBytes label.
    ulong TotalEstimatedRetainedBytes = 0,
    // Bumped whenever the severity-scoring formula changes (design §9). Trend comparisons
    // across a version boundary are not meaningful and must be refused, not diffed.
    int ScoringVersion = 2,
    // Phase E / design §7: cross-group correlation views, folded once over the completed
    // group set. First-class collections, not buried in a per-group breakdown.
    IReadOnlyList<NameCountEntry>? TopSubscriberTypesAcrossGroups = null,
    IReadOnlyList<NameCountEntry>? TopHandlerMethodsAcrossGroups = null,
    // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): distinguishes "checked and clean"
    // from "not scanned" — PublisherRegistry.CandidatePublisherCount vs. the distinct set of
    // publisher MTs that produced at least one accepted leak (AddToAccumulator's leakingMTs).
    // Both MT-keyed, so the split stays exact even if two loaded types share a display name.
    int PublisherTypesScanned = 0,
    int CleanPublisherTypeCount = 0,
    // P3-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): counts folded once over
    // TopLeakGroups, mirroring PublisherTypesScanned/CleanPublisherTypeCount's shape — cheap
    // aggregate visibility for the two highest-signal, most common event-leak categories.
    int TimerEventLeakGroupCount = 0,
    int PropertyChangedEventLeakGroupCount = 0,
    // P3-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): subscriber-count distribution
    // across ALL leak instances, in ascending-bucket order (not sorted by count — a histogram
    // reads correctly only in its natural order). Distinguishes "one giant leaking publisher"
    // from "many small leaks adding up".
    IReadOnlyList<NameCountEntry>? SubscriberCountHistogram = null) : AnalyzerDomainResult;

internal sealed record EventLeakGroupSnapshot(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    int SeverityScore,
    int InstanceCount,
    int TotalSubscribers,
    double AverageSubscribers,
    int MinSubscribers,
    int MaxSubscribers,
    IReadOnlyList<NameCountEntry>? TopSubscriberTypes = null,
    ulong EstimatedSubscriberRetainedBytes = 0,
    bool HasDuplicateSubscriptions = false,
    int DisposedButSubscribedInstances = 0,
    bool HasLifetimeMismatch = false,
    // P3-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): pure string-pattern
    // classification (EventLeakAnalyzer.IsTimerEvent/IsPropertyChangedEvent) over
    // PublisherType/EventFieldName — computed once per group, not per instance, since both
    // depend only on data shared by every instance in the group.
    bool IsTimerEvent = false,
    bool IsPropertyChangedEvent = false);

/// <summary>Per-subscriber detail row shown in the instance drill-down.</summary>
internal sealed record SubscriberDetail(
    string Type,
    string? MethodName,
    ulong Size,
    int Count = 1,
    /// <summary>True when <see cref="Size"/> is the exact dominator-tree retained bytes for this
    /// subscriber rather than the per-type shallow-size average (§9).</summary>
    bool SizeIsExact = false);

/// <summary>Structured subscriber-type tally for an instance; formatted for display in the report layer.</summary>
internal sealed record SubscriberTypeCount(string Type, int Count);

internal sealed record EventLeakInstanceSnapshot(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    ulong PublisherAddress,
    int SeverityScore,
    int SubscriberCount,
    string? RootHint,
    IReadOnlyList<SubscriberTypeCount>? SubscriberTypes = null,
    int PublisherGeneration = -1,
    int DuplicateSubscriptionCount = 0,
    bool IsDisposedButSubscribed = false,
    bool HasLifetimeMismatch = false,
    IReadOnlyList<SubscriberDetail>? SubscriberDetails = null,
    EventLeakEvidence? Evidence = null);

/// <summary>
/// EventLeak-specific evidence shape (design §4.3): the publisher's own BFS-derived root path
/// and a cheap subscriber-derived root hint are tracked separately rather than conflated into
/// one field, so the report can label which source produced the displayed path. Distinct from
/// the shared <see cref="Evidence"/> record used by Dominator/StaticRoot/Timer analyzers.
/// </summary>
internal sealed record EventLeakEvidence(
    int SchemaVersion,
    string? PublisherRootPath,
    string? SampleSubscriberHint,
    bool SearchTruncated,
    IReadOnlyList<EvidenceSignal> Signals);

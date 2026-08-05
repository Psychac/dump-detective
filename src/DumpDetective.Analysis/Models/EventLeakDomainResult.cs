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
    IReadOnlyList<NameCountEntry>? TopHandlerMethodsAcrossGroups = null) : AnalyzerDomainResult;

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
    bool HasLifetimeMismatch = false);

/// <summary>Per-subscriber detail row shown in the instance drill-down.</summary>
internal sealed record SubscriberDetail(
    string Type,
    string? MethodName,
    ulong Size,
    int Count = 1);

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

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
    IReadOnlyList<PublisherEventSummary>? TopPublisherEvents = null) : AnalyzerDomainResult;

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
    int OrphanedSubscriberInstances = 0,
    bool HasLifetimeMismatch = false);

/// <summary>Per-subscriber detail row shown in the instance drill-down.</summary>
internal sealed record SubscriberDetail(
    string Type,
    string? MethodName,
    ulong Size,
    int Count = 1);

internal sealed record EventLeakInstanceSnapshot(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    ulong PublisherAddress,
    int SeverityScore,
    int SubscriberCount,
    string? RootHint,
    IReadOnlyList<string>? SubscriberTypes = null,
    int PublisherGeneration = -1,
    int DuplicateSubscriptionCount = 0,
    int OrphanedSubscriberCount = 0,
    bool HasLifetimeMismatch = false,
    IReadOnlyList<SubscriberDetail>? SubscriberDetails = null,
    Evidence? Evidence = null);

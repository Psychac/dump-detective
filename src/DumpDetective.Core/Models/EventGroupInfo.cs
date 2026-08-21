namespace DumpDetective.Core.Models;

internal class EventGroupInfo
{
    public string PublisherType { get; set; } = string.Empty;
    public string EventFieldName { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public int SeverityScore { get; set; }
    public int InstanceCount { get; set; }
    public int TotalSubscribers { get; set; }
    public double AverageSubscribers { get; set; }
    public int MaxSubscribers { get; set; }
    public int MinSubscribers { get; set; }
    public List<EventLeakInfo> Instances { get; set; } = new();
    // Subscriber type counts accumulated across ALL instances (not just the stored top-N).
    public Dictionary<string, int>? AllSubscriberTypeCounts { get; set; }
    // Handler-method counts accumulated across ALL instances (design §7 correlation phase).
    public Dictionary<(string Type, string? MethodName), int>? AllSubscriberMethodCounts { get; set; }
}

internal class EventLeakInfo
{
    public ulong PublisherAddress { get; set; }
    public string PublisherType { get; set; } = string.Empty;
    public string EventFieldName { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public int SeverityScore { get; set; }
    public string RootHint { get; set; } = string.Empty;
    public int SubscriberCount { get; set; }
    public List<SubscriberInfo> Subscribers { get; set; } = new();
    // Heuristic signals
    public int PublisherGeneration { get; set; } = -1;
    public int DuplicateSubscriptionCount { get; set; }
    public bool IsDisposedButSubscribed { get; set; }
    public bool HasLifetimeMismatch { get; set; }
    public bool HasLowIncomingRefs { get; set; }
}

internal class SubscriberInfo
{
    public ulong Address { get; set; }
    public ulong MethodTable { get; set; }
    public string Type { get; set; } = string.Empty;
    /// <summary>Resolved handler method name, e.g. "MyClass.OnDataReceived". Null when not yet resolved or unavailable.</summary>
    public string? MethodName { get; set; }
    /// <summary>Average size (bytes) of this subscriber type on the heap, or this subscriber's
    /// exact retained bytes when <see cref="SubscriberSizeIsExact"/> is true. 0 when unknown.</summary>
    public ulong SubscriberSize { get; set; }
    /// <summary>True when <see cref="SubscriberSize"/> came from the exact dominator tree
    /// (§9, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) rather than
    /// the per-type shallow-size average.</summary>
    public bool SubscriberSizeIsExact { get; set; }
}

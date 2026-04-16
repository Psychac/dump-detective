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
}

internal class SubscriberInfo
{
public ulong Address { get; set; }
public string Type { get; set; } = string.Empty;
}

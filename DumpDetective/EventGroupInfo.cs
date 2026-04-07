namespace DumpDetective
{
    class EventGroupInfo
    {
        public string PublisherType { get; set; } = string.Empty;
        public string EventFieldName { get; set; } = string.Empty;
        public int InstanceCount { get; set; }
        public int TotalSubscribers { get; set; }
        public double AverageSubscribers { get; set; }
        public int MaxSubscribers { get; set; }
        public int MinSubscribers { get; set; }
        public List<EventLeakInfo> Instances { get; set; } = new();
    }
}

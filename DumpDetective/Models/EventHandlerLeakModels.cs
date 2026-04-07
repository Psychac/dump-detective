namespace DumpDetective
{
    internal class EventHandlerLeak
    {
        public ulong PublisherAddress { get; set; }
        public string PublisherType { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public int SubscriberCount { get; set; }
        public List<EventSubscriberDetail> SubscriberDetails { get; set; } = new();
        public ulong TotalRetainedMemory { get; set; }
        public bool IsStaticPublisher { get; set; }
        public bool HasLongLivedSubscribers { get; set; }
    }

    internal class EventSubscriberDetail
    {
        public ulong SubscriberAddress { get; set; }
        public string SubscriberType { get; set; } = string.Empty;
        public ulong Size { get; set; }
        public bool IsStaticRooted { get; set; }
        public string RootDescription { get; set; } = string.Empty;
    }
}

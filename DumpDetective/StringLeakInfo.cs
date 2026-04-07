namespace DumpDetective
{
    class StringLeakInfo
    {
        public string Value { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
    }
}

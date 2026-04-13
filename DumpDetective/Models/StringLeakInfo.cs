namespace DumpDetective.Models
{
    internal class StringLeakInfo
    {
        public string Preview { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
    }
}

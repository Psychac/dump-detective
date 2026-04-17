namespace DumpDetective.Core.Models;

internal sealed class CachedTypeStatistics
{
    public string TypeName { get; set; } = string.Empty;
    public int Count { get; set; }
    public ulong TotalSize { get; set; }
    public int LohCount { get; set; }
    public ulong LohSize { get; set; }
}

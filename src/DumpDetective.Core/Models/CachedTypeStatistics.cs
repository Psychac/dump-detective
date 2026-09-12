namespace DumpDetective.Core.Models;

public sealed class CachedTypeStatistics
{
    public string TypeName { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public int Count { get; set; }
    public ulong TotalSize { get; set; }
    public int LohCount { get; set; }
    public ulong LohSize { get; set; }

    /// <summary>
    /// Added 2026-09-12 for <c>FinalizableObjectAnalyzer</c>'s retyping, so its no-index fallback
    /// (the same shared <see cref="StatisticsCache.GetOrBuildTypeStatistics"/> walk 5 other
    /// analyzers already use) can carry finalizable/per-generation facts too, instead of the SDK
    /// bridge's <c>HeapTypeStatisticsQuery.BuildFromCachedStatistics</c> hardcoding them to
    /// false/0. Only populated by <see cref="StatisticsCache"/>'s full-heap-walk branch (reached
    /// only when no prebuilt index exists); left at their defaults on the index-hydration branch,
    /// which no existing consumer of this type needs generation data from.
    /// </summary>
    public bool IsFinalizableType { get; set; }
    public long Gen0Count { get; set; }
    public long Gen1Count { get; set; }
    public long Gen2Count { get; set; }
}

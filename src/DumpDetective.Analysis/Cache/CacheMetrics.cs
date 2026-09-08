using System;

namespace DumpDetective.Analysis.Cache;

internal class CacheMetrics
{
    public string Name { get; init; } = string.Empty;
    public long? LastBuildDurationMs { get; set; }
    public string LastBuildStatus { get; set; } = "unknown";
    public long EntryCount { get; set; }
    public long MemoryUsageBytes { get; set; }
    public DateTime? LastBuildTime { get; set; }
    public bool IsHealthy { get; set; } = true;
    public string? LastError { get; set; }
}

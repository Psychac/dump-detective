using System;
using System.Collections.Generic;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapCacheHealth
    {
        public IEnumerable<CacheMetrics> Caches { get; init; } = Array.Empty<CacheMetrics>();
        public bool OverallHealthy { get; init; }
        public DateTime CheckedAt { get; init; }
    }
}

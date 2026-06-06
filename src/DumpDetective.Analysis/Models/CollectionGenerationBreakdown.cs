using System.Collections.Generic;

using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Models
{
    /// <summary>
    /// Per-kind, per-generation collection stats.
    /// </summary>
    public sealed record CollectionGenerationStats(
        CollectionKind Kind,
        int Gen0Count,
        int Gen1Count,
        int Gen2Count,
        int LohCount
    );

    /// <summary>
    /// Contract for per-generation breakdown (list of stats records).
    /// </summary>
    public interface ICollectionGenerationBreakdown
    {
        IReadOnlyList<CollectionGenerationStats> Stats { get; }
    }

    /// <summary>
    /// Default implementation for per-generation breakdown.
    /// </summary>
    public class CollectionGenerationBreakdown : ICollectionGenerationBreakdown
    {
        public IReadOnlyList<CollectionGenerationStats> Stats { get; }

        public CollectionGenerationBreakdown(IReadOnlyList<CollectionGenerationStats> stats)
        {
            Stats = stats;
        }
    }
}

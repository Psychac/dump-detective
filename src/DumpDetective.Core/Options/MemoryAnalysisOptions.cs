namespace DumpDetective.Core.Options;

public sealed class MemoryAnalysisOptions
{
    /// <summary>
    /// Minimum object size (bytes) treated as LOH for memory summary metrics. Informational only —
    /// echoed into the report; the actual LOH classification is a fixed constant in
    /// TypeIndexBuilder, matching this default. Never tier-varied.
    /// </summary>
    public ulong LohThresholdBytes { get; init; } = 85_000;
}

namespace DumpDetective.Core.Options;

public sealed class GCGenerationAnalysisOptions
{
    /// <summary>
    /// LOH memory share threshold (%). Only emit LOH Info finding when LOH share exceeds this percentage.
    /// Suppresses noise for healthy dumps where LOH is within expected range. Default 20%.
    /// </summary>
    public double LohThresholdPercent { get; init; } = 20.0;

    /// <summary>
    /// Gen0 allocation pressure threshold (%). Emit Warning when Gen0 objects exceed this % of total objects.
    /// Signals high allocation rate that may degrade GC throughput. Default 40%.
    /// </summary>
    public double Gen0PressureThresholdPercent { get; init; } = 40.0;

    /// <summary>
    /// POH (Pinned Object Heap) memory share threshold (%). Only emit POH Info finding when POH share
    /// exceeds this percentage. POH is a separate heap for pinned objects (.NET 5+). Default 5%.
    /// </summary>
    public double PohThresholdPercent { get; init; } = 5.0;
}

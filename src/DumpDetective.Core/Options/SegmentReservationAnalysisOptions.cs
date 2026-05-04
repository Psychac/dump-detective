namespace DumpDetective.Core.Options;

public sealed class SegmentReservationAnalysisOptions
{
    public ulong ThirtyTwoBitPressureThresholdBytes { get; init; } = 1_500_000_000UL;
    public double RatioHighPressureThreshold { get; init; } = 10.0;
}

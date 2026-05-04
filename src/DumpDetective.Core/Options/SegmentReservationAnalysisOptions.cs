namespace DumpDetective.Core.Options;

public sealed class SegmentReservationAnalysisOptions
{
    public ulong ThirtyTwoBitPressureThresholdBytes { get; init; } = 1_500_000_000UL;
    public double RatioHighPressureThreshold { get; init; } = 10.0;

    public static SegmentReservationAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new SegmentReservationAnalysisOptions { ThirtyTwoBitPressureThresholdBytes = 2_000_000_000UL, RatioHighPressureThreshold = 12.0 },
        AnalysisProfile.Full => new SegmentReservationAnalysisOptions { ThirtyTwoBitPressureThresholdBytes = 1_000_000_000UL, RatioHighPressureThreshold = 8.0 },
        _ => new SegmentReservationAnalysisOptions(),
    };

    public static SegmentReservationAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

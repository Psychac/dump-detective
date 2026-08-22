namespace DumpDetective.Core.Options;

public sealed class SegmentReservationAnalysisOptions
{
    // 1.5 GB — 32-bit VA space caps around 2-4 GB; this is an "approaching the wall" line.
    public ulong ThirtyTwoBitPressureThresholdBytes { get; init; } = 1_500_000_000UL;

    // 10.0x reserved:committed — no external standard behind this number; shaky, revisit once
    // real field data on reservation ratios exists.
    public double RatioHighPressureThreshold { get; init; } = 10.0;
}

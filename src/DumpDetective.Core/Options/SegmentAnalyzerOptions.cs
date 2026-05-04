namespace DumpDetective.Core.Options;

/// <summary>
/// Holds configurable constants for segment analysis and reporting.
/// </summary>
public static class SegmentAnalyzerOptions
{
    // How many top segments to include in the "top by size" summary.
    public const int TopSegmentsCount = 10;

    // Frequency (number of objects) between inner-loop progress reports.
    // Matches previous behavior of reporting roughly every 16k objects.
    public const int ReportObjectScanInterval = 16_384; // 0x4000

    // Reporting thresholds (percent of total committed heap) for LOH warnings.
    public const double LohCriticalPercentThreshold = 40.0; // critical when >= 40%
    public const double LohElevatedPercentThreshold = 20.0; // elevated when >= 20%

    // Spike detection: mark segments whose object density (objects per MiB)
    // exceeds this multiple of the average density among inspected segments.
    public const double SpikeDensityMultiplier = 3.0;
}

namespace DumpDetective.Core.Options;

public sealed class WeakReferenceAnalysisOptions
{
    public int HandleScanCap { get; init; } = 50_000;
    public int TopTypeLimit { get; init; } = 15;
    // When true, produce NDJSON/gz + friendly JSON exports for offline tooling.
    public bool ProduceRawExports { get; init; } = false;

    // Number of WeakReference<T> sample probes to use when approximating stale wrappers.
    // A value of 0 means "no explicit cap" (but implementations may still bound work).
    public int WeakRefProbeSampleLimit { get; init; } = 50;

    // Absolute threshold for dead weak target count. Fires a signal when exceeded,
    // complementary to the ratio-based signal. Default 10,000.
    public int AbsoluteDeadCountThreshold { get; init; } = 10_000;

    public static WeakReferenceAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new WeakReferenceAnalysisOptions
        {
            HandleScanCap = 20_000,
            TopTypeLimit = 8,
            WeakRefProbeSampleLimit = 8,
            ProduceRawExports = false,
            AbsoluteDeadCountThreshold = 5_000,
        },
        AnalysisProfile.Full => new WeakReferenceAnalysisOptions
        {
            HandleScanCap = 200_000,
            TopTypeLimit = 40,
            WeakRefProbeSampleLimit = 500,
            ProduceRawExports = true,
            AbsoluteDeadCountThreshold = 50_000,
        },
        _ => new WeakReferenceAnalysisOptions(),
    };

    public static WeakReferenceAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

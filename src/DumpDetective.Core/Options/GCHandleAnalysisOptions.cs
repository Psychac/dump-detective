namespace DumpDetective.Core.Options;

public sealed class GCHandleAnalysisOptions
{
    public int TopTypeCount { get; init; } = 15;

    /// <summary>Total handle count threshold for warning-level severity (P0-2 hardcoded at 10000).</summary>
    public int TotalHandlesWarningThreshold { get; init; } = 10000;

    /// <summary>Pinned handle target count threshold for warning-level severity (P0-2 hardcoded at 1000).</summary>
    public int PinnedHandleTargetsWarningThreshold { get; init; } = 1000;

    /// <summary>Pinned retained bytes threshold for warning-level severity (new in P1-1, default 100 MB).</summary>
    public ulong PinnedRetainedBytesWarningThreshold { get; init; } = 100 * 1024 * 1024;

    /// <summary>Dependent unresolved target percentage threshold for warning-level severity (P0-2 hardcoded at 50%).</summary>
    public double DependentUnresolvedPercentWarningThreshold { get; init; } = 50.0;

    public static GCHandleAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new GCHandleAnalysisOptions { TopTypeCount = 8 },
        AnalysisProfile.Full => new GCHandleAnalysisOptions { TopTypeCount = 40 },
        _ => new GCHandleAnalysisOptions(),
    };

    public static GCHandleAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

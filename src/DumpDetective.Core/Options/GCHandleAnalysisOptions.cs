namespace DumpDetective.Core.Options;

public sealed class GCHandleAnalysisOptions
{
    /// <summary>Total handle count threshold for warning-level severity.</summary>
    public int TotalHandlesWarningThreshold { get; init; } = 10000;

    /// <summary>Pinned handle target count threshold for warning-level severity.</summary>
    public int PinnedHandleTargetsWarningThreshold { get; init; } = 1000;

    /// <summary>Pinned retained bytes threshold for warning-level severity (default 100 MB).</summary>
    public ulong PinnedRetainedBytesWarningThreshold { get; init; } = 100 * 1024 * 1024;

    /// <summary>Dependent unresolved target percentage threshold for warning-level severity.</summary>
    public double DependentUnresolvedPercentWarningThreshold { get; init; } = 50.0;
}

namespace DumpDetective.Core.Options;

public sealed class GCHandleAnalysisOptions
{
    /// <summary>Total handle count threshold for warning-level severity.</summary>
    public int TotalHandlesWarningThreshold { get; init; } = 10000;

    /// <summary>Pinned handle target count threshold for warning-level severity.</summary>
    public int PinnedHandleTargetsWarningThreshold { get; init; } = 1000;

    /// <summary>Pinned retained bytes threshold for warning-level severity (default 100 MB).</summary>
    public ulong PinnedRetainedBytesWarningThreshold { get; init; } = 100 * 1024 * 1024;

    /// <summary>Combined (Pinned + AsyncPinned) small-object-heap target count threshold for
    /// warning-level severity. SOH-pinned targets block GC compaction; LOH/POH/Frozen targets don't.</summary>
    public int PinnedSohObjectCountWarningThreshold { get; init; } = 500;

    /// <summary>RefCounted (COM interop RCW) handle count threshold for warning-level severity.</summary>
    public int RefCountedHandleCountWarningThreshold { get; init; } = 100;

    /// <summary>Number of individual Pinned/AsyncPinned handle addresses to display, ranked by
    /// retained bytes (P2-4). The full set is still computed exactly; this only bounds the
    /// display-table size, same convention as <c>RetentionOptions.TopHighlyReferencedObjectsToShow</c>.</summary>
    public int TopPinnedHandleAddressesToShow { get; init; } = 25;

    /// <summary>Minimum fraction of resolved WeakLong targets in Gen2/LOH for warning-level
    /// severity (P3-2). WeakLong clears only after finalization completes, so a population
    /// concentrated in Gen2/LOH can indicate a finalization backlog.</summary>
    public double WeakLongGen2FractionWarningThreshold { get; init; } = 70.0;

    /// <summary>Minimum absolute Gen2/LOH WeakLong target count required before
    /// <see cref="WeakLongGen2FractionWarningThreshold"/> is evaluated (P3-2) — avoids noise on
    /// small weak-handle populations.</summary>
    public int WeakLongGen2MinimumCountThreshold { get; init; } = 100;

    /// <summary>Dependent unresolved target percentage threshold for warning-level severity.</summary>
    public double DependentUnresolvedPercentWarningThreshold { get; init; } = 50.0;
}

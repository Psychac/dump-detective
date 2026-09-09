namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// A point in time, expressed in whichever of these an artifact can actually supply — never all
/// three. <see cref="Confidence"/> caps how strongly any alignment or finding built on this anchor
/// may be stated. See docs/refactor/modularity/source-model.md § 5.
/// </summary>
public sealed record TimeAnchor
{
    public DateTime? WallClockUtc { get; init; }
    public TimeSpan? ProcessUptime { get; init; }
    public long? MonotonicTicks { get; init; }
    public required AnchorConfidence Confidence { get; init; }
}

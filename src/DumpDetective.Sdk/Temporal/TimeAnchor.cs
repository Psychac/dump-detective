namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// A point in time, expressed in whichever of these an artifact can actually supply.
/// <see cref="Confidence"/> caps how strongly any alignment or finding built on this anchor may be
/// stated. See docs/refactor/modularity/source-model.md § 5.
/// </summary>
/// <remarks>
/// At least one of <see cref="WallClockUtc"/>/<see cref="ProcessUptime"/>/
/// <see cref="MonotonicTicks"/> must be populated — an anchor with all three null anchors nothing.
/// (An earlier version of this doc also claimed "never all three populated"; dropped 2026-09-10 —
/// having all three isn't actually harmful, just unusual, and enforcing an upper bound wouldn't
/// protect against anything real. See docs/refactor/modularity/phase-1-sdk-review-findings.md item
/// 11.) Not enforced at construction, same reasoning as <see cref="TemporalExtent"/>'s remarks —
/// trusted first-party producers, not external input. Guarded instead by
/// <c>tests/DumpDetective.Tests/Unit/Sdk/TemporalTests.cs</c>.
/// </remarks>
public sealed record TimeAnchor
{
    public DateTime? WallClockUtc { get; init; }
    public TimeSpan? ProcessUptime { get; init; }
    public long? MonotonicTicks { get; init; }
    public required AnchorConfidence Confidence { get; init; }
}

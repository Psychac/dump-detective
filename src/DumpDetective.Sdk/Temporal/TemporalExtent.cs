namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// When something happened or was observed — a single point (dump) or a bounded interval (trace
/// window). See docs/refactor/modularity/source-model.md § 5.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> and <see cref="End"/> must agree: <see cref="TemporalKind.Point"/> means
/// <see cref="End"/> is <c>null</c> (a point has no duration); <see cref="TemporalKind.Interval"/>
/// means <see cref="End"/> is non-null (an interval with no end isn't bounded). Not enforced at
/// construction — every producer of this type is trusted first-party analyzer code, not external
/// input, so this project's convention is to trust that contract rather than add a throw-on-invalid
/// check for a case that can't happen from outside (see CLAUDE.md's "only validate at system
/// boundaries"). Guarded instead by
/// <c>tests/DumpDetective.Tests/Unit/Sdk/TemporalTests.cs</c> characterizing real producers. See
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 11.
/// </remarks>
public sealed record TemporalExtent
{
    public required TemporalKind Kind { get; init; }
    public required TimeAnchor Start { get; init; }
    public TimeAnchor? End { get; init; }
}

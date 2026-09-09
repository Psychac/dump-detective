namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// When something happened or was observed — a single point (dump), a bounded interval (trace
/// window), or an open series (multi-dump trend). See docs/refactor/modularity/source-model.md § 5.
/// </summary>
public sealed record TemporalExtent
{
    public required TemporalKind Kind { get; init; }
    public required TimeAnchor Start { get; init; }
    public TimeAnchor? End { get; init; }
}

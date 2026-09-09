using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// How a <see cref="Measure"/> should be diffed/aggregated across a temporal series. This is what
/// lets a single generic trend differ replace ~30 hand-written comparers — see
/// docs/refactor/modularity/observation-and-correlation-model.md § 5.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeasureSemantics>))]
public enum MeasureSemantics
{
    /// <summary>Diffs as delta and percent change (e.g. retained bytes).</summary>
    Absolute,

    /// <summary>Diffs as rate-of-rate / acceleration (e.g. allocation rate).</summary>
    Rate,

    /// <summary>Diffs as a point difference, never "percent of a percent" (e.g. gen2 fraction).</summary>
    Ratio,

    /// <summary>Diffs as delta plus growth-curve fit across ≥ 3 anchors (e.g. instance count).</summary>
    Count,

    /// <summary>Diffs as delta plus distribution shift (e.g. GC pause duration).</summary>
    Duration,
}

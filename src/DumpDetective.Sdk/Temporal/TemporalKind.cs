using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// Shape of a <see cref="TemporalExtent"/>: a dump is a point, a trace is an interval, a
/// multi-dump sequence is a sparse series. See docs/refactor/modularity/source-model.md § 5.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TemporalKind>))]
public enum TemporalKind
{
    Point,
    Interval,
    Series,
}

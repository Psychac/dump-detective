using System.Text.Json.Serialization;

namespace DumpDetective.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<MetricUnit>))]
public enum MetricUnit
{
    Count,
    Bytes,
    Percent,
    Ratio,
    Milliseconds,
    Custom
}

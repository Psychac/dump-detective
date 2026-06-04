using System.Text.Json.Serialization;

namespace DumpDetective.Core.Models;

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

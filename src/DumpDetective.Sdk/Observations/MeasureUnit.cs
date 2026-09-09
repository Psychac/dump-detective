using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Observations;

[JsonConverter(typeof(JsonStringEnumConverter<MeasureUnit>))]
public enum MeasureUnit
{
    Count,
    Bytes,
    Percent,
    Ratio,
    Milliseconds,
    PerSecond,
    Custom,
}

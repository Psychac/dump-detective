namespace DumpDetective.Core.Options;

public sealed class ObjectShapeAnalysisOptions
{
    public int InstanceCountCap { get; init; } = 200;
    public int TopListLimit { get; init; } = 20;
}

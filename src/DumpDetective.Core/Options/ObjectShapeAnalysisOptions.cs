namespace DumpDetective.Core.Options;

public sealed class ObjectShapeAnalysisOptions
{
    public int InstanceCountCap { get; init; } = 200;
    public int TopListLimit { get; init; } = 20;

    public static ObjectShapeAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ObjectShapeAnalysisOptions { InstanceCountCap = 100, TopListLimit = 10 },
        AnalysisProfile.Full => new ObjectShapeAnalysisOptions { InstanceCountCap = 1_000, TopListLimit = 50 },
        _ => new ObjectShapeAnalysisOptions(),
    };

    public static ObjectShapeAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

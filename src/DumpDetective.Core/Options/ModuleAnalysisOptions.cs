namespace DumpDetective.Core.Options;

public sealed class ModuleAnalysisOptions
{
    // How many largest loaded assemblies to include in domain snapshots
    public int TopLoadedAssembliesCount { get; init; } = 30;

    // How many top modules by heap memory to return
    public int TopModulesByHeapCount { get; init; } = 20;

    // Threshold to consider a module "heavy" for finding severity (bytes)
    public ulong HeavyModuleWarningThresholdBytes { get; init; } = 200UL * 1024UL * 1024UL;

    // Threshold for density anomaly detection (minimum bytes)
    public ulong DensityAnomalyMinBytes { get; init; } = 50UL * 1024UL * 1024UL;

    // Maximum number of unique types for a module to be considered "dense"
    public int DensityAnomalyMaxTypes { get; init; } = 5;

    public static ModuleAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ModuleAnalysisOptions { TopLoadedAssembliesCount = 15, TopModulesByHeapCount = 10, HeavyModuleWarningThresholdBytes = 300UL * 1024UL * 1024UL, DensityAnomalyMinBytes = 100UL * 1024UL * 1024UL, DensityAnomalyMaxTypes = 3 },
        AnalysisProfile.Full => new ModuleAnalysisOptions { TopLoadedAssembliesCount = 80, TopModulesByHeapCount = 50, HeavyModuleWarningThresholdBytes = 100UL * 1024UL * 1024UL, DensityAnomalyMinBytes = 20UL * 1024UL * 1024UL, DensityAnomalyMaxTypes = 10 },
        _ => new ModuleAnalysisOptions(),
    };

    public static ModuleAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

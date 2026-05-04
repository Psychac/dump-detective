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

    public static ModuleAnalysisOptions Default { get; } = new();
}

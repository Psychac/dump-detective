namespace DumpDetective.Core.Options;

public sealed class ModuleAnalysisOptions
{
    // Threshold to consider a module "heavy" for finding severity (bytes)
    public ulong HeavyModuleWarningThresholdBytes { get; init; } = 200UL * 1024UL * 1024UL;

    // Threshold for density anomaly detection (minimum bytes)
    public ulong DensityAnomalyMinBytes { get; init; } = 50UL * 1024UL * 1024UL;

    // Maximum number of unique types for a module to be considered "dense"
    public int DensityAnomalyMaxTypes { get; init; } = 5;

    // Maximum number of modules whose raw metadata is read to audit AssemblyRef version
    // requirements against what's actually loaded. Bounds the cost of the metadata-blob reads on
    // dumps with thousands of modules; excess modules are skipped and a warning is emitted.
    public int MaxModulesForAssemblyRefAudit { get; init; } = 1000;
}

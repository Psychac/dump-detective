namespace DumpDetective.Core.Options;

public enum ModuleSelectionMode
{
    TopBySize,
    TopByTypeCount
}

public enum TypeEnumerationMode
{
    Full,
    Sampled,
    Skip
}

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

    /// <summary>
    /// Maximum modules per domain whose types are enumerated.
    /// </summary>
    public int ModuleEnumerationLimit { get; init; } = 50;

    /// <summary>
    /// Maximum entries in top modules-by-type-count output.
    /// </summary>
    public int TopModuleTypeCountLimit { get; init; } = 20;

    /// <summary>
    /// How to select modules when there are more modules than the enumeration limit.
    /// </summary>
    public ModuleSelectionMode ModuleSelectionMode { get; init; } = ModuleSelectionMode.TopBySize;

    /// <summary>
    /// How aggressively to enumerate type->method-table mappings for selected modules.
    /// </summary>
    public TypeEnumerationMode TypeEnumerationMode { get; init; } = TypeEnumerationMode.Full;

    /// <summary>
    /// When true, prefer index-only behavior: skip expensive enumeration when no index is present.
    /// </summary>
    public bool PreferIndexOnly { get; init; } = true;

    /// <summary>
    /// When true, include a small summary/metric about excluded modules (counts/sizes) in results.
    /// </summary>
    public bool IncludeExcludedModuleSummary { get; init; } = false;

    /// <summary>
    /// Emit a non-fatal warning when module enumeration was truncated by `ModuleEnumerationLimit`.
    /// </summary>
    public bool EmitTruncationNotice { get; init; } = false;

    public static ModuleAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ModuleAnalysisOptions
        {
            TopLoadedAssembliesCount = 15,
            TopModulesByHeapCount = 10,
            HeavyModuleWarningThresholdBytes = 300UL * 1024UL * 1024UL,
            DensityAnomalyMinBytes = 100UL * 1024UL * 1024UL,
            DensityAnomalyMaxTypes = 3,
            ModuleEnumerationLimit = 25,
            TopModuleTypeCountLimit = 10,
            ModuleSelectionMode = ModuleSelectionMode.TopBySize,
            TypeEnumerationMode = TypeEnumerationMode.Sampled,
            PreferIndexOnly = true,
            IncludeExcludedModuleSummary = false,
            EmitTruncationNotice = true,
        },
        AnalysisProfile.Full => new ModuleAnalysisOptions
        {
            TopLoadedAssembliesCount = 80,
            TopModulesByHeapCount = 50,
            HeavyModuleWarningThresholdBytes = 100UL * 1024UL * 1024UL,
            DensityAnomalyMinBytes = 20UL * 1024UL * 1024UL,
            DensityAnomalyMaxTypes = 10,
            // could increase these limits for full, but the analysis time grows quickly with the number of modules, so keeping them moderate and relying on PreferIndexOnly to skip enumeration when no index is present
            ModuleEnumerationLimit = 100,
            TopModuleTypeCountLimit = 40,
            ModuleSelectionMode = ModuleSelectionMode.TopByTypeCount,
            TypeEnumerationMode = TypeEnumerationMode.Full,
            PreferIndexOnly = false,
            IncludeExcludedModuleSummary = true,
            EmitTruncationNotice = true,
        },
        _ => new ModuleAnalysisOptions
        {
            // Balanced
            ModuleEnumerationLimit = 50,
            TopModuleTypeCountLimit = 20,
            ModuleSelectionMode = ModuleSelectionMode.TopBySize,
            TypeEnumerationMode = TypeEnumerationMode.Full,
            PreferIndexOnly = false,
            IncludeExcludedModuleSummary = true,
            EmitTruncationNotice = true,
        },
    };

    public static ModuleAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

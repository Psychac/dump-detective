namespace DumpDetective.Core.Options;

public enum ModuleSelectionMode
{
    TopBySize,
    TopByTypeCount,
    StratifiedSample
}

public enum TypeEnumerationMode
{
    Full,
    Sampled,
    Skip
}

/// <summary>
/// Configurable limits for <c>AppDomainAnalyzer</c>.
/// </summary>
public sealed class AppDomainAnalysisOptions
{
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

    public static AppDomainAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AppDomainAnalysisOptions
        {
            ModuleEnumerationLimit = 25,
            TopModuleTypeCountLimit = 10,
            ModuleSelectionMode = ModuleSelectionMode.TopBySize,
            TypeEnumerationMode = TypeEnumerationMode.Sampled,
            PreferIndexOnly = true,
            IncludeExcludedModuleSummary = false,
            EmitTruncationNotice = true,
        },
        AnalysisProfile.Full => new AppDomainAnalysisOptions
        {
            // could increase these limits for full, but the analysis time grows quickly with the number of modules, so keeping them moderate and relying on PreferIndexOnly to skip enumeration when no index is present
            ModuleEnumerationLimit = 100,
            TopModuleTypeCountLimit = 40,
            ModuleSelectionMode = ModuleSelectionMode.TopByTypeCount,
            TypeEnumerationMode = TypeEnumerationMode.Full,
            PreferIndexOnly = false,
            IncludeExcludedModuleSummary = true,
            EmitTruncationNotice = true,
        },
        _ => new AppDomainAnalysisOptions
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

    public static AppDomainAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

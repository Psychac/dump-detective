namespace DumpDetective.Core.Options;

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

    public static AppDomainAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AppDomainAnalysisOptions { ModuleEnumerationLimit = 25, TopModuleTypeCountLimit = 10 },
        AnalysisProfile.Full => new AppDomainAnalysisOptions { ModuleEnumerationLimit = 100, TopModuleTypeCountLimit = 40 },
        _ => new AppDomainAnalysisOptions(),
    };

    public static AppDomainAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}

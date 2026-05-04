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
}

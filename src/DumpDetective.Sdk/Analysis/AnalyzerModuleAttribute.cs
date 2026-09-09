namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Declares an analyzer's discovery metadata — see
/// docs/refactor/modularity/phase-3-plugin-packaging.md's discovery-by-attribute design, which
/// replaces the hand-maintained <c>DefaultAnalyzerFeatureModuleCatalog</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AnalyzerModuleAttribute(string key, string displayName, int order, string[]? tags = null) : Attribute
{
    public string Key { get; } = key;

    public string DisplayName { get; } = displayName;

    public int Order { get; } = order;

    public IReadOnlyList<string> Tags { get; } = tags ?? [];
}

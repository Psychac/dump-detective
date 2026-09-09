using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Declares the capabilities an analyzer cannot run without — see
/// docs/refactor/modularity/phase-3-plugin-packaging.md's discovery-by-attribute design.
/// Capability strings should be members of <see cref="CapabilityVocabulary"/>; validated against
/// the checked-in <c>capability-registry.json</c> at discovery time once Phase 3's
/// `PluginCatalogBuilder` exists, not by this attribute itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresCapabilityAttribute(params string[] capabilities) : Attribute
{
    public IReadOnlyList<string> Capabilities { get; } = capabilities;
}

namespace DumpDetective.Sdk.Observations;

/// <summary>
/// <see cref="EvidenceRef.Locator"/> values the report reads as details of the entity an observation names, as opposed to
/// pointing at evidence to inspect. An entity's identity never includes a detail: a module is its file name, and the folder it
/// was loaded from differs between machines.
/// </summary>
public static class EntityDetailLocators
{
    /// <summary>The full path a module was loaded from; the description holds the path.</summary>
    public const string ModulePath = "module-path";
}

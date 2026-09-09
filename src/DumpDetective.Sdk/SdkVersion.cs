namespace DumpDetective.Sdk;

/// <summary>
/// Version of the source-neutral SDK contract surface (identity, capability, observation,
/// synthesis). Bumped on any breaking change to a type in this assembly, independent of the
/// product's own version — plugins and schema files pin against this, not against a product
/// release. See docs/refactor/modularity/phase-1-contracts-sdk.md.
/// </summary>
public static class SdkVersion
{
    public const int Major = 0;
    public const int Minor = 1;

    public static string AsString => $"{Major}.{Minor}";
}

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// A single loaded module's raw, report-facing facts — deliberately not keyed off
/// <see cref="Identity.ModuleRef"/> (a canonical cross-source join key), the same split
/// <see cref="HeapObjectRef.TypeDisplayName"/> vs. <see cref="Identity.TypeRef.CanonicalName"/>
/// already established: a module-inventory report needs the exact display name/path, not a
/// join-key identity. <see cref="Address"/> is this dump's own <c>ClrModule</c> address — a
/// process-local handle, not part of any cross-source identity.
/// </summary>
public readonly record struct RuntimeModuleInfo(
    ulong Address,
    /// <summary>File name only (<c>Path.GetFileName</c>), mirroring the pre-retyping analyzer's
    /// own display convention.</summary>
    string Name,
    /// <summary>Full path as ClrMD reports it, unresolved further.</summary>
    string FullPath,
    string AssemblyName,
    ulong Size,
    bool IsDynamic,
    bool IsPEFile,
    /// <summary>The <c>ClrAppDomain.Id</c> this module belongs to. Every module belongs to exactly
    /// one <c>ClrAppDomain</c> — a module of the same name loaded into two domains is two distinct
    /// <c>ClrModule</c> instances (two distinct <see cref="Address"/> values), not one module with
    /// two domain memberships. See <see cref="IRuntimeModuleQuery.EnumerateAppDomains"/>'s own
    /// remarks for why this is still a separate, non-deduplicated stream from
    /// <see cref="IRuntimeModuleQuery.EnumerateModules()"/>.</summary>
    int AppDomainId);

/// <summary>An AppDomain's own identity/facts — no separate join-key type exists for this (nothing
/// outside dump sources has ever modeled AppDomains), so identity and facts live in one shape, the
/// same as <see cref="HeapSegmentRef"/>/<see cref="RuntimeThreadRef"/>.</summary>
public readonly record struct AppDomainInfo(int DomainId, string Name, ulong Address);

/// <summary>The <c>runtime.modules</c> capability.</summary>
/// <remarks>
/// Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming surface.
///
/// Redesigned, not just implemented, for <c>ModuleAnalyzer</c>'s retyping (2026-09-13) — the shipped
/// shape (<c>EnumerateModules() -&gt; IEnumerable&lt;ModuleRef&gt;</c>) had zero real consumers and
/// carried only an identity/join key, not the module facts (path, size, dynamic/PE flags, assembly
/// name, owning AppDomain) a report needs. <see cref="EnumerateModules()"/> mirrors the pre-retyping
/// analyzer's own <c>runtime.EnumerateModules()</c> walk exactly (address-deduplicated across the
/// whole process — a module loaded into two AppDomains still appears as two distinct addresses,
/// so this is not a name-deduplicated view either); <see cref="EnumerateModules(AppDomainInfo)"/> is
/// a deliberately separate, non-deduplicated per-domain walk (mirrors <c>ClrAppDomain.Modules</c>)
/// — kept as two capability methods rather than one, matching the pre-retyping analyzer's own two
/// separate walks exactly, since collapsing them into one deduplicated stream would silently drop
/// the cross-domain-load signal the second walk exists to catch.
/// </remarks>
public interface IRuntimeModuleQuery
{
    /// <summary>Every distinct loaded module in the process, deduplicated by address.</summary>
    IEnumerable<RuntimeModuleInfo> EnumerateModules();

    IEnumerable<AppDomainInfo> EnumerateAppDomains();

    /// <summary>Every module loaded into <paramref name="domain"/>, not deduplicated against other
    /// domains' modules — see this interface's own remarks for why.</summary>
    IEnumerable<RuntimeModuleInfo> EnumerateModules(AppDomainInfo domain);

    /// <summary>
    /// Display names of every type declared in the module at <paramref name="moduleAddress"/>
    /// (mirrors <c>ClrModule.EnumerateTypeDefToMethodTableMap()</c>, resolved to names) — includes
    /// types with zero live heap instances, which <see cref="IHeapTypeStatisticsQuery"/> (an
    /// observed-instance-only surface) can't enumerate. A method table that fails to resolve to a
    /// <c>ClrType</c> is skipped, same as the pre-retyping analyzer's own defensive check.
    /// </summary>
    IEnumerable<string> EnumerateDeclaredTypeNames(ulong moduleAddress);

    /// <summary>
    /// The <c>AssemblyLoadContext</c> address/collectible-flag of the first resolvable declared
    /// type in the module at <paramref name="moduleAddress"/> — mirrors the pre-retyping analyzer's
    /// own <c>TryResolveAssemblyLoadContext</c> (every type in a module shares the same load
    /// context, so checking one is sufficient). Returns <c>false</c> when no declared type resolves
    /// or the resolved <c>AssemblyLoadContextAddress</c> is 0 (the default, non-custom load context).
    /// The load context's own display name is a Tier-2 concern — resolve it via
    /// <see cref="IDumpObjectFieldQuery.TryReadField"/>'s <c>"_name"</c> field on
    /// <paramref name="alcAddress"/>, not here.
    /// </summary>
    bool TryResolveAssemblyLoadContext(ulong moduleAddress, out ulong alcAddress, out bool isCollectible);

    /// <summary>
    /// Every AssemblyRef table entry (name, version) parsed directly out of the module's raw
    /// ECMA-335 metadata region — dump-only, no field-value read involved (this is PE/metadata
    /// structure, not a managed object), so it lives on this capability rather than
    /// <see cref="IDumpObjectFieldQuery"/>. Empty for a dynamic module, an unparseable/corrupt
    /// metadata blob, or a module address this query doesn't know about.
    /// </summary>
    IReadOnlyList<(string Name, string Version)> GetAssemblyReferences(ulong moduleAddress);
}

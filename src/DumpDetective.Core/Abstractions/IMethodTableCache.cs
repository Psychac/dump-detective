using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Provides cached <see cref="ClrType"/> lookups by MethodTable address.
/// Resolving a <see cref="ClrType"/> from a raw MethodTable is an expensive ClrMD
/// operation; implementations must cache results so each MethodTable is resolved
/// at most once per analysis session.
/// </summary>
public interface IMethodTableCache
{
    /// <summary>
    /// Returns the <see cref="ClrType"/> for the given <paramref name="methodTable"/>
    /// address, or <c>null</c> if the type cannot be resolved.
    /// Repeated calls with the same address return the cached result without
    /// re-querying the runtime.
    /// </summary>
    ClrType? GetTypeByMethodTable(ulong methodTable);

    /// <summary>Total number of distinct MethodTable addresses cached so far.</summary>
    int CachedTypeCount { get; }
}

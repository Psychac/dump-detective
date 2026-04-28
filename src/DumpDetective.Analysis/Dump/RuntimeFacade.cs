using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Dump;

/// <summary>
/// Wraps a loaded <see cref="ClrRuntime"/> and <see cref="ClrHeap"/> pair and provides
/// cached, safe access to ClrMD metadata on behalf of all analyzers in a single session.
/// </summary>
/// <remarks>
/// <para>
/// <strong>MethodTable cache:</strong> resolving a <see cref="ClrType"/> from a raw
/// MethodTable address via <c>ClrHeap.GetTypeByMethodTable</c> is an expensive ClrMD
/// operation. <see cref="RuntimeFacade"/> caches each resolved type so every MethodTable
/// is looked up at most once per analysis session, regardless of how many analyzers
/// query the same type.
/// </para>
/// <para>
/// <strong>Thread safety:</strong> the MethodTable cache uses <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <see cref="ClrHeap"/> and <see cref="ClrRuntime"/> themselves are NOT thread-safe for
/// concurrent heap enumeration — callers must not enumerate the heap from multiple threads
/// simultaneously.
/// </para>
/// </remarks>
internal sealed class RuntimeFacade : IMethodTableCache
{
    // Sentinel value stored in the dictionary when a MethodTable resolves to null,
    // so we don't re-query ClrMD on repeated lookups for unresolvable types.
    private static readonly ClrType? s_nullSentinel = null;

    private readonly ClrHeap _heap;
    private readonly ConcurrentDictionary<ulong, ClrType?> _typeCache = new();

    public RuntimeFacade(ClrRuntime runtime, ClrHeap heap)
    {
        Runtime = runtime;
        _heap = heap;
    }

    /// <summary>The underlying <see cref="ClrRuntime"/> for this analysis session.</summary>
    public ClrRuntime Runtime { get; }

    /// <summary>The managed heap associated with <see cref="Runtime"/>.</summary>
    public ClrHeap Heap => _heap;

    /// <inheritdoc/>
    public int CachedTypeCount => _typeCache.Count;

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <c>ClrHeap.GetTypeByMethodTable</c> on first access and caches the result
    /// (including <c>null</c> for unresolvable tables) so subsequent calls are O(1) dictionary
    /// lookups with no ClrMD round-trip.
    /// </remarks>
    public ClrType? GetTypeByMethodTable(ulong methodTable)
    {
        if (methodTable == 0)
            return null;

        // GetOrAdd avoids the double-checked locking pattern and is safe for concurrent reads.
        // The factory may be invoked more than once under high contention but the cost is
        // bounded: each invocation is one ClrMD MethodTable resolution, not a heap scan.
        return _typeCache.GetOrAdd(methodTable, mt => _heap.GetTypeByMethodTable(mt));
    }

    /// <summary>
    /// Returns the display name for the given <paramref name="methodTable"/>,
    /// or <c>"&lt;unknown&gt;"</c> if the type cannot be resolved.
    /// Backed by <see cref="GetTypeByMethodTable"/> so results are cached.
    /// </summary>
    public string GetTypeName(ulong methodTable)
        => GetTypeByMethodTable(methodTable)?.Name ?? "<unknown>";

    /// <summary>
    /// Clears the internal MethodTable → <see cref="ClrType"/> cache.
    /// Only needed in long-lived scenarios where the runtime is reused across
    /// multiple independent analysis passes.
    /// </summary>
    public void ClearCache() => _typeCache.Clear();
}

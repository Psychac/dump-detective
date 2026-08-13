using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Cache;

internal class TypeMetadataCache
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;
    private readonly MethodTableCache? _methodTableCache;
    private readonly ConcurrentDictionary<ulong, TypeMetadata> _cache = new ConcurrentDictionary<ulong, TypeMetadata>();

    // Observability counters
    private long _cacheHits;
    private long _cacheMisses;
    private long _extractErrors;
    private DateTime? _lastExtractTime;

    public TypeMetadataCache(Func<HeapIndexBuildResult?> getHeapIndex, MethodTableCache? methodTableCache = null)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
        _methodTableCache = methodTableCache;
    }

    public bool TryGet(ulong methodTable, out TypeMetadata metadata)
    {
        if (methodTable == 0)
        {
            metadata = default;
            return false;
        }

        bool found = _cache.TryGetValue(methodTable, out metadata!);
        if (found) System.Threading.Interlocked.Increment(ref _cacheHits);
        else System.Threading.Interlocked.Increment(ref _cacheMisses);
        return found;
    }

    public TypeMetadata GetOrCreate(ClrHeap heap, ulong methodTable)
    {
        if (methodTable == 0)
            throw new ArgumentOutOfRangeException(nameof(methodTable));

        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        // Fast-path: return if present
        if (_cache.TryGetValue(methodTable, out var existing))
            return existing;

        // Otherwise, extract and add
        var metadata = ExtractFromClrMd(heap, methodTable);
        _cache.TryAdd(methodTable, metadata);
        return metadata;
    }

    public void Clear() => _cache.Clear();

    /// <summary>
    /// Back-compat convenience: determine whether the method table has outgoing references.
    /// Preserves behaviour where methodTable == 0 returns false without requiring a heap.
    /// Exceptions from extraction are propagated.
    /// </summary>
    public bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable)
    {
        if (methodTable == 0)
            return false;

        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (TryGet(methodTable, out var metadata))
            return metadata.ContainsPointers;

        var md = GetOrCreate(heap, methodTable);
        return md.ContainsPointers;
    }

    private TypeMetadata ExtractFromClrMd(ClrHeap heap, ulong methodTable)
    {
        try
        {
            // Fast path: use prebuilt index sample address when available
            var built = _getHeapIndex();
            if (built?.TypeAggregates is IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates
                && aggregates.TryGetValue(methodTable, out var aggregate)
                && aggregate.SampleAddress != 0)
            {
                var sample = heap.GetObject(aggregate.SampleAddress);
                if (sample.IsValid && sample.Type is not null)
                {
                    _lastExtractTime = DateTime.UtcNow;
                    return ConvertClrTypeToMetadata(sample.Type, methodTable);
                }
            }

            // Fallback: resolve via MethodTableCache or ClrHeap
            var type = _methodTableCache?.GetTypeByMethodTable(heap, methodTable) ?? heap.GetTypeByMethodTable(methodTable);
            if (type is not null)
            {
                _lastExtractTime = DateTime.UtcNow;
                return ConvertClrTypeToMetadata(type, methodTable);
            }

            // If we reached here, the type couldn't be resolved. Treat as conservative: assume contains pointers.
            _lastExtractTime = DateTime.UtcNow;
            return new TypeMetadata(methodTable, containsPointers: true, isArray: false, arrayContainsPointers: false, isString: false, isDelegate: false, isException: false, isFreeObject: false, instanceSize: 0, referenceFieldOffsets: ImmutableArray<int>.Empty);
        }
        catch (Exception)
        {
            System.Threading.Interlocked.Increment(ref _extractErrors);
            // per user instruction, rethrow extraction errors
            throw;
        }
    }

    private static TypeMetadata ConvertClrTypeToMetadata(ClrType type, ulong methodTable)
    {
        bool isArray = type.IsArray;
        bool arrayContainsPointers = false;
        bool containsPointers = false;
        var offsets = ImmutableArray.CreateBuilder<int>();

        if (isArray)
        {
            arrayContainsPointers = type.ComponentType?.IsObjectReference == true;
            containsPointers = arrayContainsPointers;
        }
        else
        {
            foreach (var field in type.Fields)
            {
                if (field.IsObjectReference)
                {
                    containsPointers = true;
                    offsets.Add(field.Offset);
                }
                else if (field.ElementType == ClrElementType.Struct && field.Type is ClrType nestedType
                    && FieldTreeContainsPointers(nestedType, depth: 1))
                {
                    // A struct-typed field with no top-level reference field of its own can still
                    // hold a reference underneath (e.g. `struct Entry { string Key; }` embedded in
                    // a wrapper class). EnumerateReferences walks into these, so ContainsPointers
                    // must too, or callers relying on it (e.g. RetainedSizeCandidateSelector.RequiresWalk)
                    // would wrongly treat the wrapper's shallow size as its full retained size.
                    containsPointers = true;
                }
            }
        }

        int instanceSize = (int)(type.StaticSize);

        bool isString = type.Name is not null && type.Name.Equals("System.String", StringComparison.Ordinal);
        bool isDelegate = TypeFilterHelper.IsDelegateType(type);
        bool isException = type.IsException;
        bool isFreeObject = false; // ClrMD provides free-object checks on instances, not types

        return new TypeMetadata(
            methodTable: methodTable,
            containsPointers: containsPointers,
            isArray: isArray,
            arrayContainsPointers: arrayContainsPointers,
            isString: isString,
            isDelegate: isDelegate,
            isException: isException,
            isFreeObject: isFreeObject,
            instanceSize: instanceSize,
            referenceFieldOffsets: offsets.ToImmutable());
    }

    // Value types cannot be self-referential (directly or transitively) — the CLR rejects such
    // a struct at load time — so this recursion always terminates. The depth cap is a defensive
    // guard rail, not a correctness requirement.
    private const int MaxFieldRecursionDepth = 32;

    private static bool FieldTreeContainsPointers(ClrType structType, int depth)
    {
        if (depth >= MaxFieldRecursionDepth)
            return false;

        foreach (var field in structType.Fields)
        {
            if (field.IsObjectReference)
                return true;

            if (field.ElementType == ClrElementType.Struct && field.Type is ClrType nestedType
                && FieldTreeContainsPointers(nestedType, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(TypeMetadataCache),
            LastBuildDurationMs = null,
            LastBuildStatus = "success",
            EntryCount = _cache.Count,
            MemoryUsageBytes = 0,
            LastBuildTime = _lastExtractTime,
            IsHealthy = true,
            LastError = _extractErrors > 0 ? "extract errors occurred" : null
        };
    }
}

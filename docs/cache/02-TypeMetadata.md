# Implementation Specification – Type Metadata Cache

## Background

`MethodTableHasOutgoingRefs` and related callers repeatedly resolve static type metadata from ClrMD. This causes duplicate work and latency; a small immutable cache keyed by MethodTable reduces cost and stabilizes behavior across analyzers.

## Goals

- Cache immutable type information once per `MethodTable`.
- Keep cache records small and copyable; safe for concurrent readers.
- Provide a simple API for fast lookups in hot paths.

## Non-Goals

- Cache full `ClrType` instances.
- Cache object instances or per-object mutable state.
- Track runtime-mutable metadata (GC generation, pinned state, etc.).

## Data Structure

TypeMetadata (immutable)

- `ulong MethodTable`
- `bool ContainsPointers` — whether instances of this type contain managed reference fields.
- `bool IsArray`
- `bool ArrayContainsPointers` — for arrays, whether the element type contains pointers.
- `bool IsString`
- `bool IsDelegate`
- `bool IsException`
- `bool IsFreeObject`
- `int InstanceSize` — canonical instance size (use `int` unless very large heaps require `long`).
- `int ReferenceFieldCount`
- `ImmutableArray<int> ReferenceFieldOffsets` — byte offsets relative to object start.

Notes:
- Keep the structure compact; prefer value types (`readonly struct`) for hot-path copies.

## Public API

- `bool TryGet(ulong methodTable, out TypeMetadata metadata)`
- `TypeMetadata GetOrCreate(ulong methodTable)`
- `void Clear()` — testing/diagnostics only

Design notes:
- `TryGet` must be extremely cheap and allocation-free when possible.
- `GetOrCreate` is responsible for extracting metadata using ClrMD when missing; it may allocate transient objects while building the immutable record.

## Algorithm

1. Call `TryGet(methodTable)`.
2. If present, return it to caller.
3. Otherwise, call `GetOrCreate(methodTable)` which:
	 - Validates the `methodTable` value.
	 - Uses ClrMD to locate `ClrType` (or other metadata) exactly once.
	 - Extracts required fields (contains pointers, field offsets, instance size, array element info, flags).
	 - Builds an immutable `TypeMetadata` record.
	 - Inserts the record into the cache in a thread-safe, idempotent way (double-checked lock or concurrent dictionary `GetOrAdd`).
4. Return the stored record.

## ClrMD Notes and Best Practices

- Resolve metadata from `ClrType` only during creation — do not retain `ClrType` references.
- Cache derived values (e.g. `ArrayContainsPointers`) so callers don't need to query element types repeatedly.
- Be defensive: ClrMD queries can fail on trimmed or partially-corrupted dumps; treat missing information as "unknown" and record conservative defaults.

## Consumers

- Event analyzer
- WeakReference analyzer
- WCF analyzer
- Graph builders and root-path finders
- Heap traversal helpers and other hot-path code

Note on current usage:

- Implementation note: at the time of writing, many consumers still call the lightweight predicate-style API (`MethodTableHasOutgoingRefs`) rather than the richer `GetOrCreate` pathway. This preserves a fast, conservative check for pointer-containing types but means callers do not receive full `TypeMetadata` details.
- Recommended migration: callers that need richer information (e.g. `ReferenceFieldOffsets`, `IsArray`, `IsDelegate`, or `InstanceSize`) should be updated to call `GetOrCreate(heap, methodTable)` so the extraction is performed once and the full immutable `TypeMetadata` can be reused. Migrating avoids duplicate ClrMD work and unlocks more accurate, stable behavior across analyzers.

## Edge Cases and Caveats

- Invalid `MethodTable`: treat as cache miss and return a conservative `TypeMetadata` (e.g., `ContainsPointers = true`) or throw based on caller expectations.
- Missing/partial metadata from ClrMD: record a conservative fallback and emit a diagnostic counter/metric.
- Free objects and short-lived objects: detect `IsFreeObject` via the usual ClrMD checks and surface it in metadata.
- Arrays: arrays require inspecting element type; for large nested arrays ensure extraction is bounded (avoid deep recursion).
- Generic types: method table identity is already canonical for a runtime type, but some runtimes may canonicalize generics differently — validate against live ClrMD behavior.

## Performance & Concurrency

- Use `ConcurrentDictionary<ulong, TypeMetadata>` or a read-optimized lock-free cache for lookups.
- Optimize `TryGet` to be allocation-free (no boxing, no new arrays).
- Metadata extraction (`GetOrCreate`) can be slower; run it outside of tight loops when possible.
- Consider a small fixed-size LRU or TTL for very long-running processes to avoid unbounded memory growth in pathological workloads. For most analysis scenarios, caching all observed method tables is acceptable.

## Memory & Size Considerations

- TypeMetadata should be compact (a few dozen bytes). Prefer `readonly struct` and `ImmutableArray<int>` for offsets.
- Avoid storing large strings (type names); store lightweight type IDs if persistent naming is needed.

## Observability

- Emit counters:
	- `typeMetadata.cache.hits`
	- `typeMetadata.cache.misses`
	- `typeMetadata.extract.errors`
- Log warnings when ClrMD fails to provide expected fields; include the `methodTable` value.

## Error Handling and Robustness

- Swallow ClrMD transient errors in `GetOrCreate` and record a conservative metadata entry rather than bubbling exceptions into hot paths.
- Surface a distinct error path for fatal issues (critical corruption) so callers can abort if necessary.

## Testing

- Unit tests that exercise:
	- Normal metadata extraction for primitive, reference, and array types.
	- Behavior on invalid/missing method tables.
	- Concurrency: many threads calling `GetOrCreate` for the same `methodTable` concurrently.
	- Memory usage/retention smoke tests.

## Suggested Improvements

- Add an optional diagnostic mode that stores type name -> methodTable mapping for human-readable logs (disabled by default).
- Consider storing a compact Bloom filter of pointer-containing types when scanning very large heaps to speed certain heuristics.
- If analysis runs across multiple dumps, persist a small on-disk cache mapped by runtime id to warm subsequent runs.

- Migration note: update analyzers and hot-path callers from `MethodTableHasOutgoingRefs` to `GetOrCreate` when they require more than a boolean pointer-presence check. This reduces redundant ClrMD queries and centralizes extraction logic in the cache.

## Implementation Example (pseudo-C#)

```csharp
private readonly ConcurrentDictionary<ulong, TypeMetadata> _cache = new();

public bool TryGet(ulong methodTable, out TypeMetadata metadata) => _cache.TryGetValue(methodTable, out metadata);

public TypeMetadata GetOrCreate(ulong methodTable)
{
		return _cache.GetOrAdd(methodTable, mt => ExtractFromClrMd(mt));
}

private TypeMetadata ExtractFromClrMd(ulong methodTable)
{
		// Use ClrMD carefully; return conservative defaults on failure.
}
```

---

End of specification.

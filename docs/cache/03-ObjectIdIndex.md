
# Implementation Specification – Dense ObjectId Index

## Background

Current indexes are address-centric. Future graph algorithms and compact in-memory data structures benefit from dense, small integer identifiers instead of sparse pointer-sized addresses.

## Goals

- Assign every indexed object a stable, dense `ObjectId` (uint) for the lifetime of an index build and persisted index files.
- Provide O(1) mappings both directions: address -> ObjectId and ObjectId -> HeapEntry.
- Preserve existing address-based APIs; add non-breaking helpers for ObjectId access.

## Non-Goals

- Replace addresses externally or force callers to stop using addresses.
- Change analyzer public APIs in a breaking way.

## Existing Flow

ClrHeap
 -> ObjectIndexWriter
 -> HeapEntry
 -> HeapIndexBuildResult

## Target Flow

ClrHeap
 -> ObjectIndexWriter (assign ObjectId)
 -> HeapEntry(ObjectId)
 -> HeapIndexBuildResult
 -> Address/ObjectId maps persisted (when disk-backed)

## HeapEntry Changes

Add:

- `uint ObjectId`

Do not remove existing fields; maintain binary compatibility where possible.

## New Index Structures

- `Address -> ObjectId` (hash or flat map)
- `ObjectId -> HeapEntry` (dense array or memory-mapped file)

Both should provide amortized O(1) access. Prefer contiguous arrays for `ObjectId -> HeapEntry` to minimize indirection and memory overhead.

## Index Builder Changes

- `MemoryBackedObjectIndexWriter`
	- Assign sequential ObjectIds while indexing (zero-based or one-based — pick and document).
	- Emit `HeapEntry` records with `ObjectId` populated.

- `DiskBackedObjectIndexWriter`
	- Persist `ObjectId` with each `HeapEntry` record and write/serialize the two maps (Address->ObjectId, ObjectId->offset) in the index footer or a dedicated sidecar.
	- Include index version and flags for ObjectId presence.

- `ObjectIndexReader`
	- Deserialize `ObjectId` and expose lightweight readers for both mappings. Prefer lazy memory-mapped reads for large indexes.

## Public API

Keep all existing address-based APIs. Add opt-in helpers:

- `bool TryGetObjectId(ulong address, out uint objectId)`
- `bool TryGetHeapEntry(uint objectId, out HeapEntry entry)`

These helpers should be fast and available on both memory- and disk-backed readers.

## Algorithm (high level)

1. Enumerate heap objects once using `heap.EnumerateObjects()`.
2. For each valid object, allocate the next `ObjectId` (sequential uint).
3. Populate `HeapEntry` with `Address`, `MethodTable`, `Size`, and `ObjectId`.
4. Store mapping in `Address->ObjectId` and append `HeapEntry` to `ObjectId->HeapEntry` storage.
5. Persist mappings (disk-backed) and write index metadata.

## Complexity

- CPU: O(N)
- Memory: +4 bytes per object for `ObjectId` plus mapping overhead. If using a dense array for `ObjectId->HeapEntry`, additional memory is proportional to N * sizeof(HeapEntry).

## ClrMD Notes

- `ObjectId` is DumpDetective-specific. Do not expose it as a substitute for address in external systems.
- Do not assume any ordering of objects beyond the ordering produced during the single index build pass.

### ClrMD behavior (explicit)

- ClrMD (including v4) exposes object addresses via `ClrObject.Address` (an `ulong`) and related `ClrObject`/`ClrType` properties. There is no built-in compact, sequential `ObjectId` provided by ClrMD itself. Tool-specific transient identifiers you may see in debugger outputs are not a stable index and should not be relied on for persistent indexing. See the ClrMD project for API details: https://github.com/microsoft/clrmd and NuGet: https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime

## Edge Cases & Caveats

- Duplicate addresses: Indexer should detect and either skip duplicates or fail the build with a clear error; persisting duplicates would corrupt maps.
- Corrupt heap pages: treat invalid objects conservatively — skip and log, or record as special `ObjectId` entries with an error flag.
- Large heaps (tens of millions of objects): memory for dense arrays can be large; provide a disk-backed mode and configurable memory thresholds.
- Object lifetime vs. index lifetime: `ObjectId`s are stable only within an index version; do not assume stability across rebuilds.
- Mixed platforms / address width: addresses are 64-bit on target platforms; ObjectId remains 32-bit — document this choice and ensure it covers expected heap sizes (if >4B objects, fallback to 64-bit ObjectId planned).
- Endianness and on-disk layout: define explicit layout and include index version and endianness markers to support cross-platform reading.

## Improvements & Suggestions

- Use a compact `HeapEntry` layout on disk (binary format) to reduce IO and memory. Consider pooling/read-only `struct` representations.
- Allow swapped storage strategies based on heap size: small heaps -> in-memory arrays; large heaps -> memory-mapped files + sparse hash for address->ObjectId.
- Provide a streaming query API that consumes `ObjectId` ranges to avoid materializing the full `ObjectId->HeapEntry` in memory for analyses that operate on subsets.
- Add a light-weight checksum or per-chunk CRC to detect disk corruption early when reading large indexes.
- Offer a deterministic ObjectId assignment mode (e.g., sort-by-address before assigning) for reproducible diffs and easier dev/test debugging — make this optional because sorting adds a copy/pass and memory/time cost.

## Backwards Compatibility & Versioning

- Add index file versioning. Readers must check for ObjectId presence and gracefully fall back to address-only behavior if absent.
- Keep existing APIs unchanged; add feature-detection flags on readers (`HasObjectIds`).

## Migration Strategy

1. Implement ObjectId in `MemoryBackedObjectIndexWriter` and `ObjectIndexReader` behind a feature flag.
2. Add serialization support in `DiskBackedObjectIndexWriter` with a new index version.
3. Ship readers that detect the version and expose `TryGetObjectId` when available.
4. Add tests, benchmarks, and an opt-in CLI switch to build ObjectId-enabled indexes.

## Testing and Validation

- Unit tests: mapping correctness, duplicate address handling, API fallbacks.
- Integration: build small, medium, and large indexes and validate round-trip read/write and `TryGetHeapEntry` correctness.
- Fuzzing: corrupt index fragments to verify graceful failure and error messages.
- Performance tests: measure indexing throughput, memory usage, and query latency across strategies (in-memory vs memory-mapped).

## Performance & Memory Notes

- Memory overhead: expect ~4 bytes/object + per-entry HeapEntry size. For N objects, dense arrays give the best locality and fastest lookup, but cost memory proportional to N.
- Disk layout: compact binary with memory-mapped reads yields lowest runtime memory and good query performance.
- Concurrency: building the index can be single-threaded for simplicity; optional multi-threaded builders must partition address ranges or synchronize `ObjectId` allocation.

## Example API Usage

Consumer code can detect and use ObjectIds when available:

```csharp
if (indexReader.HasObjectIds && indexReader.TryGetObjectId(addr, out uint oid)) {
		indexReader.TryGetHeapEntry(oid, out var entry);
		// use entry
} else {
		// fallback to address-based lookup
}
```

## Recommended Next Steps

1. Add `ObjectId` field to `HeapEntry` and wire it through `MemoryBackedObjectIndexWriter` (prototype in-memory only).
2. Add unit tests and a small integration test to validate mapping and round-trip persistence.
3. Implement disk serialization with versioning and memory-mapped reader.

## Appendix: Quick Risks

- Risk: Index size increases and may affect CI or artifact storage — mitigate by compacting on-disk layout and optional compression.
- Risk: Assumption that 32-bit `ObjectId` is sufficient; plan for 64-bit fallback.
- Risk: Reproducibility across builds — optional deterministic assignment mode can address this.


# Binary Storage Format

This document defines the binary format used for disk-backed storage of Phase 1 heap index data.

All disk-backed index data for a dump lives in a single **`cache.bin`** container file, written to
a per-dump `<dump>.dumpindex/` folder (or `--cache-dir`, see
[docs/architecture.md § 6](architecture.md#6-storage-layer)). Prior to 2026-07-15 this data was
written as nine separate files; the container consolidates them behind one header/TOC while
preserving each section's original binary payload layout.

Design principles:
- Fixed-size header + table of contents for O(1) section lookup
- Append-only writes, no in-place mutation during a build
- Sequential and (for hot sections) memory-mappable reads
- No serialization overhead — no JSON except in small metadata sections

For the cache subsystem's higher-level architecture (facade, sub-caches, writer/reader
orchestration, why sections are optional, governing design constraints), see
[docs/cache/cache-architecture.md](cache/cache-architecture.md) — not duplicated here.

---

## Container layout

| Section | Size | Description |
|---------|------|-------------|
| FileHeader | 64 bytes | Magic, version, content hash, TOC offset, section count |
| TOC (Table of Contents) | up to 17 × 32 bytes | One entry per present section |
| Section Data | Variable | Concatenated payload sections |

## FileHeader (64 bytes)

| Field | Size | Type | Value |
|-------|------|------|-------|
| Magic | 8 bytes | bytes | `"DDCACHE1"` (ASCII) |
| FormatVersion | 4 bytes | int | Current: **4**. Bumped from 3 when the `ReverseEdgeBuckets`/`ReverseEdgeDirectories`/`ReverseEdgeMetadata` sections were added; from 2 when `ObjectGenerations` was added; from 1 when the object index moved to columnar layout. Older `cache.bin` files fail to parse and are rebuilt. `SegmentIndex` (added after v4) did **not** trigger a further bump — it's purely additive and always-optional; see [docs/cache/cache-architecture.md § 4](cache/cache-architecture.md#4-point-lookup--objectaddresslookup--segmentindex). |
| DumpContentHash | 32 bytes | bytes | Content-addressed cache key: dump file length (8 bytes) + XxHash64 over sampled start/middle/end 1MB windows (8 bytes); remaining 16 bytes reserved/zero. An all-zero stored hash means "unknown" (predates this field, or hashing failed at build time) and is accepted rather than treated as a mismatch. See `DumpContentHasher`. |
| SectionCount | 4 bytes | int | Number of sections actually present in the TOC (up to 17; fewer if optional satellite sections were skipped or failed non-fatally) |
| TocOffset | 8 bytes | long | Offset to TOC — always 64 |
| Reserved | 8 bytes | bytes | Zero-filled |

## TOC entry (32 bytes each)

| Field | Size | Type | Description |
|-------|------|------|-------------|
| SectionId | 4 bytes | int | `CacheSectionId` enum value (table below) |
| Offset | 8 bytes | long | Absolute byte offset of section payload in `cache.bin` |
| Length | 8 bytes | long | Byte length of section payload |
| RecordCount | 8 bytes | long | Number of records in the section |
| Checksum | 4 bytes | uint | XxHash32 of section bytes; written always, validated lazily on the section's first open |

### CacheSectionId values

| Id | Section | Required? |
|---|---|---|
| 0 | `Objects` (legacy interleaved format) | Unused since format v2 — never written |
| 1 | `TypeAggregates` | Required (part of cache-hit fast-path check) |
| 2 | `Roots` | Satellite (skippable via `DD_SKIP_ROOT_INDEX_BUILD=1`) |
| 3 | `Handles` | Satellite |
| 4 | `Tasks` | Satellite |
| 5 | `EventCandidates` | Satellite |
| 6 | `LargeObjects` | Satellite |
| 7 | `LohFreeBlocks` | Satellite |
| 8 | `StringDedup` | Satellite |
| 9 | `StringDedupMeta` | Satellite |
| 10 | `ObjectAddresses` | Required (part of cache-hit fast-path check) |
| 11 | `ObjectMethodTables` | Required |
| 12 | `ObjectSizes` | Required |
| 13 | `ObjectGenerations` | Required (v3+) |
| 14 | `ReverseEdgeBuckets` | Optional (skippable via `DD_SKIP_REVERSE_INDEX_BUILD=1`) |
| 15 | `ReverseEdgeDirectories` | Optional |
| 16 | `ReverseEdgeMetadata` | Optional |
| 17 | `SegmentIndex` | Optional (skippable via `DD_SKIP_SEGMENT_INDEX_BUILD=1`) |

Only `TypeAggregates` and the four columnar object-index sections are re-verified by the cache-hit
fast path today; a missing/failed satellite section falls back to a live ClrMD walk for whichever
analyzer needed it — see [docs/cache/backlog.md](cache/backlog.md) for the known gap where this
fast-path check doesn't (yet) re-validate *every* previously-written section on a later hit.

---

## Section payloads

### Object index (columnar, format v2+/v3+)

Four parallel columns — struct-of-arrays layout — one entry per heap object, aligned by index:

| Section | Element type | Bytes/object | Notes |
|---|---|---|---|
| `ObjectAddresses` | `ulong` | 8 | |
| `ObjectMethodTables` | `ulong` | 8 | |
| `ObjectSizes` | `ulong` | 8 | |
| `ObjectGenerations` | `sbyte` | 1 | GC generation (0/1/2, higher for LOH/POH/Frozen depending on ClrMD's reporting), or -1 if unresolved |

Written by `DiskBackedObjectIndexWriter` as per-segment scratch-file columns, concatenated into
the container after the parallel scan completes. Read back by `ObjectIndexReader`, which zips the
four columns into `HeapEntry` records via pooled buffers, batched by index size.

The generation value is computed once per object during the single-pass heap scan (segment-kind
lookup for non-ephemeral segments, `segment.GetGeneration(address)` for ephemeral ones — no extra
ClrMD calls beyond what the scan already pays), so Phase 2 analyzers read `entry.Generation`
directly instead of re-resolving it via `SegmentKindMapper.ResolveGeneration(heap, address)`.

This replaces the legacy interleaved `Objects` section (24-byte header + 24-byte
`Address|MethodTable|Size` records) used before format v2; `RecordCount` in the TOC replaces the
old per-section header entirely — there is no separate `ObjectIndex.bin` file anymore.

### TypeAggregates
`TypeAggregateIndex.bin`-format payload: extended header + one record per type
(`MethodTable | Count | TotalSize`, plus module registry, global size buckets, and type-shape
cache). Presence enables a fast-path that skips a full heap rescan on subsequent runs.

### Roots (v2, current)
24-byte header + 20-byte fixed root records, plus a variable-length trailer — one record per
static/thread-static root that resolved to a declaring field — laid out as
`RootAddress(8) | OwnerTypeLen(2) | FieldNameLen(2) | AppDomainId(4) | OwnerType(N) | FieldName(M)`.
The trailer's record count is stashed in the shared 24-byte header's `Reserved` field (bytes
16–23, always 0 in v1 and in every other satellite index that doesn't use a trailer). Written by
`RootIndexWriter.WriteFieldNameTrailer`, read by `RootIndexReader.ReadRootFieldNames`. A v1
(pre-trailer) `Roots` payload fails the reader's header-version check entirely (not just missing
field names — all root data), falling back to a live heap walk until the cache is rebuilt. See
[docs/analysis/root-field-name-index-plan.md](analysis/root-field-name-index-plan.md).

### Handles / Tasks / EventCandidates / LargeObjects / LohFreeBlocks
Each preserves its pre-migration per-file format unchanged: 24-byte header (magic, version,
ticks, record count) + fixed-size records — `HandleSnapshot.bin` (20-byte records: Addr,
MethodTable, Kind), `TaskIndex.bin`, `EventCandidateIndex.bin`, `LargeObjectIndex.bin`,
`LohFreeBlockIndex.bin`.

### StringDedup / StringDedupMeta
`StringDedupIndex.bin` format: 12-byte header + dedup records, keyed by XxHash64 →
preview/count/total-size. `StringDedupMeta` is UTF-8 JSON holding a distribution summary.

### ReverseEdgeBuckets / ReverseEdgeDirectories / ReverseEdgeMetadata
The disk-backed reverse-reference (parent-lookup) index: hash-partitioned, sorted-per-bucket edge
data plus a JSON metadata section describing bucket layout (bucket byte-ranges are recorded here
rather than in the TOC, since the container TOC has one fixed slot per `CacheSectionId`, not one
per bucket). See [docs/analysis/phase1-redesigns/full-reverse-index-plan.md](analysis/phase1-redesigns/full-reverse-index-plan.md)
for the full format.

### SegmentIndex
A small per-GC-segment table enabling `ObjectAddressLookup`'s binary-search
`address → (MethodTable, Size)` point lookup (backing `IHeapAnalysisCache.TryGetObjectMetadata`),
as opposed to the sequential-only object-index columns above. Segment-count-sized, not
object-count-sized — always fully loaded into memory by the reader rather than mmap'd. A missing
section just means the point lookup falls back to a live `heap.GetObject` resolution. Written/read
by `Indexing/Satellite/SegmentIndexWriter.cs`.

Record layout (28 bytes, little-endian), in the shared 24-byte `IndexHeader` style used by other
satellite indexes (magic `"SEGX"`, version 1):

| Field | Size | Type | Description |
|-------|------|------|-------------|
| Start | 8 bytes | ulong | Segment's starting address (`ClrSegment.Start`) |
| End | 8 bytes | ulong | Segment's ending address (`ClrSegment.End`) |
| FirstRecordIndex | 8 bytes | long | This segment's first record index into the concatenated object-index columns |
| RecordCount | 4 bytes | int | Number of objects in this segment (fits `int` — no single GC segment holds anywhere near 2^31 objects) |

Segments with zero objects are omitted entirely (a lookup can never land in one). Point lookup does
a two-level binary search — segment table, then in-segment slice of the mmap'd `ObjectAddresses`
column — because disk-mode enumeration order intentionally matches `heap.EnumerateObjects()`'s own
segment-iteration order, not a globally address-sorted order (capped-scan analyzers depend on
*which* objects populate a partial scan). See
[docs/cache/cache-architecture.md § 4 and § 7](cache/cache-architecture.md) for the full rationale.
A lookup miss (address falls in a segment gap, free block, padding, or doesn't land exactly on a
record boundary — interior pointers are out of scope) returns `false`, not an error.

---

## Atomic write

The container is written atomically:
1. Writer opens `cache.bin.tmp`, writes a zeroed placeholder header + TOC
2. Writes each section in sequence, checksumming incrementally as bytes are written (no re-read
   pass)
3. On `Finish()`: patches the real header/TOC, flushes to disk, atomically renames `.tmp` →
   `cache.bin`
4. On exception: the `.tmp` file is deleted; the next run sees a cache miss and rebuilds cleanly —
   never a half-written `cache.bin`

## Read path

- `CacheContainerReader.TryOpen` validates magic/version/content-hash once; subsequent section
  access via `TryOpenSection`/`TryOpenSectionAccessor` is instant (TOC lookup + bounded stream)
- Hot sections (`ObjectAddressLookup`'s `SegmentIndex` + `ObjectAddresses`) are accessed via
  `MemoryMappedFile`/`MemoryMappedViewAccessor` for point lookups; large sequential sections
  (the object-index columns during a full scan) use pooled `FileStream` batch reads instead,
  since the zero-copy unsafe pointer pattern isn't worth the complexity for
  thousands-of-lookups-per-run point queries versus hundreds-of-millions-of-records sequential
  reads

## Benefits over the prior per-file design

- **Single validity check**: one `cache.bin` + magic/version/content-hash check, not per-file
  existence + mtime checks. The content hash means a dump moved/copied to a new path still hits
  the cache, and a same-path dump silently replaced with different content doesn't.
- **Atomic writes**: no partial cache state where some files exist and others don't
- **Simpler reader logic**: one `CacheContainerReader` opens the container once; sections access
  it via bounded streams
- **Backward-compatible at section level**: each section's own binary parsing logic is unchanged
  from its pre-migration per-file format; only "how do I get a stream for this section" changed

---

## Constraints

- Endianness: little-endian only
- No per-record compression (handled externally if ever needed)
- A format-version mismatch invalidates the whole container (rebuild), not a per-section
  granularity

---

## Where to look next

- [docs/architecture.md](architecture.md) — overall system architecture
- [docs/cache/cache-architecture.md](cache/cache-architecture.md) — cache subsystem internals:
  facade/sub-cache design, writer segment-scan details, governing design constraints
- [docs/cache/backlog.md](cache/backlog.md) — known gaps (unbounded satellite candidate
  collections, cache-hit fast-path section coverage, unread `EventCandidateIndex`, etc.)
- [docs/analysis/phase1-redesigns/full-reverse-index-plan.md](analysis/phase1-redesigns/full-reverse-index-plan.md) — reverse-index full format

# 💾 Binary Storage Format

This document defines the binary format used for disk-backed storage of heap data.

The format is designed for:
- Sequential writes
- Fast reads
- Minimal memory overhead

---

# 🧠 Design Principles

- Fixed-size records for predictable offsets
- Append-only writes
- No in-place mutation
- Alignment for efficient memory access
- Avoid serialization overhead (no JSON)

---

# 📦 Container Format (`cache.bin`)

As of **2026-07-15**, all disk-backed index data is written to a single **`cache.bin`** container file instead of nine separate files. The container uses a fixed-size header + table of contents to address sections, preserving all per-section binary layouts unchanged.

## Single Container Layout

| Section | Size | Description |
|---------|------|-------------|
| FileHeader | 64 bytes | Magic, version, TOC offset, section count |
| TOC (Table of Contents) | ~384 bytes | 12 entries × 32 bytes each (ObjectAddresses, ObjectMethodTables, ObjectSizes, TypeAggregates, Roots, Handles, Tasks, EventCandidates, LargeObjects, LohFreeBlocks, StringDedup, StringDedupMeta) |
| Section Data | Variable | Concatenated payload sections (ObjectAddresses, ObjectMethodTables, ObjectSizes, TypeAggregates, Roots, Handles, Tasks, EventCandidates, LargeObjects, LohFreeBlocks, StringDedup, StringDedupMeta) |

## FileHeader (64 bytes)

| Field | Size | Type | Value |
|-------|------|------|-------|
| Magic | 8 bytes | bytes | "DDCACHE1" (ASCII) |
| FormatVersion | 4 bytes | int | 2 (bumped from 1 when the Objects section moved to columnar layout; old `cache.bin` files fail to parse and are rebuilt) |
| DumpContentHash | 32 bytes | bytes | Content-addressed cache key: dump file length (8 bytes) + XxHash64 over sampled start/middle/end 1MB windows (8 bytes), remaining 16 bytes reserved/zero. Zero-filled if unknown (predates this field, or hashing failed at build time); an all-zero stored hash is treated as "unknown" and accepted rather than a mismatch. See `DumpContentHasher`. |
| SectionCount | 4 bytes | int | Number of sections in TOC (typically 12) |
| TocOffset | 8 bytes | long | Offset to TOC = 64 |
| Reserved | 8 bytes | bytes | Zero-filled |

## TOC Entry (32 bytes each)

| Field | Size | Type | Description |
|-------|------|------|-------------|
| SectionId | 4 bytes | int | Section identifier (`CacheSectionId` enum: 0=Objects [unused since v2], 1=TypeAggregates, 2=Roots, 3=Handles, 4=Tasks, 5=EventCandidates, 6=LargeObjects, 7=LohFreeBlocks, 8=StringDedup, 9=StringDedupMeta, 10=ObjectAddresses, 11=ObjectMethodTables, 12=ObjectSizes) |
| Offset | 8 bytes | long | Absolute byte offset of section payload in `cache.bin` |
| Length | 8 bytes | long | Byte length of section payload |
| RecordCount | 8 bytes | long | Number of records in section |
| Checksum | 4 bytes | uint | XxHash32 of section bytes (validation deferred to later row) |

## Section Payload

Each section's payload is **exactly the bytes that would have been in the pre-migration per-file format**, unchanged, except the object index, which moved from an interleaved array-of-structs layout to three parallel columnar sections (see below). This preserves all other existing reader logic:

- **ObjectAddresses / ObjectMethodTables / ObjectSizes sections** (format version 2+): three parallel `ulong[]` columns — struct-of-arrays layout — one entry per heap object, aligned by index across all three columns. Written by `DiskBackedObjectIndexWriter` as per-segment scratch-file columns, concatenated into the container; read back by `ObjectIndexReader`, which zips them into `HeapEntry` records via pooled buffers, batched by index size. Replaces the legacy interleaved `Objects` section (24-byte header + 24-byte `Address|MethodTable|Size` records); `RecordCount` in the TOC replaces the old per-section header.
- **TypeAggregates section**: TypeAggregateIndex.bin format (extended header + type records)
- **Roots section**: RootIndex.bin format (24-byte header + 20-byte root records)
- **Handles section**: HandleSnapshot.bin format (24-byte header + 20-byte handle records)
- **Tasks section**: TaskIndex.bin format (24-byte header + task records)
- **EventCandidates section**: EventCandidateIndex.bin format
- **LargeObjects section**: LargeObjectIndex.bin format
- **LohFreeBlocks section**: LohFreeBlockIndex.bin format
- **StringDedup section**: StringDedupIndex.bin format (12-byte header + dedup records)
- **StringDedupMeta section**: UTF-8 encoded JSON (distribution summary)

## Atomic Write

The container is written atomically:
1. Writer opens `cache.bin.tmp` (temp file)
2. Writes sections sequentially to temp file
3. On `Finish()`: writes TOC and FileHeader, flushes to disk, atomically renames `.tmp` → `cache.bin`
4. On exception: deletes `.tmp` file; next run sees missing container (cache miss)

## Benefits Over Per-File Design

- **Single validity check**: Verify one `cache.bin` file + magic/version + content hash, not two (pre-migration checked only `TypeAggregateIndex.bin` existence + dump mtime). The content hash (see `DumpContentHasher`) means a dump moved/copied to a new path still hits the cache, and a same-path dump silently replaced with different content doesn't.
- **Atomic writes**: No partial cache state where some files exist and others don't
- **Simpler reader logic**: One `CacheContainerReader` opens container once, multiple sections access it via bounded streams
- **Backward-compatible at section level**: Each reader's per-format parsing logic unchanged; only "how do I get a stream" is different

---

offset = 16 + (i * 24)
# 📦 Object Index Format

Each object is stored as a fixed-size 24-byte record:

| Field        | Size (bytes) | Type   | Description                  |
|--------------|--------------|--------|------------------------------|
| Address      | 8            | ulong  | Object memory address        |
| MethodTable  | 8            | ulong  | Type identifier              |
| Size         | 8            | ulong  | Object size in bytes         |

---

## Record Size

Total = **24 bytes**

---

## File Structure

[ObjectIndex.bin Header]
[Object Records...]
[Satellite/Optional Index Sections]

---

# 🧾 Header Format

`ObjectIndex.bin` uses a 24-byte header (preserved for backward compatibility):

| Field        | Size | Description |
|--------------|------|-------------|
| Magic        | 4    | File identifier (int)
| Version      | 4    | Format version (int)
| Ticks        | 8    | UTC ticks captured at build time (long)
| RecordCount  | 8    | Total number of records (long)

Header size = 24 bytes

---

## Offset Calculation

To locate record `i`:

offset = header_size + (i * record_size)

Example:

offset = 24 + (i * 24)

---

# 🔍 Read Strategy

## Sequential Read
- Use `FileStream` with large buffered reads and `ArrayPool<byte>` buffers.

## Random Access
- Use `MemoryMappedFile` and compute offsets directly using the 24-byte header and 24-byte records.

---

# ✍️ Write Strategy

- Always append records
- Never overwrite existing data
- Write a placeholder header, stream records serially (parallel segment scan writes under a lock), then overwrite header with final `RecordCount`.

---

# 📊 Type Index (Optional Section)

Stores aggregated type data (satellite file `TypeAggregateIndex.bin`):

| Field        | Size | Description              |
|--------------|------|--------------------------|
| MethodTable  | 8    | Type identifier          |
| Count        | 8    | Number of objects        |
| TotalSize    | 8    | Total memory usage       |

---

# 🔗 Future Extensions

Reserved satellite files and header versioning enable adding:

- Reference offsets
- Generation info (Gen0/1/2/LOH/POH)
- Flags (pinned, finalizable, etc.)

---

# ⚠️ Constraints

- Endianness: Little-endian only
- No per-record compression (handled externally if needed)
- Backward compatibility maintained via header version field

---

# 🚀 Performance Characteristics

| Operation        | Complexity |
|------------------|------------|
| Write            | O(1)       |
| Sequential Read  | O(n)       |
| Random Access    | O(1)       |

---

# 🧠 Rationale

Binary format chosen for predictable layout, low overhead, and memory-map friendliness.

---

# 🏁 Summary

This binary format ensures minimal per-object overhead and efficient large-scale processing.

It is optimized for high-performance dump analysis at scale.
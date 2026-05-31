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
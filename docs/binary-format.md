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

# 📦 Object Index Format

Each object is stored as a fixed-size record:

| Field         | Size (bytes) | Type   | Description                  |
|--------------|-------------|--------|------------------------------|
| Address      | 8           | ulong  | Object memory address        |
| MethodTable  | 8           | ulong  | Type identifier              |
| Size         | 4           | int    | Object size in bytes         |

---

## Record Size

Total = **20 bytes**

Optional padding to 24 bytes for alignment:

| Padding      | 4           | unused | Reserved for future use      |

---

## Final Record Layout (Aligned)

| Offset | Field        | Size |
|--------|-------------|------|
| 0      | Address     | 8    |
| 8      | MethodTable | 8    |
| 16     | Size        | 4    |
| 20     | Padding     | 4    |

Total = **24 bytes**

---

# 📁 File Structure


[Header]
[Object Records...]
[Optional Index Section]


---

# 🧾 Header Format

| Field          | Size | Description                  |
|---------------|------|------------------------------|
| Magic Number  | 4    | File identifier              |
| Version       | 4    | Format version               |
| Record Count  | 8    | Total number of records      |

---

## Example

| Field         | Value        |
|--------------|-------------|
| Magic        | 0x444D5041  ("DMPA") |
| Version      | 1           |
| Record Count | N           |

---

# 🧮 Offset Calculation

To locate record `i`:


offset = header_size + (i * record_size)


Example:


offset = 16 + (i * 24)


---

# 🔍 Read Strategy

## Sequential Read
- Use FileStream
- Read in large buffered chunks

## Random Access
- Use MemoryMappedFile
- Compute offsets directly

---

# ✍️ Write Strategy

- Always append records
- Never overwrite existing data
- Flush periodically (batch writes)

---

# 📊 Type Index (Optional Section)

Stores aggregated type data:

| Field         | Size | Description              |
|--------------|------|--------------------------|
| MethodTable  | 8    | Type identifier          |
| Count        | 4    | Number of objects        |
| TotalSize    | 8    | Total memory usage       |

---

# 🔗 Future Extensions

Reserved space enables adding:

- Reference offsets
- Generation info (Gen0/1/2/LOH/POH)
- Flags (pinned, finalizable, etc.)

---

# ⚠️ Constraints

- Endianness: Little-endian only
- No compression (handled externally if needed)
- Backward compatibility via version field

---

# 🚀 Performance Characteristics

| Operation        | Complexity |
|----------------|-----------|
| Write           | O(1)      |
| Sequential Read | O(n)      |
| Random Access   | O(1)      |

---

# 🧠 Rationale

Why binary format instead of JSON:

- Predictable layout
- Lower memory footprint
- Faster parsing
- Better cache locality

---

# 🏁 Summary

This binary format ensures:
- Minimal overhead per object
- Efficient large-scale processing
- Compatibility with memory-mapped access

It is optimized for high-performance dump analysis at scale.
# Storage Layer

**Purpose:**
- Persist compact object indices and auxiliary type indices to disk for large-dump analysis.

**Responsibilities:**
- Append-only writes of fixed-size object records.
- Provide high-throughput readers (sequential and memory-mapped).
- Offer optional type-index sections for quick aggregation.

**Key types / interfaces:**
- `ObjectIndexWriter`
- `ObjectIndexReader`

**Binary format:**
- Aligned record layout (24 bytes): `Address(8) | MethodTable(8) | Size(4) | Padding(4)`.
- Header with magic, version, record count.

**Performance / safety constraints:**
- Use sequential writes; batch and flush periodically.
- Prefer large buffered reads and `MemoryMappedFile` for random access.
- Little-endian only; version header for compatibility.

**Related docs:**
- [docs/binary-format.md](docs/binary-format.md)

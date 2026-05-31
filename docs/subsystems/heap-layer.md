# Heap Layer

**Purpose:**
- Stream heap objects and produce compact, high-throughput metadata for indexing.

**Responsibilities:**
- Enumerate heap with `foreach (var o in heap.EnumerateObjects())` and yield minimal `HeapEntry`.
- Produce `HeapEntry` structs: `Address`, `MethodTable`, `Size`.
- Drive `TypeIndexBuilder` and segment classification.

**Key types / interfaces:**
- `HeapStreamer`
- `HeapEntry` (readonly struct)
- `TypeIndexBuilder`
- `SegmentAnalyzer`

**Performance / safety constraints:**
- Never call `.ToList()` on enumeration; use streaming.
- Avoid per-object allocations; use `struct` models and `ArrayPool` buffers.
- Single-pass scan to feed disk-backed index writer.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [docs/performance-checklist.md](docs/performance-checklist.md)

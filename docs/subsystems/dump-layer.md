# Dump Layer

**Purpose:**
- Load memory dumps and provide a safe runtime abstraction for analyzers.

**Responsibilities:**
- Resolve DAC and create `DataTarget`/`ClrRuntime`.
- Validate heap walkability and expose `DumpLoadContext`.
- Provide lifecycle management and disposal semantics.

**Key types / interfaces:**
- `IDumpLoader` — load dump, return `DumpLoadContext`.
- `RuntimeFacade` — cached `MethodTable -> ClrType` access.

**Performance / safety constraints:**
- Avoid holding large ClrMD objects longer than necessary; dispose promptly.
- Cache `ClrType` metadata to reduce expensive lookups.
- Validate `obj.IsValid` and `obj.Type != null` before use.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)

**Notes:**
- Keep surface area minimal; expose only safe, cached helpers to analyzers.

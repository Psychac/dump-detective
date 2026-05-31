# Cache Layer

**Purpose:**
- Provide small, memory-resident caches for hot metadata (type maps, ClrType metadata) without storing large object graphs.

**Responsibilities:**
- Maintain `MethodTable -> ClrType` caches.
- Provide `IHeapAnalysisCache` read-only queries of aggregated stats.

**Key types / interfaces:**
- `IHeapAnalysisCache`
- `IMethodTableCache` / `RuntimeFacade` caching helpers

**Performance / safety constraints:**
- Keep memory footprint bounded; use size limits and eviction policies.
- Avoid caching whole objects or full graphs.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [docs/performance-checklist.md](docs/performance-checklist.md)

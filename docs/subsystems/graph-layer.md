# Graph Layer

**Purpose:**
- Provide reference traversal primitives and root-path finding while keeping memory bounded.

**Responsibilities:**
- Compute forward references lazily from object fields.
- Build selective reverse-reference indexes on demand and disk-backed when large.
- Implement `RootPathFinder` (bounded BFS) for finding paths from GC roots.

**Key types / interfaces:**
- `ReferenceGraph`
- `ReverseReferenceIndex` (optional)
- `RootPathFinder`

**Performance / safety constraints:**
- Never materialize full reverse graph in memory.
- BFS depth limit (default 20) and visited `HashSet<ulong>`.
- Early termination when path found; scope reverse index builds.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [docs/performance-checklist.md](docs/performance-checklist.md)

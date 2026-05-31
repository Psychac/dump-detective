# Query Layer

**Purpose:**
- Offer structured, index-based query capabilities for interactive and programmatic inspection.

**Responsibilities:**
- Execute queries like "top types by memory", "objects of type X", and reference path requests.
- Translate queries into index reads and scoped traversals.

**Key types / interfaces:**
- `QueryEngine`
- Query models and result DTOs

**Performance / safety constraints:**
- Operate on indices, not raw heap objects, for most queries.
- Avoid repeated full-index scans; support paged/batched results.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)

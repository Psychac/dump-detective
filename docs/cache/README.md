# Cache Subsystem Docs

Two docs, current-state and forward-looking:

- **[cache-architecture.md](cache-architecture.md)** — the authoritative spec for what's
  actually built: the `HeapAnalysisCache` facade and its sub-caches, the `cache.bin`
  container, the disk writer/reader, the object-address point lookup, the reverse
  (parent-lookup) index, forward-BFS traversal, and the governing design constraints.
  Written directly against source, not against prior design docs.
- **[backlog.md](backlog.md)** — everything real and not yet built: bounded-memory
  gaps, perf wins with data already collected but unread, the confirmed GC-root
  native-cost diagnosis and its unattempted mitigations, and gated/speculative items
  with their trigger conditions.

For the exact byte-level `cache.bin` layout, see
[docs/binary-format.md](../binary-format.md). For the disk-backed reverse-reference
index's full format, see
[docs/analysis/phase1-redesigns/full-reverse-index-plan.md](../analysis/phase1-redesigns/full-reverse-index-plan.md).

Prior design-history docs (numbered docs, `ArchitectureDecisions.md`,
`cache-modernization-spec.md`) have been retired — their still-true content is folded
into the two docs above; their still-open proposals are in `backlog.md`.

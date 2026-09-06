# Cache Subsystem Docs

Current state, backlog, and a three-doc redesign set.

**Current state and open work:**

- **[cache-architecture.md](cache-architecture.md)** — the authoritative spec for what's
  actually built: the `HeapAnalysisCache` facade and its sub-caches, the `cache.bin`
  container, the disk writer/reader, the object-address point lookup, the reverse
  (parent-lookup) index, forward-BFS traversal, and the governing design constraints.
  Written directly against source, not against prior design docs.
- **[backlog.md](backlog.md)** — everything real and not yet built: bounded-memory
  gaps, perf wins with data already collected but unread, the confirmed GC-root
  native-cost diagnosis and its unattempted mitigations, and gated/speculative items
  with their trigger conditions.

**Redesign set — partly shipped.** Read the measurements first; they overturned several
conclusions the two design docs originally reached, and both docs carry `⚠` markers where that
happened.

Shipped so far, on the reference dump's `cache.bin`: 1,398.3 → **587.29 MiB, 42.0% of where it
started**, with no compression written yet. The implementation doc's §6 (shared `CacheSession`,
section catalog, generic provider cache) is closed; the write-only `ForwardEdge*` sections are no
longer persisted (−462.4 MiB, measurements §9.2); `MethodTable` dictionary encoding shipped as
format v5 (−83.6 MiB, measurements §11); **format v6** — narrowed `ObjectSizes`, block-delta
`ObjectAddresses` and `DominatorReachableAddresses`, plus the section manifest — shipped in full
(−164.7 MiB, measurements §14); and **format v7** — the dominator child list derived on demand
instead of persisted, resolved to that option by measuring its real consumer's call frequency
(zero, across three real dumps) rather than left as an open fork — shipped (−100.4 MiB,
measurements §16). Spec in [format doc §4](cache-format-clean-slate-redesign.md) (dominator) and
[§10](cache-format-clean-slate-redesign.md) (v6 base columns); the sequence for everything still
open (CSR, then compression last) is in §7.1.1.

- **[cache-redesign-measurements.md](cache-redesign-measurements.md)** — the evidence base.
  Structural survey of five real `cache.bin` files, measured block-compression ratios on real
  section bytes, the per-open checksum cost, and the statically-derived per-run open count.
  Obtained without loading any dump.
- **[cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md)** — the bytes
  on disk: block compression, delta encoding, `MethodTable` dictionary encoding, CSR edge
  indices. Revised plan in §7.1; **design risks in §7.2, including one that qualifies the
  headline recommendation.**
- **[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)**
  — the code around the bytes: shared session, section descriptors, column projection. Start at
  §6.0 (zero-dependency fixes); design risks in §6.4.

The two design docs are independent of each other — neither blocks the other.

For the exact byte-level `cache.bin` layout, see
[docs/binary-format.md](../binary-format.md). For the disk-backed reverse-reference
index's full format, see
[docs/analysis/phase1-redesigns/full-reverse-index-plan.md](../analysis/phase1-redesigns/full-reverse-index-plan.md).

Prior design-history docs (numbered docs, `ArchitectureDecisions.md`,
`cache-modernization-spec.md`) have been retired — their still-true content is folded
into the two docs above; their still-open proposals are in `backlog.md`.

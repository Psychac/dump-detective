# Index Container Format — Source-Neutral Summary

Part of [../../docs/refactor/modularity/phase-1-contracts-sdk.md](../../docs/refactor/modularity/phase-1-contracts-sdk.md).
This is the "generalized from docs/binary-format.md" deliverable that phase's migration step 5
calls for — but it is a pointer/summary document, not a restatement of
[docs/binary-format.md](../../docs/binary-format.md)'s byte-level tables. Read that doc for the
per-dump-section wire layout; this doc only covers what changed once a second artifact kind (trace)
started sharing the same container.

## What actually shipped: one flat catalog, not string namespacing

The Phase 2 target shape (see
[phase-2-artifact-platform.md](../../docs/refactor/modularity/phase-2-artifact-platform.md)'s
target-shape listing) sketched `SectionedContainer` as generalizing section identity to string
namespaces — `"heap.*"`, `"trace.*"`. **That is not what shipped.** What's real today
(`src/DumpDetective.Platform/Storage/Container/CacheContainerFormat.cs`,
`CacheSectionCatalog.cs`) is:

- A single `internal enum CacheSectionId` — plain integer identifiers, append-only, never
  renumbered — shared by every artifact kind that uses the container.
- A single `CacheSectionCatalog.All` list pairing each id with a name and a
  `CacheSectionRequirement` (`Required` / `Conditional` / `Unused`).
- One `cache.bin`-shaped container (magic `"DDCACHE1"`, `CacheContainerFormat.CurrentFormatVersion
  = 10` as of 2026-09-09 — see the staleness note below), one `FileHeader` + TOC, written by
  `DumpDetective.Analysis` for dump sections and by `DumpDetective.Sources.NetTrace` for trace
  sections, both going through the same `Storage.Container` code in `DumpDetective.Platform`
  (which has zero ClrMD/TraceEvent references — verified by
  `PlatformProject_ShouldDependOnSdkOnly`).

This achieves the same goal the string-namespaced design was reaching for — one container format
serving every artifact kind, safely — at a much lower cost: no new container/reader/writer classes
were needed, because the existing dump-era `CacheContainerFormat` was already source-agnostic once
inspected (see [phase-2-artifact-platform.md § Status](../../docs/refactor/modularity/phase-2-artifact-platform.md#status-trimmed-pass-shipped-2026-09-09)).

## The extension pattern, demonstrated by Phase 6a/6b

Four trace-only sections were added purely additively, with **no `CurrentFormatVersion` bump**:

| `CacheSectionId` | Requirement | Written by |
|---|---|---|
| `TraceMethods` | Conditional | `DumpDetective.Sources.NetTrace` (`TraceMethodIndexer`) |
| `TraceGcEvents` | Conditional | `DumpDetective.Sources.NetTrace` (`GcPauseIndexer`) |
| `TraceContention` | Conditional | `DumpDetective.Sources.NetTrace` (`ContentionIndexer`) |
| `TraceCpuSamples` | Conditional | `DumpDetective.Sources.NetTrace` (`CpuSampleIndexer`) |

Each is `Conditional`, not `Required` or `Unused` — see `CacheSectionCatalog`'s own remarks for why:
a dump-produced container's permanent absence of a `Trace*` section isn't a degraded build, it's
simply the wrong artifact kind, and `Conditional` is the closest existing fit (`Required` would
break every dump's cache-hit fast path; `Unused`'s own doc comment, "no writer and no reader in
current code," is false here since `Sources.NetTrace` has both). This is the one real precedent for
how a future third artifact kind should extend the catalog: add ids, mark them `Conditional`, no
format-version bump, and (per `CacheSectionCatalog.MissingFromCatalog()`, exercised by a standing
unit test) the catalog itself will fail closed if a new id is added without a matching entry.

## Where the real record layouts live

This doc intentionally does not re-derive per-section byte layouts:

- Dump sections (`Objects*`, `Roots`, `TypeAggregates`, `ReverseEdge*`, `Dominator*`, ...): see
  [docs/binary-format.md](../../docs/binary-format.md) and
  [docs/cache/cache-architecture.md](../../docs/cache/cache-architecture.md).
- Trace sections (`trace.gcevents`, `trace.contention`, `trace.cpu-samples`, `trace.methods`
  record shapes): see
  [phase-6-trace-source.md](../../docs/refactor/modularity/phase-6-trace-source.md).

## Known gap, flagged not fixed here

`docs/binary-format.md`'s own header table says `FormatVersion` "Current: 4". The real value today
is `CacheContainerFormat.CurrentFormatVersion = 10`, and that doc's `CacheSectionId` table (17
entries) is missing more than 20 real ids added since (`ObjectTypeDictionary`,
`ObjectAddressBlockBases`, `SectionManifest`, `ReverseEdgeOffsets`/`ReverseEdgeChildren`,
`ObjectGenerationRuns`, `ReachableRowBitmap`, the four `Trace*` ids above, and others). This is
pre-existing cache-subsystem documentation debt, unrelated to the dump/trace generalization this
doc covers, and out of scope to fix as part of Phase 1 — noted here only so it isn't mistaken for
something this pass already resolved.

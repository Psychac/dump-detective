# ReferenceChainAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low

## Report Sections Served
- §4.1 Retention Hotspots (reference chain samples)
- §4.2 Dominator Tree (reference chain paths to roots)
- §5.3 Root Paths (root → object chains — partial; `BoundedRootPathFinder` utility)

---

## Currently Produces
- `ReferenceChainDomainResult`: samples reference chains from top N types to GC roots
- `MaxPathSearchObjects = 5000`, `DefaultMaxPathDepth = 25`
- Uses `BoundedRootPathFinder` internally

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Overlap with `GCRootAnalyzer` (once created) — deduplication concern | §5.3 | Medium |
| Confidence / cap signal in domain result | §17 | Medium |

---

## Required Changes

1. **Add `ChainSearchCapped`** flag and `CappedCount` to `ReferenceChainDomainResult` —
   how many of the sampled types had their path search capped. Already computable from
   `BoundedRootPathFinder` results; not currently surfaced.
2. **Post-`GCRootAnalyzer` creation**: `ReferenceChainAnalyzer` should shift focus from
   general top-N type sampling to **on-demand deep path tracing** for specific flagged
   objects (those identified by `DominatorAnalyzer` or `GCRootAnalyzer`). This makes it
   a depth tool rather than a breadth tool.

---

## Phase Assignment

`ReferenceChainAnalyzer` is **entirely Phase 2**. Reference chain traversal uses
`ClrObject.EnumerateReferences()` which requires live heap access.

The `ChainSearchCapped` addition is a pure result field populated from already-available
`BoundedRootPathFinder.Capped` / `PathSearchCapReason` signals — zero cost.

---

## Related Analyzers
- **`GCRootAnalyzer`** (new) — unified root path finding; `ReferenceChainAnalyzer` becomes the on-demand depth tool
- **`DominatorAnalyzer`** (new) — identifies specific objects needing deep path traces
- **`StaticRootLeakDetector`** — shares `BoundedRootPathFinder` utility; static roots are a subset of all roots

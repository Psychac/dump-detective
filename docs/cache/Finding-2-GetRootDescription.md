# Finding 2 — `GetRootDescription` (detailed analysis)

## Summary

`HeapAnalysisCache.GetRootDescription` is currently a dead delegation: the
internal `_rootDescriptions` field is declared but never populated, and the
API always returns `null`. The problem is symmetric across disk and memory
modes because the disk fast-path in `RootCache.GetOrBuildValidRoots` returns
early after reading `RootIndex.bin` (which contains only target/root/kind,
not human-readable description strings).

## Root cause

- Index format and fast-paths prioritized compact satellite indices and omitted
  description strings to keep index size and write time low.
- `HeapAnalysisCache` never delegates to `RootCache` or otherwise hydrates
  `_rootDescriptions`, leaving the API surface unimplemented.

## User-visible impact

- Reports and analyzers that would show friendly root descriptions (e.g. static
  field names, event handler signatures, thread descriptions) lack readable
  text, making triage and human analysis harder.
- Numeric analyzer outputs remain correct; this is a UX/correctness gap,
  not a functional/diff discrepancy between disk and memory modes.

## Trade-offs for possible fixes

- Persist descriptions in the root index (fast reads, larger index files).
- Compute descriptions lazily via `ClrRoot.ToString()` on demand (smaller
  indices, potential ClrMD/heap overhead and thread-safety considerations).
- Hybrid: persist descriptions for top-N important roots and lazily compute
  the rest.

## Implementation considerations

- Backwards compatibility: add an index version or sentinel so older indices
  without descriptions remain readable and trigger lazy compute.
- Thread-safety: cache computed descriptions under existing cache locks or in a
  concurrent structure; avoid calling ClrMD unsafely from parallel threads.
- Privacy/performance: truncate or sanitize long strings before persisting;
  use a deduplicated string table to reduce disk cost.

## Testing and validation

- Unit tests for `HeapAnalysisCache.GetRootDescription` covering disk fast-path
  and memory-mode lazy paths.
- Integration tests that assert presence and stability of descriptions for a
  small synthetic dump.
- Perf tests to measure index size and write/read time with persisted
  descriptions versus lazy computation.

## Recommendation

1. Short-term: wire `HeapAnalysisCache.GetRootDescription` to delegate to
   `_rootcache`/`RootIndexReader` or to compute lazily so the API returns
   reasonable values (small, low-risk change).
2. Medium-term: add optional persisted descriptions with index-versioning and
   a deduped string table (best UX, modest disk cost).
3. Long-term: implement selective/top-N persistence with lazy fallback and
   add tests/benchmarks to validate size and performance.

---

_Document generated: 2026-07-13_

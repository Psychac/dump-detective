
# Implementation Specification – Cache Refactoring

## Background

`HeapAnalysisCache` has accumulated multiple responsibilities (coordination, container, utilities). The goal of this refactor is to turn it into a lightweight façade that delegates to focused cache components while preserving the public API used by analyzers.

## Goals

- Separate responsibilities and reduce coupling.
- Keep analyzers unchanged; preserve external behavior and signatures.
- Make components testable, observable, and easier to maintain.

## Non-Goals

- Implementing a full graph or ObjectId system in this phase.
- Changing on-disk serialization formats.

## Existing Classes (reference)

- `HeapAnalysisCache`
- `HeapIndexBuildResult`
- `MemoryBackedObjectIndexWriter`
- `DiskBackedObjectIndexWriter`

## Target Design

```
HeapAnalysisCache (façade)
 ├── HeapIndexCache
 ├── StatisticsCache
 ├── RootCache
 ├── TypeMetadataCache
 ├── ThreadCache
 └── MethodTableCache
```

`HeapAnalysisCache` becomes the composition root and delegates responsibilities to the specialized caches.

## Responsibilities (concise)

- HeapIndexCache: Owns `HeapIndexBuildResult`, builds/loads indexes, exposes readers and lifetime management.
- StatisticsCache: Owns type aggregates; hydrates from index when available and computes incremental updates.
- RootCache: Owns verified root sets, descriptions, and the static root list.
- ThreadCache: Tracks thread-root counts and thread-related aggregates.
- TypeMetadataCache: Minimal placeholder in Phase 1; expand in Phase 2.
- MethodTableCache: Compatibility layer mapping method-table → type metadata.

## Public & Internal API

- Do not change any public method signatures on `HeapAnalysisCache`.
- Internally forward calls to the new cache components.
- Each cache should offer a small surface: `EnsureBuilt()`, `Clear()` (where applicable), and `GetMetrics()`.
- Caches should avoid direct circular dependencies; prefer explicit, narrow interfaces.

## Caveats and Important Constraints

- Backwards compatibility: existing analyzers must see identical behavior. Add automated regression tests that exercise `HeapAnalysisCache` public surface.
- Index availability: caches must handle three index states gracefully — not present, memory-loaded, or on-disk. Prefer lazy, idempotent loading.
- Concurrency: `EnsureBuilt()` may be invoked concurrently by analyzers. Implement light-weight synchronization (double-checked locking or `Lazy<T>`-style patterns) and ensure cancellation/failure leaves cache in a consistent, retryable state.
- Partial failures: if a dependent cache fails during initialization, `HeapAnalysisCache` should degrade gracefully (log and expose a read-only limited mode) rather than throwing for all callers.
- Performance: keep hot-paths allocation-free where possible (use `Span<T>`, `ArrayPool<T>`, readonly structs). Avoid LINQ in hot loops.

## Improvements Added

- Explicit migration checklist (below) to move responsibilities incrementally.
- Testing checklist and minimal observability guidance (metrics and health endpoints).
- Failure and concurrency guidance to avoid race conditions during analyzer startup.

## Migration Checklist (recommended incremental steps)

1. Add `HeapIndexCache` and delegate index-related methods from `HeapAnalysisCache` to it. Add unit tests for index load paths.
2. Add `StatisticsCache`; wire type-aggregate reads to delegate to it but keep `HeapAnalysisCache` adapter methods.
3. Add `RootCache` and `ThreadCache` similarly, one at a time, verifying that analyzer behavior is unchanged after each step.
4. Add `TypeMetadataCache` as a minimal pass-through; expand in Phase 2.
5. Add `MethodTableCache` as compatibility layer and migrate callers gradually.
6. Remove direct implementations in `HeapAnalysisCache` once all responsibilities are delegated and tests pass.

For each step:
- Create unit tests that call the public `HeapAnalysisCache` API surface.
- Create an integration test that boots analyzers against a small synthetic dump and verifies identical results before/after the change.

## Testing Checklist

- Unit tests for each new cache (`EnsureBuilt()`, load-failure, clear semantics).
- Regression tests that call `HeapAnalysisCache` public methods and compare outputs pre/post refactor.
- Concurrency tests that call `EnsureBuilt()` from multiple threads and assert single initialization.
- End-to-end integration test: run a fast analyzer suite against a small dump and compare top-level findings.

## Observability & Metrics

- Each cache should export lightweight metrics: `build_duration_ms`, `last_build_status{success|failure}`, `entry_count`, and `memory_usage_bytes`.
- Expose a health API on `HeapAnalysisCache` that reports which caches are online and their last build status.
- Log startup/init failures with structured context (cache name, exception, index path/state).

Status: basic metrics and health API implemented in the codebase. Each cache exposes `GetMetrics()` and `HeapAnalysisCache` exposes `GetCacheMetrics()` and `GetHealth()` returning an aggregated `HeapCacheHealth` object. Next recommended steps are unit tests for the metrics surface and adding retention/aggregation if needed for long runs.

## Concurrency & Failure Modes (details)

- Use an idempotent initialization pattern. Example (pseudo):

```
if (state == Built) return;
lock(sync)
{
	if (state == Built) return;
	try { Build(); state = Built; }
	catch { state = Failed; throw; }
}
```

- On failure, mark cache `Failed` and allow a background/explicit retry path. Do not leave global system unusable.

## Open Questions / Notes

- Monitoring retention: decide how long to retain aggregated metrics for large runs.
- If analyzer startup latency increases, consider lazy-loading heavy caches only when first used by an analyzer.

---

## Appendix: Quick compatibility rules

- Never remove or rename public `HeapAnalysisCache` methods in Phase 1; only delegate.
- Keep behavior stable for existing on-disk index locations and memory-backed paths.

---

Updated: migration checklist, concurrency guidance, testing and observability notes.

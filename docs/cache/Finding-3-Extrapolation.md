# Finding 3 — Extrapolation: Redundant root enumeration in memory mode

- **Summary:** `RootCache.GetOrBuildValidRoots` fast-paths only for disk `RootIndex.bin` and
  does not consult `heapIndex.InMemoryRootCandidates`. As a result, memory-mode
  analyses re-run `heap.EnumerateRoots()` in `EnsureRootCaches` even though
  `MemoryBackedObjectIndexWriter` already collected root candidates.

- **Immediate impact:**
  - **Perf:** repeated full root walk increases wall-clock analysis time proportional to root count; duplicative work when multiple `RootCache` consumers run.
  - **Memory:** streaming enumeration avoids lasting memory pressure, but repeated walks add short-lived allocations and CPU.
  - **Maintainability:** duplicated paths raise risk of behavioural drift between consumers.

- **Root causes:** inconsistent abstraction boundaries — `RootIndexReader` knows about `InMemoryRootCandidates`, but `RootCache` does not reuse it.

- **Short fix (low-risk):** branch in `RootCache.GetOrBuildValidRoots` on
  `heapIndex.StorageKind == Memory` and call `RootIndexReader.ReadRootCandidates`
  (or a small provider API) to hydrate caches from `InMemoryRootCandidates`.
  Add a guarded fallback to `heap.EnumerateRoots()` when in-memory candidates are absent.

- **Medium / long-term:**
  - Centralize root-provision via an `IHeapRootProvider` used by `RootCache`, `GCRootAnalyzer`, and others.
  - Memoize the provider so multiple consumers share a single enumeration result.
  - Expose metrics (counts/time) to detect regressions.

- **Trade-offs / risks:**
  - Minimal change is low-risk. A broader refactor improves design but requires coordination and tests.
  - If `InMemoryRootCandidates` diverges in sampling semantics from live enumeration, switching consumers may change downstream behavior — add tests to validate semantics.

- **Testing & validation guidance:**
  - Add an integration test asserting `RootCache` and `GCRootAnalyzer` see identical root sets in memory mode.
  - Add a microbenchmark measuring time saved by using `InMemoryRootCandidates` vs `EnumerateRoots()`.
  - Add a regression test ensuring presence of `InMemoryRootCandidates` prevents redundant `EnumerateRoots()`.

- **Recommendation:** implement the low-risk branch in `RootCache` to hydrate from
  `InMemoryRootCandidates`, add the fallback and lightweight tests/metrics, then
  plan the centralization refactor as a follow-up.

---

_Authored: 2026-07-13 — extrapolation for `Finding 3` requested by reviewer._

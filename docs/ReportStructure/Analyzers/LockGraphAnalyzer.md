# LockGraphAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **7** · Effort: Low · ✅ **Completed**

## Report Sections Served
- §7.2 Synchronization Patterns (contested locks, top contested types)
- §7.3 Deadlock Detection (circular wait candidates)

---

## Currently Produces
- `LockGraphDomainResult`: held locks count, contested locks count, deadlock candidates count
- `TopContestedTypes` — types most frequently contested

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Deadlock cycle path (which threads form the cycle) | §7.3 | High |
| Lock wait duration estimate (not available in static dump) | §7.3 | N/A |
| Thread IDs involved in each deadlock candidate | §7.3 | High |

---

## Required Changes

1. **Add `DeadlockCandidateDetails`** — `IReadOnlyList<DeadlockCandidateSnapshot>` to
   `LockGraphDomainResult`. Currently `DeadlockCandidates.Count` is surfaced but the
   candidate list itself is not emitted. New record:
   ```
   DeadlockCandidateSnapshot(
       IReadOnlyList<uint> ThreadIds,
       IReadOnlyList<uint> OSThreadIds,
       IReadOnlyList<string> LockObjectTypes,
       string CycleSummary)
   ```
2. **Add `ContestedLockDetails`** — `IReadOnlyList<ContestedLockSnapshot>` — the top contested
   lock objects with address, type name, waiting thread IDs, owner thread ID. The internal
   `ContestedLocks` list already exists in `LockGraphAnalysis`; it just isn't mapped to the
   domain result.

---

## Phase Assignment

### Phase Assignment — Entirely Phase 2

Lock graphs require **thread-to-lock ownership mapping** from `runtime.Threads` and monitor
lock inspection on heap objects. This is runtime state that:
- Cannot be streamed during Phase 1 (requires thread context correlation)
- Cannot be pre-indexed (lock ownership is dynamic, not object metadata)

**No Phase 1 contribution possible or needed.** `LockGraphAnalyzer` remains entirely Phase 2.

The required changes (surfacing `DeadlockCandidateDetails`, `ContestedLockDetails`) are
pure Phase 2 additions — the internal `LockGraphAnalysis` data already has the cycles;
they just need to be mapped to the domain result.

---

## Related Analyzers
- **`ThreadAnalyzer`** — `WaitCategoryDistribution` complements contested lock data for §7.2
- **`ThreadStackClusterAnalyzer`** — `LockHolderClusterCount` cross-references which thread clusters hold locks
- **`HangAnalyzer`** (post-split) — blocking thread analysis is the hang-detection complement to deadlock detection

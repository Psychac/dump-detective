# WeakReferenceAnalyzer — Phase 1 Audit

**Analyzer:** `WeakReferenceAnalyzer.cs`
**Audit date:** 2026-08-03
**Protocol:** `phase1-analyzer-architecture-review.md`

---

## Inputs Reviewed

| Artifact | File |
|---|---|
| Analyzer | `WeakReferenceAnalyzer.cs` |
| Domain result | `WeakReferenceDomainResult.cs` |
| Options | `WeakReferenceAnalysisOptions.cs` |
| Finding generator | `WeakReferenceFindingGenerator.cs` |
| Section builder | `WeakReferenceSectionBuilder.cs` |
| Trend comparer | `WeakReferenceTrendComparer.cs` |
| Infrastructure | `HandleSnapshot.cs`, `HandleSnapshotProvider.cs`, `DiskHandleSnapshotReader.cs`, `MemoryHandleSnapshotReader.cs` |
| Index | `HeapIndexBuildResult.cs` (InMemoryHandleSnapshot, TypeAggregates) |
| Tests | `WeakReferenceFindingGeneratorTests.cs`, `WeakReferenceOptionsTests.cs`, `WeakReferenceAnalyzerDiscrepancyTests.cs` |

---

## Audit Area 1 — Role & Opportunity Assessment

### Current role

The analyzer covers three distinct GC-handle subsystems as documented in its XML summary:

- **Phase A (§24.1):** GC weak-handle population — counts WeakShort, WeakLong, WeakWinRT handles and measures target liveness ratio.
- **Phase B (§24.2):** `WeakReference<T>` / `System.WeakReference` object analysis — counts wrapper objects, bytes consumed, and approximates stale wrapper count by probing `m_handle`.
- **Phase C (§24.3):** `ConditionalWeakTable` dead-key detection — counts dependent handles whose primary key has been collected.

### Coverage assessment

The three-phase structure gives good overall coverage of the weak-reference ecosystem. The problem domain is coherently scoped.

**Coverage gaps:**

- **WeakShort vs WeakLong semantic gap.** The analyzer treats both identically in counting but never explains their semantic difference (WeakShort is tracked before finalization; WeakLong survives finalization). An application with many dead WeakLong handles in Finalization Queue objects is a different problem from dead WeakShort handles. No breakdown or separate signal exists.
- **Dependent-handle value types absent.** Phase C counts dead-key dependent handles but extracts nothing about the value (secondary) objects attached to them. ConditionalWeakTable stores associated data in the secondary — knowing the secondary type reveals what data is being orphaned.
- **WeakWinRT handles are counted but not described.** The analyzer increments the WeakWinRT kind counter but produces no explanation or separate signal. These are COM/WinRT interop handles and their presence in managed code is often unexpected.
- **Phase B fallback absent.** When `typeAggregates` is null (no heap index), Phase B executes zero work and silently reports `weakRefObjCount = 0`. There is no fallback heap scan for `System.WeakReference`/`System.WeakReference\`1` objects. An engineer receiving zeroes would have no indication that the index was missing.
- **No holder-type tracking.** `staleHolderTypeHits` is declared and passed to the result's `TopStaleWrapperHolderTypes` field, but **is never populated** (see Area 6 — Correctness). The section builder renders the table conditionally, so it is always absent.
- **No absolute-count threshold.** The finding generator fires only on `DeadTargetRatio >= 0.5`. A process with 10 dead handles out of 20 triggers a warning; a process with 200,000 dead handles out of 500,000 (60% ratio) produces the same warning. An absolute count threshold (e.g., > 10,000 dead handles regardless of ratio) is missing.

### Expansion opportunities

- Track pending-finalization objects that are the target of WeakShort handles — correlates with `FinalizableObjectAnalyzer`.
- Surface the secondary (value) type breakdown from dependent handles.
- Add a fallback heap-scan path for Phase B when TypeAggregates is unavailable.

### Architectural observations

`GCHandleAnalyzer` has a `// TODO` noting that it should consume `HeapIndexBuildResult.InMemoryHandleSnapshot` — `WeakReferenceAnalyzer` correctly does this. The shared infrastructure exists; the TODO is a maintenance gap in the sibling analyzer, not a platform deficiency.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Overview finding is always emitted, providing a guaranteed baseline for every report.
- Dead-ratio thresholds (0.5 Warning / 0.8 Critical) are clear, documented implicitly by constant usage.
- Trend comparer covers all meaningful metrics with `HigherIsWorse` direction correctly applied.
- `ScanCapped` flag surfaces via both a section block and the finding text.

### Weaknesses

**W1 — `TopStaleWrapperHolderTypes` is always empty.**
`staleHolderTypeHits` is allocated but never written to (see Area 6). The section builder conditionally renders the table only when `Count > 0`, so the table is silently absent from every report. Engineers cannot identify which types accumulate stale wrappers.

**W2 — Stale wrapper count approximation is not explained.**
The finding text states `"Stale WeakReference wrappers: {r.StaleWrapperCount:N0}"` with no qualification that this is a sample-based estimate, not an exact count. The approximation error can be 100% in either direction (see Area 6). Users may treat the number as authoritative.

**W3 — Hard-coded cap value in finding text.**
`WeakReferenceFindingGenerator` emits: `"scan capped at 50 000 handles"` as a string literal, regardless of the configured `HandleScanCap`. If the cap is 200,000 in Full profile, the finding text is wrong.

```csharp
// FindingGenerator.cs — literal string does not match options.HandleScanCap
string scanNote = r.ScanCapped ? " (scan capped at 50 000 handles)" : string.Empty;
```

**W4 — Only one signal reported even when two trigger.**
The generator resolves a single `top` signal among all candidates. When both the dead-ratio signal and the dependent-handle signal fire, only the higher-severity one appears. The second finding is silently dropped. Engineers debugging ConditionalWeakTable issues alongside stale caches would miss the dependent-handle signal entirely.

**W5 — No alive/dead split by handle kind.**
The `WeakHandleKinds` table shows counts per kind (WeakShort, WeakLong, WeakWinRT) but not their alive/dead split. A WeakLong handle pointing to a dead object is semantically different from a WeakShort pointing to one; the current report cannot distinguish them.

**W6 — No size context for alive targets.**
`TopWeakTargetTypes` shows counts of alive weak targets but not their sizes. A type with 5 instances of 50 MB each is far more interesting than one with 1,000 instances of 32 bytes each.

**W7 — Phase B zero-result case is silent.**
When `typeAggregates` is null, `WeakReferenceObjectCount = 0` is reported. This looks identical to a heap with no WeakReference objects. There is no diagnostic text, log message, or flag in the result indicating the analysis was skipped.

### Missing diagnostics

- Per-kind alive/dead breakdown (WeakShort alive, WeakShort dead, WeakLong alive, WeakLong dead).
- Secondary (value) type breakdown for dependent handles with dead keys.
- Annotation when Phase B was skipped due to missing TypeAggregates.
- Total size of alive vs dead weak targets.
- Absolute count threshold finding (fire when `DeadWeakTargets > N` regardless of ratio).

### Report improvements

- Fix the hard-coded cap string in `WeakReferenceFindingGenerator`.
- Emit both signals when both thresholds are met, not just the highest.
- Add a `(estimated)` qualifier to the stale wrapper count in evidence text.
- Add a "Phase B skipped — heap index unavailable" block when TypeAggregates is null.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD usage

**Good:**
- `heap.GetObject(addr).IsValid` is correctly checked before reading type names — tolerant of invalid addresses.
- `sample.Type.GetFieldByName("m_handle")` followed by `mHandleField.Read<nint>(address, interior: false)` is the correct ClrMD pattern for reading an `IntPtr` field.
- Type name matching uses `StartsWith` for generic `System.WeakReference\`1[...]` and `string.Equals` for the non-generic — both are correct.

**Gaps:**
- **`heap.GetObject` called twice per handle in MemoryHandleSnapshotReader and WeakReferenceAnalyzer.** `MemoryHandleSnapshotReader.EnumerateRecords` calls `heap.GetObject(addr)` to resolve the MT for the record. Then `WeakReferenceAnalyzer` calls `heap.GetObject(addr)` again on the returned `HandleRecord.Address` to check liveness. This is two ClrMD object lookups per handle in memory mode. The record could carry a `bool IsAlive` pre-computed during enumeration.
- **`ClrHandle.DependentTarget` unused.** ClrMD exposes `ClrHandle.DependentTarget` — the associated secondary object for dependent handles. Phase C ignores it, missing value-type attribution for dead-key dependent handles.
- **`ClrHandle.HandleKind` equality not leveraged.** The handle-kind check uses `rec.Kind != KindWeakShort && rec.Kind != KindWeakLong && rec.Kind != KindWeakWinRT` — this is correct for the current set, but the exclusion list must be maintained manually. A whitelist approach (`IsWeakKind(rec.Kind)`) would be less error-prone.

### Platform utilization

**Good:**
- Correctly prefers `heapIndex.InMemoryHandleSnapshot` over a live `runtime.EnumerateHandles()` call — avoids a second expensive enumeration.
- Falls back to disk (`HandleSnapshotProvider.CreateFromDiskIfExists`) before falling back to live enumeration.
- Correctly uses `TypeAggregateIndexEntry.SampleAddress` and `TypeAggregateIndexEntry.Count` to avoid a full heap object scan in Phase B.

**Gaps:**
- **Two separate passes over the handle snapshot.** Phases A and C each independently acquire an `IHandleSnapshotReader` (or iterate `InMemoryHandleSnapshot`). For disk-backed mode this means two full sequential reads of `HandleSnapshot.bin`. Merging phases A and C into one pass would halve disk I/O.
- **Phase B has no fallback.** When `typeAggregates` is null, the entire Phase B is skipped with no fallback to `heap.EnumerateObjects()` filtered by type name. Other analyzers use filtered heap scans when the index is absent.
- **`ObjectScanCounter` only in Phase A (reader path).** Phase C's reader loop has no `ObjectScanCounter`. Long-running dependent-handle enumeration on large dumps produces no progress feedback.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-value missing diagnostics

**4.1 Per-kind alive/dead breakdown (High)**
Distinguish WeakShort vs WeakLong vs WeakWinRT in the alive/dead counts. WeakLong handles that are dead indicate GC is suppressing finalization to service the handle — a different GC pressure pattern than dead WeakShort handles.

**4.2 Dependent handle value-type breakdown (High)**
For dead-key dependent handles, enumerate `ClrHandle.DependentTarget` to identify the secondary object type. This reveals what data `ConditionalWeakTable` stores for the orphaned keys — critical for diagnosing `ConditionalWeakTable` misuse (e.g., storing large value objects keyed on disposed objects).

**4.3 GC generation distribution of alive/dead weak targets (Medium)**
Use `heap.GetObject(addr).Type?.IsValueType` and the object's segment generation to show which GC generation the alive and dead targets came from. Dead Gen2 objects that are weak targets indicate long-lived stale registrations, while dead Gen0 objects are routine churn.

**4.4 Size breakdown for alive weak targets (Medium)**
`TopWeakTargetTypes` tracks count but not size. Adding `TotalSizeBytes` to `NameCountEntry` or introducing a parallel `TopWeakTargetTypesBySize` list enables engineers to identify which types contribute the most retained pressure through weak references.

**4.5 Absolute count threshold finding (Medium)**
A `DeadWeakTargets > AbsoluteDeadCountThreshold` signal (e.g., default 10,000) is complementary to the ratio signal. Large-scale applications with millions of handles can have a benign 40% dead ratio numerically but still have 400,000 stale handles accumulated.

**4.6 WeakReference object growth velocity (via trend, Low)**
The trend comparer already tracks `weakref.objects.bytes`. Adding a second metric `weakref.stale.ratio` (staleWrapperCount / weakRefObjCount) would make cross-dump trend reporting more sensitive to gradual stale accumulation.

**4.7 Namespace/assembly attribution for stale wrappers (Low)**
Once holder type tracking is fixed, grouping stale holders by namespace prefix (e.g., `Microsoft.Extensions.Caching.*` vs `MyApp.Services.*`) would help engineers quickly narrow the investigation scope.

---

## Audit Area 5 — Performance, Memory & Scalability

### Performance assessment

**P1 — Two passes over handle snapshot (disk-backed mode).**
Phases A and C each call `HandleSnapshotProvider.CreateFromDiskIfExists(heapIndex.IndexPath)` and iterate the entire `HandleSnapshot.bin` section. On a 10 GB dump this file can contain 100k+ records across two full sequential disk reads. Merging the two into one pass is a straightforward refactor.

```csharp
// Current: two readers opened sequentially
// Phase A: reader = HandleSnapshotProvider.CreateFromDiskIfExists(...)
// Phase C: reader = HandleSnapshotProvider.CreateFromDiskIfExists(...)

// Proposed: single reader, route per-record to Phase A or Phase C bucket
foreach (var rec in reader.EnumerateRecords(cancellationToken))
{
    if (IsWeakKind(rec.Kind))   { /* Phase A logic */ }
    if (rec.Kind == KindDependent) { /* Phase C logic */ }
}
```

**P2 — Double `heap.GetObject` per handle in memory mode.**
`MemoryHandleSnapshotReader.EnumerateRecords` calls `heap.GetObject(addr)` to populate `MethodTable` in the record. `WeakReferenceAnalyzer` then calls `heap.GetObject(addr)` again to check `IsValid`. For 50,000 handles (Balanced cap) this is 100,000 `heap.GetObject` calls where 50,000 would suffice.

**P3 — Phase B probe cap `WeakRefProbeSampleLimit = 8` (Balanced default).**
With only 8 probes at Balanced profile, if an application has 50 distinct `WeakReference<T>` closed generic types, 42 of them receive zero stale analysis. The default is aggressively conservative. Raising to a higher value (e.g., 50 for Balanced, 500 for Full) costs proportional `mHandleField.Read<nint>` calls — each is a single ClrMD field read, not a heap scan.

**P4 — No progress reporting in Phase C reader path.**
Phase C's reader loop does not use `ObjectScanCounter`. On a dump with 500k handles, the dependent-handle pass is silent.

**P5 — `HandleScanCap` may be too low for large services.**
At Balanced=50,000 and Full=200,000, a high-traffic ASP.NET Core or WCF service with event-heavy patterns can easily exceed these counts. When the cap is hit, `ScanCapped = true` but the results may be significantly skewed — the ratio is computed on partial data.

### Memory assessment

No materialization of full handle lists. `targetTypeHits` and `weakHandleKinds` dictionaries are bounded by distinct type names (O(types)), not by handle count. Phase B uses aggregate index entries, not per-object materialization. Memory usage is well-controlled.

**One issue:** When `ProduceRawExports = true`, `sampleRecords` collects up to 100 `object` records via `new { address, methodTable, kind }`. These are anonymous types boxing three value fields each. For 100 records this is negligible, but anonymous object boxing avoidable with a named struct.

### Scalability assessment

The design scales well on large dumps when the disk index is available. The primary scalability risk is the double-pass over `HandleSnapshot.bin` on disk. In memory mode, two iterations over an in-memory array are cheap. The `HandleScanCap` provides a hard bound in live-enumeration fallback mode.

---

## Audit Area 6 — Correctness & Confidence

### Bug 1 (Critical) — `staleHolderTypeHits` never populated

`staleHolderTypeHits` is declared and passed to `TopStaleWrapperHolderTypes` in the result, but **no code ever writes to it**.

```csharp
// Phase B — staleWrapperCount is incremented:
staleWrapperCount += (int)Math.Min(entry.Count, int.MaxValue);

// But staleHolderTypeHits is never touched.
// IncrementDict(staleHolderTypeHits, ???) — missing
```

`TopStaleWrapperHolderTypes` is always an empty list. The section builder guards on `Count > 0`, so the table silently disappears. Every existing test uses `TopStaleWrapperHolderTypes: []` in construction — the tests do not detect this gap.

**Impact:** Engineers are told "N stale wrappers" but cannot determine which types hold them. The primary diagnostic value of Phase B is absent.

### Bug 2 (High) — GZip stream not finalized in InMemory export path

When `ProduceRawExports = true` and `heapIndex.InMemoryHandleSnapshot` is populated, `WriteExportRecord` writes to `tmpGz`. However, after the InMemoryHandleSnapshot foreach loops in Phases A and C, `tmpGz` is **never disposed or flushed**. The `try { tmpGz?.Dispose(); tmpGz = null; }` calls exist only inside the reader-path `else` branches.

The artifact attachment block at the end checks `File.Exists(tmpNdjsonPath)` but never calls `tmpGz.Dispose()` — the file exists but the GZip trailer is not written, leaving the file corrupt and unreadable by standard tools.

```csharp
// InMemory Phase A path — no dispose after foreach
foreach (var rec in inMem) { ... WriteExportRecord(...); }
// ← tmpGz is open here, never closed before Phase B starts

// InMemory Phase C path — same issue
foreach (var rec in inMemHandles) { ... }
// ← tmpGz still open, GZip trailer never written
```

**Impact:** Every `ProduceRawExports = true` run in memory-index mode produces a corrupt `.ndjson.gz` artifact.

### Bug 3 (Medium) — Hard-coded cap string in finding generator

`WeakReferenceFindingGenerator` always emits `"scan capped at 50 000 handles"`. The actual cap used depends on profile and configuration — 20,000 (Fast), 50,000 (Balanced), 200,000 (Full), or a user override. The finding text is wrong for any non-Balanced run where the cap was hit.

```csharp
// FindingGenerator.cs
string scanNote = r.ScanCapped ? " (scan capped at 50 000 handles)" : string.Empty;
```

The `WeakReferenceDomainResult` does not carry the actual cap value, so the generator cannot fix this without a model change.

### Bug 4 (Medium) — Stale wrapper approximation error range undisclosed

Phase B probes one sample instance per `WeakReference<T>` closed generic type group and attributes the stale/alive state to the entire group. If the sampled instance is alive, all `entry.Count` instances are treated as alive; if stale, all are treated as stale.

For a type with 10,000 instances where 5,000 are stale, the result will be either 0 or 10,000 stale wrappers — never the true count. The finding text presents `StaleWrapperCount` as a plain number without any indication that it is a group-sample approximation.

### Confidence risks

| Risk | Severity | Notes |
|---|---|---|
| staleHolderTypeHits always empty | Critical | Full diagnostic column missing |
| GZip corruption in InMemory export | High | Artifact unusable |
| Stale wrapper count accuracy | Medium | Can be 0–100% off per type group |
| Capped scan skews ratio | Medium | Ratio calculated on partial data |
| Phase B zero-result with no flag | Medium | Indistinguishable from real zero |
| Cap literal in finding text | Medium | Wrong for non-Balanced profiles |

### Edge cases unsupported

- Dumps with handles pointing to invalid/free segments (not just `addr == 0`) — `heap.GetObject(addr).IsValid` handles this correctly.
- No guard against `entry.Count` overflow from `ulong` to `int` when cast via `Math.Min` — the cast uses `Math.Min(entry.Count, int.MaxValue)` which is correct.
- Phase B `probesDone` check fires before the type-matching loop's contribution is counted — `if (probesDone >= probeLimit) break;` uses `probesDone` which is only incremented after a successful field read, not per entry. This is correct behavior but the cap applies only to instances where `m_handle` was actually read, not to the type-aggregation count.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| SOS capability | DumpDetective coverage |
|---|---|
| `!gchandles` — type+kind breakdown | Covered: WeakHandleKinds + TopWeakTargetTypes |
| `!gchandles -type WeakShort/WeakLong` — filtered list | Not covered: no per-kind alive/dead split |
| `!gchandles -type Dependent` — key+value object pairs | Partially covered: dead-key count; value type missing |
| `!dumpheap -type System.WeakReference` — all instances | Covered via TypeAggregates in Phase B |
| Dead target identification per handle | Covered at aggregate level; no per-object listing |

**Gap:** SOS allows drilling to specific dead-handle targets by address. DumpDetective provides only aggregate statistics — no per-instance listing even for small populations.

### PerfView

PerfView's GC heap view can show WeakReference object counts by type. DumpDetective Phase B covers this equivalently via TypeAggregates. PerfView has no ConditionalWeakTable dead-key analysis — DumpDetective leads here.

### JetBrains dotMemory

dotMemory shows "Incoming references" including weak references, and can flag objects held only via weak references. DumpDetective does not yet provide a "held only via weak reference" classification — this requires cross-referencing weak target addresses against the reference graph, which is a non-trivial but high-value capability.

### Competitive gaps

- **"Held only via weak reference" flag.** Objects reachable only through weak handles would be collected on the next GC. Identifying them helps confirm whether a suspected leak is actually still strongly rooted. This requires joining the handle snapshot against `ReverseReferenceIndex`.
- **Per-instance listing for small populations.** When total weak handles is below a configurable threshold (e.g., < 1000), a per-instance listing (address, type, alive/dead) would match the WinDbg workflow for targeted investigation.
- **ConditionalWeakTable value-type report.** No .NET tool other than raw WinDbg provides this. DumpDetective would be differentiated by including it.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

**Production readiness:** Conditionally ready. The analyzer's core liveness counting (Phase A) and ConditionalWeakTable dead-key counting (Phase C) are reliable. Phase B (WeakReference object analysis) has a critical data loss bug (`staleHolderTypeHits` never populated) and a GZip corruption bug in export mode. These prevent the stale-wrapper diagnostic from functioning at all.

**Major strengths:**
- Clean three-phase structure covering distinct subsystems coherently.
- Correct use of shared handle snapshot infrastructure and TypeAggregates index — avoids redundant heap scans.
- Disk / memory / live-fallback chain is properly ordered and tested.
- Trend comparer provides all relevant metrics with correct direction.
- Integration discrepancy test validates disk-vs-memory parity.

**Major weaknesses:**
- `staleHolderTypeHits` is never populated — the primary Phase B diagnostic (which types accumulate stale wrappers) is absent from every report.
- GZip export is corrupt in InMemory mode — `tmpGz` is never disposed in the InMemory snapshot paths.
- Hard-coded cap literal in finding text.
- Two full passes over `HandleSnapshot.bin` where one would suffice.
- Phase B has no fallback when TypeAggregates is absent.
- No per-kind alive/dead breakdown for WeakShort/WeakLong.

---

### Priority Roadmap

| # | Recommendation | Area | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|---|
| P0-1 | **Fix `staleHolderTypeHits` never populated in Phase B** — record holder type per MT group during stale probe loop | Correctness | High | Low | High | Improvement | ✅ Done (commit aea3761) |
| P0-2 | **Fix GZip stream not disposed in InMemory export paths** — add `tmpGz?.Dispose(); tmpGz = null;` after both InMemory foreach loops | Correctness | High | Low | High | Improvement | ✅ Done (commit aea3761) |
| P1-1 | **Fix hard-coded "50 000" cap literal** — add `ScanCapUsed` int to `WeakReferenceDomainResult`; emit it in finding text | Diagnostic | Medium | Low | High | Improvement | ✅ Done (commit 4785d51) |
| P1-2 | **Merge Phase A and Phase C into a single handle-snapshot pass** — eliminates one full read of HandleSnapshot.bin on disk | Performance | High | Medium | High | Improvement | ✅ Done (commit f4caad1) |
| P1-3 | **Add Phase B fallback heap scan** when `typeAggregates` is null — filter `heap.EnumerateObjects()` by `WeakRefGenericName`/`WeakRefNonGenericName`; add `PhaseBSkipped` flag to result | Correctness | Medium | Medium | High | Improvement | ✅ Done (commit f123412) |
| P1-4 | **Emit both signals** from `WeakReferenceFindingGenerator` when both the dead-ratio and dependent-handle thresholds are met | Diagnostic | Medium | Low | High | Improvement | ✅ Done (commit 7ebe9bd) |
| P2-1 | **Add per-kind alive/dead breakdown** — track `aliveByKind` and `deadByKind` dicts; add to domain result and section builder | Diagnostic | High | Medium | High | Improvement | Pending |
| P2-2 | **Add absolute dead-count threshold signal** (configurable, default 10,000) alongside the ratio signal | Diagnostic | Medium | Low | High | Improvement | ✅ Done (commit f4c7461) |
| P2-3 | **Eliminate double `heap.GetObject` in MemoryHandleSnapshotReader** — add `bool IsAlive` pre-computed to `HandleRecord` or expose it as a property | Performance | Medium | Low | High | Improvement | ✅ Done (commit f4c7461) |
| P2-4 | **Add `ObjectScanCounter` to Phase C reader path** for progress reporting on large dumps | Performance | Low | Low | High | Improvement | ✅ Done (commit f4c7461) |
| P2-5 | **Raise `WeakRefProbeSampleLimit` defaults** — Balanced: 50, Full: 500; the per-probe cost is a single ClrMD field read | Performance | Medium | Low | High | Improvement | ✅ Done (commit f4c7461) |
| P3-1 | **Expose dependent-handle value types** — use `ClrHandle.DependentTarget` in a Phase C extension to identify secondary types for dead-key entries | Diagnostic | High | Medium | Medium | Improvement | Pending |
| P3-2 | **Add "held only via weak reference" detection** — join WeakTarget addresses against `ReverseReferenceIndex`; flag objects with no strong incoming edges | Diagnostic | High | High | Medium | Evolution | Pending |
| P3-3 | **Add GC generation distribution** for alive/dead weak targets using object segment metadata | Diagnostic | Medium | Medium | Medium | Improvement | Pending |
| P3-4 | **Add `(estimated)` qualifier** to stale wrapper count in evidence text; document approximation method | Diagnostic | Low | Low | High | Improvement | Pending |

---

### Final Verdict

1. **Is the analyzer production-ready?**
   ✅ **Yes.** Phase A (handle liveness), Phase B (WeakReference stale analysis), and Phase C (dependent handle dead keys) are all production-quality. P0-1 and P0-2 have been resolved (commit aea3761).

2. **Highest-impact improvements?**
   P0-1 (populate `staleHolderTypeHits`) and P0-2 (fix GZip disposal) restore Phase B to functioning state. P1-2 (merge passes) halves disk I/O for the most common production case. P2-1 (per-kind alive/dead breakdown) significantly increases diagnostic value for WeakLong finalization issues.

3. **Platform evolution opportunities?**
   P3-2 ("held only via weak reference" detection) would position DumpDetective ahead of all commercial tools in this area. It requires a join against `ReverseReferenceIndex` — an index that already exists in the platform for other analyzers — making it a feasible cross-analyzer collaboration.

4. **Highest engineering return?**
   ✅ P0-1 and P0-2 are complete (commit aea3761). Remaining quick wins: P1-4, P1-1, and P2-2 are low-effort fixes with high diagnostic impact. P1-2 and P2-3 are medium-effort performance improvements that will further enhance correctness and signal quality.

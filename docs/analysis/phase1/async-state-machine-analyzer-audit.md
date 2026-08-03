# AsyncStateMachineAnalyzer — Phase 1 Audit

**Analyzer:** `AsyncStateMachineAnalyzer` (`src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs`)
**Protocol:** Phase 1 Analyzer Audit (`phase1-analyzer-architecture-review.md`)
**Date:** 2026-08-03

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`AsyncStateMachineAnalyzer` covers three concerns:

1. **State machine population** — identifies all compiler-generated async state machine types on the heap using `TypeAggregates` name matching (`<MethodName>d__N` pattern + `IAsyncStateMachine` interface check) and reports instance counts and total bytes per type.
2. **Captured closure analysis** — reads reference fields from each type's `SampleAddress` to estimate how many bytes each state machine instance holds via its captured variables.
3. **Suspended method map** — aggregates state machine instances by `(DeclaringType, MethodName)` to expose which async methods have the most suspended instances, regardless of how many compiler-generated types they produce.

The three concerns are cohesive and the analyzer correctly delegates all heap scanning to the pre-built `TypeAggregates` index — no full heap scan is performed.

### Coverage Gaps

- **No `IsAsyncStateMachineType` flag.** `TypeAggregateFlags` has dedicated bits for `IsStringType`, `IsTaskType`, `IsDelegateType`, `IsFinalizableType`, and `IsArrayType`, but not for async state machines. The analyzer must iterate every entry in `TypeAggregates` (potentially 50K+ types in large applications) and apply regex matching and interface enumeration per candidate, where `AsyncTaskAnalyzer` uses a single flag-masked O(types) filter. This is the dominant performance and scalability risk.

- **No GC generation data.** `TypeAggregateIndexEntry` records `Gen0Count`, `Gen1Count`, `Gen2Count` per type, but `StateMachineTypeProfile` discards them entirely. A state machine promoted to Gen2 is a strong signal of a long-lived suspension (stuck await, never-completing task, or fire-and-forget). The current output cannot distinguish ephemeral suspensions from structural leaks.

- **No state value distribution.** A single `AvgStateValue` is read from one sample instance. In practice, different instances of the same type will be suspended at different await points (different non-negative state values). A histogram of observed state values per type is much more actionable — it identifies which specific awaits are accumulating.

- **No Task linkage.** Compiler-generated state machines drive a `Task` via `m_task` / the builder's backing task. The analyzer does not resolve that task, so there is no correlation between state machine instances and the `AsyncTaskAnalyzer` task population. `AsyncTaskAnalyzer`'s gap (no state machine correlation) is symmetrical.

- **`async void` methods.** `async void` state machines implement `IAsyncStateMachine` and use the same `d__N` naming. They are included in the population, but there is no flag or annotation distinguishing them from `async Task` methods. `async void` suspensions are more dangerous (unobservable failures, fire-and-forget by construction) and should be called out explicitly.

- **`IValueTaskSource<T>` state machines.** Custom `IValueTaskSource<T>` implementations can generate state machines that do not wrap a `Task` allocation. These are structurally identical on the heap but contextually different. The analyzer treats all state machines uniformly.

- **No capture-depth distinction.** The captured closure analysis reads only the direct reference fields of the state machine struct. Nested closures (a captured object that itself holds a closure or a large graph) are not followed. Shallow totals can significantly underestimate true retention.

### Unexpected Functionality

None. All logic serves async state machine diagnostics.

### Adjacent Capabilities

- `AsyncTaskAnalyzer` independently analyzes the `Task` side of the same async operations. The two analyzers are natural complements and share no data today.
- `ThreadAnalyzer` and `HangAnalyzer` both enumerate thread stacks. State machines waiting on `Task.Wait()` or `Task.Result` (sync-over-async) would appear in both thread stacks and the state machine population. Cross-referencing these would identify sync-over-async deadlock candidates.
- `LeakCandidateAnalyzer` uses Gen2/LOH counts to rank leak suspects. State machines in Gen2 warrant the same treatment.

### Architectural Observations

- The analyzer accesses the heap index via a direct cast to `HeapAnalysisCache` (`if (cache is HeapAnalysisCache heapCache ...)`). This pattern bypasses the `IHeapAnalysisCache` abstraction and is inconsistent with the `IHeapIndexBuilder.TryGetHeapIndex` interface exposed for exactly this purpose. When the abstraction boundary is tightened, this will break silently.
- `IsThreadSafe` is not declared on `IAnalyzer` (checked against actual interface source). The CLAUDE.md reference to `IsThreadSafe` is a documentation artefact.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- **Suspended method map** is the single most valuable table. It collapses all compiler-generated state machine variants for the same method into one row, immediately surfacing which async methods dominate the suspension population.
- **`ScanLimited` key metric** is emitted in the header when the candidate cap is hit, preventing silent data truncation.
- **Three distinct report tables** (types by count, instances by captured bytes, methods by suspension count) cover different investigation angles from the same data pass.
- **`InsightFinding` quality** is high: each finding includes well-formed `Evidence`, `Recommendation`, `Tags`, `MetricValue`, and `MetricUnit` fields.

### Weaknesses

- **`AvgStateValue` is not an average.** It is the state value read from the single `SampleAddress` instance. A single-instance state value is meaningless as a summary statistic. The field name, column header, and any downstream consumer will misinterpret it as a statistical aggregate. At minimum the column should be named `Sample State` or `State (sample)`; ideally it should be replaced with a distribution.

- **State value numbers have no interpretation guidance.** The report shows an integer (e.g., `2`) with no explanation that `-1` = before first await (not yet started), `-2` = completed, `0` = suspended at first await, `1` = second await, etc. An engineer unfamiliar with the compiler encoding cannot act on this column.

- **Capture bytes address is the sample address, not a named instance.** `HighCaptureStateMachine.Address` is set to `entry.SampleAddress` (the lowest-address instance of the type). The report renders it as `0x...`, implying it is the specific instance that has large captures. In reality it is the sample chosen by the index, which may not be the instance with the largest actual capture. The column heading "Address" implies exact instance identity.

- **Capture bytes are shallow and shared.** The estimate sums `refObj.Size` for all directly referenced objects. This over-counts shared objects (a `HttpClient` referenced by many state machines contributes its full size per sample), under-counts nested graphs, and ignores value-type fields entirely. No uncertainty annotation is present in the report.

- **Only one fire-and-forget finding is generated.** `AsyncStateMachineFindingGenerator` breaks after the first `SuspendedMethodEntry` that exceeds the threshold. In a service with multiple fire-and-forget sinks, only the worst is reported. The remaining offenders are silently dropped.

- **Fire-and-forget finding severity is always `Warning`.** There is no escalation based on the absolute count or proportion of suspended instances. A method with 50,000 suspended instances receives the same severity as one with 100.

- **`ScanLimited` impact not quantified.** When the 200-type cap is hit, the key metric says "Yes — type candidate cap hit; results may be partial" but gives no estimate of how many types may have been skipped or what fraction of state machine bytes is covered.

- **No section narrative for zero-result case.** When no state machines are found, the analyzer returns an empty result and the section builder produces no output. A brief note ("No async state machine types detected on this heap") aids readability.

### Missing Diagnostics

- State value histogram per top type (how many instances at each await point).
- Gen2 instance count and fraction per state machine type (long-lived suspension indicator).
- `async void` annotation on the type profile row.
- Estimated true retention via transitive reference following for top suspects (bounded BFS).
- Task linkage for top state machine types.

### Missing Statistics

- Median and P95 instance count across all identified state machine types.
- Total state machine memory as a fraction of total heap size.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

- **`EnumerateInterfaces()` per candidate is suboptimal.** `ImplementsIAsyncStateMachine` calls `type.EnumerateInterfaces()` for every regex-matched candidate type. With `TypeCandidateLimit = 200`, this is at most 200 calls, but the real cost is the pre-filter iteration over all `typeAggregates` entries. Every type must reach the regex pre-check before `EnumerateInterfaces` is called; the regex is only reached after a string `LastIndexOf('<')` and `Contains(">d__")` pass. The pre-filter is efficient, but the interface check adds latency on candidate types that match the pattern but are not state machines.
- **`ClrType.Fields` iteration is correct and efficient** — field metadata is stable, cached by ClrMD, and the inner loop is proportional to field count, not instance count.
- **`stateField.Read<int>(sample, interior: false)`** is the correct ClrMD API for reading a value-type field from a heap object. The `interior: false` is correct for a state machine struct on the heap. The try/catch for unreadable fields is appropriate defensive practice.
- **`f.ReadObject(sample.Address, interior: false)`** for reference fields is correct. The `interior: false` is appropriate since `sample.Address` is the object's base address.

### Infrastructure Utilization

- **`TypeAggregateFlags.IsAsyncStateMachineType` does not exist.** This is the most material infrastructure gap. Adding a bit during Phase 1 (checking `type.Name` pattern and `EnumerateInterfaces` once per unique MT at index time) would reduce Phase 2 detection to a single flag-masked scan over `typeAggregates` — identical to how `AsyncTaskAnalyzer` uses `IsTaskType`. The index scan is done in a hot parallel path where the type classification cost is amortized over all analyzers; paying it once at index time is strictly better than paying it per-analyzer.
- **`TypeAggregateIndexEntry.Gen0Count`, `Gen1Count`, `Gen2Count` are completely ignored.** These fields are populated during Phase 1 at zero additional cost and are directly relevant to the question "are these state machines long-lived?". Not surfacing them is the biggest diagnostic opportunity left on the table.
- **`FormatBytes` is duplicated** between `AsyncStateMachineAnalyzer` and `AsyncStateMachineFindingGenerator`. `FormatHelper.FormatBytes` exists in `DumpDetective.Core.Utilities` and is used by the section builder. Both private copies should be removed.
- **`cache is HeapAnalysisCache` direct cast** bypasses the `IHeapIndexBuilder` abstraction. Other analyzers use the same pattern (`GCRootAnalyzer`, `LeakCandidateAnalyzer`), but this creates implicit coupling to the concrete cache type.

### Index Recommendations

- **Add `TypeAggregateFlags.IsAsyncStateMachineType` (bit 5).** During Phase 1, when `ComputeTypeFlags` is called for a type, check whether the type name matches the `<...>d__N` suffix and `EnumerateInterfaces` returns `IAsyncStateMachine`. Set the flag once per unique method table. Phase 2 then filters on the flag and skips the regex entirely.
- The `TypeAggregateIndexEntry` binary record currently uses `bits 5–7 = reserved`. Bit 5 is available.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics

1. **State value histogram per type.** Instead of reading the state value from one sample, iterate all instances of the top-N types (bounded by count, e.g. max 1000 instances per type) and build a `Dictionary<int, int>` (state → instance count). A table showing "25% at await 0, 60% at await 1, 15% at await 3" directly identifies which specific awaits are accumulating and lets engineers correlate with source code (C# compiler emits `await` points in order).

2. **Gen2 fraction per state machine type.** `TypeAggregateIndexEntry.Gen2Count / Count` expresses the fraction of instances that have survived at least two GCs. A high Gen2 fraction for a state machine type means instances are genuinely stuck, not just transiently in-flight. This is the most reliable signal distinguishing a healthy high-throughput method from a leak.

3. **`async void` flag.** At Phase 1, or using `ClrType.Signature` / parent type method metadata, flag state machines whose originating method returns `void`. These carry special risk (unobservable faults) and warrant a dedicated finding.

4. **Task linkage.** State machines built around `AsyncTaskMethodBuilder<T>` hold a `m_task` field on the builder struct. Reading this field from the sample instance would link each state machine type to its backing `Task` and enable cross-referencing with `AsyncTaskAnalyzer`'s faulted/orphaned task list.

5. **Transitive capture size (bounded BFS, top suspects only).** For the top-5 types by estimated captured bytes, perform a bounded BFS (depth 3, max 500 objects) from the sample instance's reference fields to compute a more accurate retained estimate. This is analogous to `DominatorAnalyzer`'s selective deep traversal.

6. **Sync-over-async detection.** Cross-reference state machines waiting on `.Result` or `.Wait()` (inferrable from `ThreadAnalyzer`'s blocked thread stacks) with the suspended method map. State machines for `async Task` methods that are themselves awaited synchronously on a blocked thread are deadlock candidates.

### High-Value Statistics

- Total state machine memory as percentage of heap.
- Gen2 instance count and fraction (per type and aggregate).
- Ratio of distinct originating methods to distinct state machine types (high ratio = many overloads or lambdas).
- P50/P95 suspension count per method.

### Evidence Recommendations

- Replace `AvgStateValue` with `SampleState` + `State Distribution` (histogram of top state values for the type).
- Add `Gen2Count` and `Gen2 Fraction` columns to `StateMachineTypeProfile`.
- Add `IsAsyncVoid` flag to `StateMachineTypeProfile`.
- Annotate the capture estimate as "shallow estimate" and list only directly referenced objects above threshold.

### Priority-Ranked Opportunities

| Priority | Opportunity | Expected Impact |
|---|---|---|
| P0 | `IsAsyncStateMachineType` Phase 1 flag | Eliminates O(all types) scan; required for correctness at scale |
| P0 | Gen2 count/fraction in profile | Enables leak vs. throughput distinction |
| P1 | State value distribution | Identifies specific await points accumulating |
| P1 | Fix `AvgStateValue` naming and semantics | Removes actively misleading data |
| P1 | `async void` detection | High-risk pattern, actionable finding |
| P2 | Task linkage | Enables cross-analyzer correlation |
| P2 | Multiple fire-and-forget findings (remove `break`) | Surfaces all offenders, not just the worst |
| P2 | Fire-and-forget severity escalation based on count | Accurate triage |
| P3 | Bounded transitive capture BFS for top suspects | More accurate retention estimate |
| P3 | Sync-over-async cross-reference | High-value for deadlock investigation |

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap Scan

No heap scan is performed. All type identification is done via `TypeAggregates` dictionary iteration. This is the correct approach and scales with O(unique types) not O(heap objects).

### Type Iteration Cost

- **All type aggregates are iterated.** On a large application dump with 20,000–50,000 distinct types, every entry is visited. The pre-filter (`LastIndexOf('<')` + `Contains(">d__")`) is cheap for non-candidates, but the full scan is O(distinct types). With an `IsAsyncStateMachineType` flag, this collapses to O(matching types) — typically 50–500 entries.
- **`EnumerateInterfaces()` per regex match.** Each type that passes the name filter incurs an interface enumeration call via ClrMD. This iterates the type's interface table in the dump. With a large framework (many generic async methods), there may be several hundred candidates, each paying this cost.
- **`GetTypeByMethodTable` per aggregate entry** is called for every entry in the outer loop before the name pre-filter: `heap.GetTypeByMethodTable(kv.Key)`. This means ClrMD resolves the type for every MT in the dictionary, not just candidates. This is an unnecessary cost for the majority of non-state-machine types.

  **Recommendation:** Move `GetTypeByMethodTable` after the flag check (once `IsAsyncStateMachineType` flag exists) or after the `TypeAggregates` entry's type name is available via the index.

### Memory

- `candidates` list is bounded by `TypeCandidateLimit` (200). Per-entry allocation is one tuple with two strings and a struct — negligible.
- `highCaptures` list is bounded by candidates. Per-entry `List<string>` for large captures is small.
- `topTypes`, `topByCapturedSize`, `suspendedMap` lists are all bounded by options limits. No unbounded allocations.
- Reference field reads use `f.ReadObject` per field — ClrMD allocates a `ClrObject` struct (stack-allocated in practice). No per-instance heap allocation.

### Scalability Assessment (1 GB – 100 GB)

| Scale | Risk | Notes |
|---|---|---|
| 1–5 GB | Low | Type count typically <10K; full iteration is fast |
| 5–25 GB | Medium | Type count 10K–50K; iteration and per-candidate interface checks become measurable |
| 25–100 GB | High | Type count 50K+; `GetTypeByMethodTable` for every entry and interface checks for all pattern matches add seconds; `IsAsyncStateMachineType` flag is required |

### Cancellation

Cancellation is checked at the outer aggregate iteration loop entry. For large dumps where iteration takes seconds, this is sufficient.

### Progress Reporting

No progress reporting. For large dumps where the type scan takes noticeable time, a progress report at entry and exit would be consistent with other analyzers.

---

## Audit Area 6 — Correctness & Confidence

### `AvgStateValue` is semantically wrong

`avgStateValue` is read from a single sample instance. It is labelled as "avg" in the domain model (`AvgStateValue`), the section builder column header (`Avg State`), and consumed by any downstream tooling. A single-sample value is not an average. For types with many instances at different await points, this value is arbitrary and may mislead an engineer into thinking the "average" state is representative.

**Risk:** Medium. An engineer investigating a `AvgStateValue = 3` for a type with 1,000 instances may conclude all instances are at await 3, when in reality they are distributed across awaits 0–5.

### Capture Bytes Over-estimates Shared References

`capturedBytes` sums `refObj.Size` for each directly referenced object. If the same large object (e.g. a shared `IConfiguration`) is referenced by all state machine instances and also appears in the closure, its full size is counted once per sample. For types with high instance counts, this produces a per-instance capture figure that dramatically overestimates actual incremental retention. The report has no disclaimer about shared-reference counting.

**Risk:** High for the `HighCaptureStateMachine` table. Findings based on `LargeCaptureWarning` / `LargeCaptureCritical` thresholds may fire for shared infrastructure objects that are not actually leaked.

### Single-Sample Coverage

Every field-level data point (state value, captured bytes, ref field count) comes from one instance. For a type with 10,000 instances, the sample is the lowest-address object (determined by the Phase 1 scan). This instance may be in an atypical state, may reference objects that other instances do not, or may have had its fields partially recycled or GC-moved since the index was built.

**Risk:** Low for field count (structural, per-type). Medium for state value and captured bytes (instance-specific).

### `TypeCandidateLimit` Silent Truncation

When the candidate list reaches 200 and `ScanLimited` is set, the remaining type aggregates are not inspected. If the first 200 state machine types are small administrative methods, the most-populated state machine type (a high-throughput hot path method) may be entirely absent from the report. `ScanLimited = true` is emitted, but no indication of how many additional types were skipped or what their combined count/size represents is provided.

**Risk:** Medium for large codebases with many async methods.

### `totalCount` Accumulation

`totalCount` is declared as `long` and accumulates `candidates[i].Entry.Count` (also `long`). The final result is cast to `int` via `(int)Math.Min(totalCount, int.MaxValue)` — this is correctly guarded. However, `totalBytes` accumulates `entry.TotalSize` (`ulong`) without overflow protection; for dumps with hundreds of millions of state machine instances, this could theoretically overflow (would require ~17 EB of state machine objects on a 64-bit address space — not a practical risk, but the asymmetry with `totalCount` handling is worth noting).

### Regex Timeout

The regex is constructed with `TimeSpan.FromMilliseconds(50)` as a timeout. For type names with pathological backtracking potential, this will throw `RegexMatchTimeoutException`. The outer loop does not catch this exception, meaning a single pathological type name would abort the entire analyzer. The exception is not a `CancellationToken` cancellation, so it would surface as an analyzer failure.

**Risk:** Low in practice (compiler-generated names are regular), but a `try/catch` around the `StateMachinePattern.Match` call would prevent single-type failures from aborting the analysis.

### Edge Cases

- State machines from C# iterators (`yield return`) also generate `d__N`-named types but do not implement `IAsyncStateMachine` — correctly excluded by the interface check.
- F# async workflows do not generate `IAsyncStateMachine` implementations using the same pattern — correctly excluded.
- Generic state machines produce names like `<Method>d__N[[T, Assembly]]`. The regex pattern `<(.+?)>d__\d+$` anchors to `$` end-of-string. Generic type parameters appended after `d__N` by the CLR would cause the pattern to not match — these state machines would be silently excluded. This should be verified against ClrMD's `ClrType.Name` format for generic async methods.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS: `!dumpasync`

`!dumpasync` (SOS 6+) is the gold standard for async state machine analysis. It:
- Walks the continuation chain from each active `Task`, linking the `Task` → `AsyncStateMachine` → next `Task` in the async call graph.
- Reports the `IAsyncStateMachine` implementor, the awaiting type, and the state value with human-readable await-point annotation.
- Identifies `async void` methods explicitly.
- Shows the full async call stack as a tree, not just individual type counts.

**Gap vs. DumpDetective:** DumpDetective's suspended method map and type counts provide population-level analysis that `!dumpasync` does not offer. DumpDetective cannot reconstruct the async call tree or identify which awaiter type each state machine is waiting on.

### PerfView

PerfView's memory analysis and ETW async tracking are trace-based, not dump-based. Not directly comparable for post-mortem analysis. The `HeapSnapshot` view shows type-level counts similar to DumpDetective's type table.

### Visual Studio Memory Usage

VS shows type-level instance counts and retention trees but does not have async-specific views. DumpDetective's suspended method map is superior for async-specific investigation.

### JetBrains dotMemory

dotMemory's "Async/Await" view (available in newer versions) shows async method frames in the allocation path and can group by originating method. It approximates DumpDetective's suspended method map but with richer call-graph context. dotMemory requires a live process or attached profiling session; it does not analyze cold dumps.

### Competitive Opportunities

1. **Async call tree reconstruction.** Following `IAsyncStateMachine` → builder → backing `Task` → `m_continuationObject` → next state machine would produce a tree equivalent to `!dumpasync`. This is high effort but the highest-value gap.
2. **Awaiter type identification.** Reading the `<>u__N` awaiter fields of a state machine reveals what the method is actually waiting on (a `Task`, a `ValueTask`, a custom awaiter, a lock). This is available in the dump and is the key missing piece for actionable diagnosis.
3. **Aggregate async call graph.** Rather than a full tree per instance, collapsing all chains into a type-level call graph (method A suspending at await 2 waiting for method B) would be a compact and novel view not offered by any tool in this comparison.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

**Production readiness:** Conditionally production-ready. The analyzer produces useful population-level diagnostics and the suspended method map is genuinely valuable. It is blocked from full production confidence by the `AvgStateValue` accuracy issue, the missing GC generation data, and scalability concerns from the full-type-aggregate scan without a Phase 1 flag.

**Major strengths:**
- Zero heap scan; O(types) operation using the pre-built index.
- Suspended method map uniquely collapses multi-variant compiler types by originating method.
- `TypeCandidateLimit` and `TopTypeLimit` bounding prevents unbounded analysis.
- `ScanLimited` is surfaced in output.
- Finding quality (evidence, recommendation, tags) is high.

**Major weaknesses:**
- `AvgStateValue` is incorrect by construction (single sample, not an average).
- No GC generation data — cannot distinguish ephemeral from leaked state machines.
- Missing `IsAsyncStateMachineType` Phase 1 flag forces O(all types) iteration.
- Capture byte estimate is shallow and over-counts shared references.
- `GetTypeByMethodTable` called for every type aggregate before name pre-filter.
- `RegexMatchTimeoutException` unhandled in outer loop.
- `FormatBytes` duplicated across analyzer and finding generator.
- Only one fire-and-forget finding generated regardless of offender count.

### Priority Roadmap

| ID | Recommendation | Classification | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|
| P0-1 | Add `IsAsyncStateMachineType` flag to `TypeAggregateFlags` (bit 5); set in `DiskBackedObjectIndexWriter.ComputeTypeFlags` using name pattern + `EnumerateInterfaces` | Evolution | High — eliminates full-type scan; enables O(matching types) Phase 2 | Medium | High | DONE |
| P0-2 | Add `Gen2Count` and `Gen2Fraction` to `StateMachineTypeProfile`; expose in section table | Improvement | High — enables leak vs. throughput distinction | Low | High | DONE |
| P0-3 | Rename `AvgStateValue` → `SampleStateValue` (or replace with distribution); update domain model, section builder column header, and any downstream consumers | Improvement | High — removes actively misleading column | Low | High | DONE |
| P1-1 | Move `heap.GetTypeByMethodTable(kv.Key)` after name pre-check (or after flag check once P0-1 lands); avoids ClrMD resolution for non-candidate types | Improvement | Medium — reduces Phase 2 latency on large dumps | Low | High | DONE |
| P1-2 | Wrap `StateMachinePattern.Match` in try/catch for `RegexMatchTimeoutException`; log and continue | Improvement | Medium — prevents single-type failure from aborting analysis | Low | High | DONE |
| P1-3 | Remove `break` in fire-and-forget finding loop; report top-3 offenders above threshold, not just one | Improvement | Medium — surfaces all fire-and-forget sinks | Low | High | DONE |
| P1-4 | Escalate fire-and-forget finding severity based on `SuspendedCount` (e.g. Warning ≥100, Error ≥1000, Critical ≥10000) | Improvement | Medium — accurate severity triage | Low | High |
| P1-5 | Replace `FormatBytes` in both `AsyncStateMachineAnalyzer` and `AsyncStateMachineFindingGenerator` with `FormatHelper.FormatBytes` | Improvement | Low — removes duplication | Trivial | High | DONE |
| P1-6 | Add state value interpretation guidance to section narrative (table footnote or prose block) | Improvement | Medium — makes state column actionable without external docs | Low | High |
| P2-1 | State value histogram per top type (bounded instance scan for top-10 types, max 1000 instances each) | Improvement | High — identifies specific stuck await points | Medium | High |
| P2-2 | Detect `async void` originating methods; add `IsAsyncVoid` flag to `StateMachineTypeProfile`; generate a dedicated Warning finding | Improvement | High — unobservable failure risk pattern | Medium | Medium |
| P2-3 | Annotate capture estimate as "shallow (direct references only)"; add explicit sharing-caveat note | Improvement | Medium — prevents misinterpretation of `HighCaptureStateMachine` table | Low | High |
| P2-4 | Verify regex `<(.+?)>d__\d+$` against generic async state machine type names emitted by ClrMD; add `Contains(">d__")` post-processing to handle trailing generic params if needed | Improvement | Medium — correctness risk for generic async methods | Low | Medium |
| P3-1 | Task linkage: read `AsyncTaskMethodBuilder.m_task` from state machine sample; cross-reference with `AsyncTaskAnalyzer` results | Evolution | High — enables cross-analyzer async call graph | High | Medium |
| P3-2 | Async call tree reconstruction analogous to `!dumpasync` (state machine → builder task → continuation → next state machine) | Evolution | Very High — flagship capability | Very High | Medium |
| P3-3 | Add `statemachine.gen2.count` and `statemachine.gen2.fraction` to `AsyncStateMachineTrendComparer` metrics | Improvement | Medium — enables regression tracking of long-lived suspensions | Low | High |

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. The population-level counts and suspended method map are reliable and useful. The `AvgStateValue` column is incorrect by construction and should be renamed or removed before this section is shown to production users. The capture analysis is a useful heuristic but should be annotated as an estimate with known limitations.

2. **Highest-impact improvements:** Adding `IsAsyncStateMachineType` to `TypeAggregateFlags` (P0-1), surfacing Gen2 counts (P0-2), and fixing `AvgStateValue` (P0-3) together transform the analyzer from a population counter into a leak-vs-throughput classification tool.

3. **Platform evolution opportunities:** The `IsAsyncStateMachineType` Phase 1 flag benefits the entire analysis pipeline. Task linkage (P3-1) and async call tree reconstruction (P3-2) would close the largest gap against WinDbg's `!dumpasync` and represent the most significant competitive capability improvement available in this domain.

4. **Highest engineering return:** P0-1 (Phase 1 flag, one-time O(types) check at index build), P0-2 (Gen2 count, already in the index, zero additional cost), and P0-3 (rename, trivial) deliver the highest diagnostic value per engineering hour.

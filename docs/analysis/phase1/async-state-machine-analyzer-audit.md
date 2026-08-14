# AsyncStateMachineAnalyzer — Phase 1 Audit

**Analyzer:** `AsyncStateMachineAnalyzer` (`src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs`)
**Protocol:** Phase 1 Analyzer Audit (`phase1-analyzer-architecture-review.md`)
**Original audit date:** 2026-08-03
**Re-audit date:** 2026-08-14 — performed after P0/P1/P2/P3-3 roadmap items shipped. Areas below describe the
**current** implementation; resolved findings from the original audit are marked accordingly. The re-audit
found three new issues (P0-4 regex drift silently undoing P2-4, P1-7 trend metric scope mismatch, P1-8 dead
code) — all three were fixed same-session; see the Priority Roadmap and Final Executive Summary for current
status. P2-5 through P2-8 remain open.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`AsyncStateMachineAnalyzer` covers four concerns (one more than the original audit — the histogram is new):

1. **State machine population** — identifies all compiler-generated async state machine types on the heap via the `TypeAggregateFlags.IsAsyncStateMachineType` Phase 1 flag (single flag-masked scan, no regex/interface work in Phase 2), and reports instance counts, total bytes, and Gen0/1/2 counts per type.
2. **Captured closure analysis** — reads reference fields from each type's `SampleAddress` to estimate how many bytes each state machine instance holds via its captured variables, now explicitly annotated as a shallow, non-exclusive estimate (P2-3).
3. **Suspended method map** — aggregates state machine instances by `(DeclaringType, MethodName)` to expose which async methods have the most suspended instances, regardless of how many compiler-generated types they produce.
4. **Suspend-state histogram (new, P2-1)** — for the top `HistogramTopTypeLimit` types, runs a second bounded pass (preferring the disk-backed object index, falling back to a live heap walk) to sample up to `HistogramInstanceCapPerType` instances per type and build a real `<>1__state` value distribution, replacing the old single-sample "average" that was never actually an average.

The four concerns remain cohesive. Concern 4 is the first place this analyzer does more than O(types) work — it is bounded, but it is a materially different cost profile from the rest of the analyzer and should be understood as such (see Area 5).

### Coverage Gaps (updated)

**Resolved since original audit:**
- ~~No `IsAsyncStateMachineType` flag~~ — done (P0-1). Phase 2 now does a single flag-masked scan.
- ~~No GC generation data~~ — done (P0-2). `Gen2Count`/`Gen2Fraction` are on `StateMachineTypeProfile` and surfaced in the report.
- ~~No state value distribution~~ — done (P2-1). Real histogram, not a single sample.
- ~~`async void` methods not flagged~~ — done (P2-2). `IsAsyncVoid` flag + dedicated finding.

**Still open:**
- **No Task linkage.** Compiler-generated state machines drive a `Task` via `m_task` / the builder's backing task. The analyzer does not resolve that task, so there is no correlation with `AsyncTaskAnalyzer`'s task population. Unchanged from original audit (tracked as P3-1).
- **`IValueTaskSource<T>` state machines.** Still treated uniformly with `Task`-backed state machines; no contextual distinction. Unchanged.
- **No capture-depth distinction.** Capture analysis still reads only direct reference fields (now explicitly annotated as shallow via P2-3, but the underlying limitation — no transitive/BFS estimate — is unchanged). Tracked as P3-2/P4 territory (bounded BFS for top suspects).

### Finding — Dead Code (RESOLVED as P1-8)

`AsyncStateMachineAnalyzer.ImplementsIAsyncStateMachine` (private helper) was no longer called anywhere — it was the Phase 2 fallback before the `IsAsyncStateMachineType` flag existed, orphaned once candidate selection moved to filtering purely on the flag (Step 1). Confirmed via symbol search (zero call sites) and removed.

### Unexpected Functionality

None. All logic serves async state machine diagnostics.

### Adjacent Capabilities

Unchanged from original audit:
- `AsyncTaskAnalyzer` independently analyzes the `Task` side of the same async operations — natural complement, no shared data today.
- `ThreadAnalyzer`/`HangAnalyzer` thread-stack data could be cross-referenced for sync-over-async deadlock candidates.
- `LeakCandidateAnalyzer` uses Gen2/LOH counts to rank leak suspects — this analyzer now exposes the same signal (P0-2) but the two are not cross-referenced.

### Architectural Observations

- The analyzer still accesses the heap index via a direct cast to `HeapAnalysisCache` (`if (cache is HeapAnalysisCache heapCache ...)`) for `TryGetHeapIndex`/`TypeAggregates`, bypassing the `IHeapAnalysisCache` abstraction. This is consistent with other analyzers (`GCRootAnalyzer`, `LeakCandidateAnalyzer`) so it is not a regression, but it remains a latent coupling risk. Unchanged from original audit.
- The new histogram pass (Step 3b) correctly uses the `IHeapAnalysisCache.EnumerateIndexedEntriesAsTuples()` abstraction (not a direct cast) with a live-heap fallback — this is a better pattern than the `TypeAggregates` access above and could be a model for tightening the rest of the analyzer's cache access.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths (updated)

- **Suspended method map** remains the single most valuable table — collapses compiler-generated type variants by originating method.
- **`ScanLimited` key metric** is emitted when the candidate cap is hit.
- **Four report tables** now (types by count — including the histogram columns, instances by captured bytes, methods by suspension count) cover distinct investigation angles.
- **Finding quality** remains high, and finding coverage is now broader: high total count, up to 3 fire-and-forget offenders (was 1), async void detection, and large-capture warning — 4 distinct finding types vs. the original audit's 2.
- **`Dominant State` + `State Distribution` columns** (P2-1) are a genuine upgrade — an engineer can now see "70% stuck at await 2, 20% at await 0" instead of one arbitrary sample value.
- **Capture estimate is now explicitly annotated** (P2-3) — column header says "(shallow)" and the narrative explains the shared-reference over-counting risk.

### Weaknesses (updated)

**Resolved since original audit:**
- ~~`AvgStateValue` is not an average~~ — resolved. Replaced by `DominantState` (derived from the real histogram, with single-sample fallback for types outside the histogram's top-N) and `StateDistribution`.
- ~~State value numbers have no interpretation guidance~~ — resolved (P1-6). Narrative block explains -2/-1/0/1/... encoding.
- ~~Capture bytes are shallow and shared with no annotation~~ — resolved (P2-3).
- ~~Only one fire-and-forget finding is generated~~ — resolved (P1-3). Up to 3 (`MaxFireAndForgetFindings`).
- ~~Fire-and-forget finding severity is always Warning~~ — resolved (P1-4). Escalates to `Critical` at `HighCountCritical` (10,000).

**Still open:**
- **Capture bytes address is still the sample address, not a named instance.** `HighCaptureStateMachine.Address` is still `entry.SampleAddress`. The P2-3 annotation explains the byte-count methodology but does not address the separate issue that the *address itself* implies a specific instance was chosen for its captures, when it was chosen because it's the lowest-address instance of the type. Minor — the column caveat block partially covers this by implication, but an explicit note ("sample instance, not the largest-capture instance") would remove the last ambiguity.
- **`ScanLimited` impact still not quantified.** The key metric still just says "Yes — type candidate cap hit; results may be partial" with no estimate of how many types were skipped or what fraction of total state-machine bytes they might represent. Unchanged from original audit; never got a roadmap ID.
- **No section narrative for zero-result case.** Still true — when no state machines are found, `TopStateMachineTypes.Count == 0` means no explanatory block is emitted (`KeyMetrics` still populate with zeros, but no prose). Unchanged; never got a roadmap ID.
- **`Dominant State` for types outside the histogram's top-N silently falls back to the old single-sample value**, with no report-level indication of which regime a given row is in. The narrative block says "distribution is blank for types beyond the histogram sampling limit" which is the right signal (StateDistribution is empty for those rows), but `Dominant State` itself doesn't change appearance — a reader skimming just that column can't tell a real dominant-state (backed by up to 1,000 samples) from a single-sample fallback. Worth a visual/textual distinction (e.g. render sample-only values as `"3 (sample)"`).

### Missing Diagnostics (updated)

**Resolved:** state value histogram, Gen2 count/fraction, async void annotation, capture estimate caveat — all shipped.

**Still missing:**
- Task linkage for top state machine types (P3-1).
- Estimated true retention via transitive reference following for top suspects (bounded BFS) — still absent.

### Missing Statistics (unchanged — never implemented)

- Median and P95 instance count across all identified state machine types.
- Total state machine memory as a fraction of total heap size.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage (updated)

- **Candidate selection no longer calls `EnumerateInterfaces()` in Phase 2.** Resolved by the Phase 1 flag (P0-1) — Step 1 is a pure flag-masked dictionary scan.
- **`ClrType.Fields` iteration is correct and efficient** — unchanged assessment, still true.
- **`stateField.Read<int>(..., interior: false)`** — correct usage, unchanged.
- **`f.ReadObject(..., interior: false)`** for reference fields — correct usage, unchanged.
- **New: histogram pass reads `<>1__state` per matched instance via `heap.GetObject(address)`.** This is a live ClrMD call, but it is gated behind an address match against a `HashSet`-backed lookup and capped per type — bounded exactly as designed (Area 5 covers the cost profile).

### Infrastructure Utilization — Critical Finding (RESOLVED same-session as P0-4)

**The `IsAsyncStateMachineType` flag computation and the analyzer's candidate regex had drifted out of sync, silently defeating P2-4.**

Two independent copies of the same state-machine-name regex exist:

| Location | Pattern | Generic-aware? |
|---|---|---|
| `DiskBackedObjectIndexWriter.IsAsyncStateMachineType` (Phase 1, sets the `IsAsyncStateMachineType` flag) | `<(.+?)>d__\d+$` | **No** |
| `AsyncStateMachineAnalyzer.StateMachinePattern` (Phase 2, re-parses the method name from the already-filtered candidate) | `<(.+?)>d__\d+(?:\[\[.+?\]\])?$` | **Yes** (P2-4 fix) |

P2-4 fixed only the Phase 2 copy. But Phase 2's candidate loop (`AsyncStateMachineAnalyzer.Analyze`, Step 1) filters purely on the Phase 1 flag:

```csharp
if ((kv.Value.Flags & TypeAggregateFlags.IsAsyncStateMachineType) == 0)
    continue;
```

For a generic async state machine (`<Method>d__1[[System.String, mscorlib]]`), the Phase 1 regex (`$`-anchored, no generic tail allowance) does **not** match, so `IsAsyncStateMachineType` is never set on that type's aggregate entry. The type is filtered out before Phase 2's more permissive regex ever sees it. **P2-4's fix is unreachable in production** — generic async methods are silently excluded from the entire analysis today, exactly the bug P2-4 was written to close.

This was not caught by `AsyncStateMachineRegexTests.cs` because that test file contains a **third**, independent copy of the pattern (hardcoded directly in the test, matching the fixed Phase 2 regex) — it validates that string, not either production regex, and provides zero protection against this exact class of drift.

**Fix applied (P0-4):** extracted one shared `internal static readonly Regex` — `AsyncStateMachineNamePattern.Regex` in `DumpDetective.Analysis.Utilities` — referenced by both `DiskBackedObjectIndexWriter.IsAsyncStateMachineType` and `AsyncStateMachineAnalyzer`'s candidate parsing, and repointed `AsyncStateMachineRegexTests` at that shared instance instead of a fourth private copy. This eliminates the drift class entirely rather than just re-syncing the two literals (which would have drifted again the next time either file was touched independently).

**Other infrastructure items — resolved since original audit:**
- ~~`FormatBytes` duplicated between analyzer and finding generator~~ — resolved (P1-5), both use `FormatHelper.FormatBytes`.
- ~~`GetTypeByMethodTable` called for every type aggregate before name pre-filter~~ — resolved (P1-1, effectively superseded by P0-1): `GetTypeByMethodTable` is now only called inside the Step 3 loop, which only iterates already flag-filtered candidates.

**New strength worth noting:** the histogram pass (Step 3b) prefers `IHeapAnalysisCache.EnumerateIndexedEntriesAsTuples()` (disk-backed sequential index reads) over a live `heap.EnumerateObjects()` walk, falling back to the live walk only when no disk index exists. This is the right pattern for a bounded-but-potentially-heap-sized second pass and is a better model than the direct-cast `TypeAggregates` access used elsewhere in this same analyzer (see Area 1's architectural observation).

### Index Recommendations

- ~~P0-4: Deduplicate the state-machine regex between `DiskBackedObjectIndexWriter` and `AsyncStateMachineAnalyzer`~~ — done. Shared `AsyncStateMachineNamePattern.Regex`; `AsyncStateMachineRegexTests` now exercises the actual production regex object, not a fourth private copy.
- The original audit's `IsAsyncStateMachineType` (bit 5) recommendation is done; bits 6-7 remain reserved per `TypeAggregateFlags.cs`.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics — status update

1. ~~State value histogram per type~~ — **done** (P2-1).
2. ~~Gen2 fraction per state machine type~~ — **done** (P0-2).
3. ~~`async void` flag~~ — **done** (P2-2).
4. **Task linkage** — still open (P3-1). Highest-remaining-value item; would enable cross-referencing with `AsyncTaskAnalyzer`.
5. **Transitive capture size (bounded BFS, top suspects only)** — still open. No roadmap ID assigned yet; natural companion to P2-3's annotation (the annotation explains the limitation, this would reduce it for the highest-value cases).
6. **Sync-over-async detection** — still open (tracked loosely under P3 territory, no dedicated ID).

### High-Value Statistics (unchanged — never implemented)

- Total state machine memory as percentage of heap.
- ~~Gen2 instance count and fraction~~ — done.
- Ratio of distinct originating methods to distinct state machine types.
- P50/P95 suspension count per method.

### Evidence Recommendations — status update

All four original recommendations shipped:
- ~~Replace `AvgStateValue` with `SampleState` + `State Distribution`~~ — done, as `DominantState` + `StateDistribution`.
- ~~Add `Gen2Count`/`Gen2Fraction` columns~~ — done.
- ~~Add `IsAsyncVoid` flag~~ — done.
- ~~Annotate the capture estimate as shallow~~ — done.

### Priority-Ranked Opportunities (remaining)

| Priority | Opportunity | Expected Impact |
|---|---|---|
| P0 | Fix regex drift between Phase 1 flag computation and Phase 2 candidate parsing (P0-4) | Restores generic async method coverage; correctness regression |
| P1 | `statemachine.gen2.fraction` trend metric numerator/denominator mismatch (see Area 6) | Trend regression tracking currently understates Gen2 fraction when candidate count exceeds `TopTypeLimit` |
| P2 | Task linkage | Enables cross-analyzer async call graph |
| P2 | Bounded transitive capture BFS for top suspects | Reduces (does not eliminate) the shallow-capture limitation for the highest-value rows |
| P3 | Sync-over-async cross-reference | High-value for deadlock investigation |
| P3 | .NET 11 Runtime Async re-audit (unchanged — blocked on GA spec) | Prevents silent under-counting as Runtime Async adoption grows |

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap Scan (updated)

**Steps 1-2 (candidate selection, aggregation):** still O(unique types), now via a single flag-masked scan — this is *better* than the original audit's assessment (which still described a full regex/interface scan), since P0-1 shipped.

**Step 3 (field metadata + sample-based analysis):** O(candidate types), bounded by `TypeCandidateLimit`/`TopTypeLimit` — unchanged from original audit, still correct.

**Step 3b — new cost center.** The histogram pass is *not* O(types) — it iterates the disk-backed object index (or, in the fallback case, a live `heap.EnumerateObjects()`) until every tracked type's cap is filled, with an early-exit once all `HistogramTopTypeLimit` types are capped. In the common case (state-machine types are reasonably dense on the heap) this exits early and is cheap. In a pathological case — a very large heap where the histogrammed types are sparse or clustered near the end of the address space — this pass can approach O(heap objects) before early-exiting, which is a materially different cost profile from the rest of the analyzer. This is a bounded worst case (not unbounded), but it introduces the first potentially-large streaming pass in an analyzer whose original design point was "zero heap scan, O(types) only."

**Recommendation:** add progress reporting to Step 3b for large dumps (see below) so a slow pathological case is visible rather than silent.

### Type Iteration Cost (updated)

- ~~All type aggregates iterated with per-candidate regex/interface check~~ — resolved by P0-1. Step 1 is now O(distinct types) with a flag mask only, no regex/interface work per entry.
- ~~`GetTypeByMethodTable` called for every aggregate entry~~ — resolved (P1-1/P0-1 combined). Only called for already flag-filtered candidates in Step 3.

### Memory (unchanged assessment, still correct)

- `candidates`, `highCaptures`, `pendingProfiles`, `topTypes`, `topByCapturedSize`, `suspendedMap` are all bounded by options limits.
- **New:** `stateFieldByMt`, `histogramMts`, `histograms`, `histogramRemaining` (Step 3b) are all bounded by `HistogramTopTypeLimit` (default 10) keys; the inner `Dictionary<int,int>` per type is bounded by the number of distinct state values actually observed (small — state values are small non-negative integers plus -1/-2), capped indirectly by `HistogramInstanceCapPerType`. No unbounded allocation introduced.

### Scalability Assessment (1 GB – 100 GB) — updated

| Scale | Risk | Notes |
|---|---|---|
| 1–5 GB | Low | Flag-masked scan is fast; histogram pass exits early in typical distributions |
| 5–25 GB | Low-Medium | Was "Medium" in original audit due to per-candidate interface checks — now resolved by P0-1. Histogram pass is the main remaining variable cost, bounded by early-exit |
| 25–100 GB | Medium | Type-count risk from original audit resolved. Histogram pass worst case (sparse/clustered candidate types on a 100GB+ object index) is the new scalability question; no progress reporting currently makes this invisible if it runs long |

### Cancellation

Checked per-iteration in Step 1, Step 3, and Step 3b (index/heap loop) — consistent with the `AsyncTaskAnalyzer.ScanRawHeapForTasks` convention used elsewhere for the same class of loop. No change needed.

### Progress Reporting

Still absent for the whole analyzer (original audit finding), and now more relevant: Step 3b is the first part of this analyzer whose cost can scale with heap/index size rather than type count. **Recommendation (new, no roadmap ID yet):** thread `context.Progress` through to Step 3b, using the same `ObjectScanCounter` pattern as `AsyncTaskAnalyzer.ScanRawHeapForTasks`.

---

## Audit Area 6 — Correctness & Confidence

### Resolved since original audit

- ~~`AvgStateValue` is semantically wrong~~ — resolved. `DominantState` is now genuinely derived from a real histogram for the top `HistogramTopTypeLimit` types (falls back to single-sample for the rest, which is a known and now-annotated regime, not a silent one — see Area 2's open item about visually distinguishing the two regimes).
- ~~`RegexMatchTimeoutException` unhandled~~ — resolved (P1-2), `try/catch` around the Phase 2 `Match` call.
- ~~Generic state machine names not matched~~ — the Phase 2 regex was fixed (P2-4), but see the new finding below: **the fix does not take effect** because of the Phase 1/Phase 2 regex drift.

### Finding — Regex Drift Silently Defeated P2-4 (Critical, RESOLVED as P0-4)

See Area 3 for full detail. Summary: `DiskBackedObjectIndexWriter.IsAsyncStateMachineType` (Phase 1, gates candidate inclusion) used the pre-P2-4 regex without generic-parameter tolerance. Generic async methods (`async Task<T> GetAsync<T>()`, `async IAsyncEnumerable<T> StreamAsync<T>()`, LINQ-with-async, generic repository patterns — all called out as "common in modern .NET" in the original P2-4 audit entry) were excluded before Phase 2's fixed regex was ever reached.

**Risk (before fix):** High. Not a theoretical edge case — generic async methods are common, and the analyzer silently under-reported (no crash, no warning) on any dump containing them. **Fixed** by unifying both call sites on `AsyncStateMachineNamePattern.Regex` (P0-4).

### Finding — Trend Comparer Gen2 Fraction Numerator/Denominator Mismatch (Medium, RESOLVED as P1-7)

`AsyncStateMachineTrendComparer.ExtractMetrics` computes:

```csharp
long totalGen2Count = 0;
foreach (var profile in r.TopStateMachineTypes)
    totalGen2Count += profile.Gen2Count;
double gen2Fraction = r.TotalStateMachines == 0 ? 0.0 : totalGen2Count * 100.0 / r.TotalStateMachines;
```

`totalGen2Count` sums `Gen2Count` only over `TopStateMachineTypes` (bounded by `TopTypeLimit` — 20 in the default/Balanced profile), but `r.TotalStateMachines` is the full aggregate count across **all** candidate types (bounded by `TypeCandidateLimit` — 200 in the default profile). When there are more distinct state-machine types than `TopTypeLimit`, this ratio compares a partial numerator against a full-population denominator, systematically understating `statemachine.gen2.fraction`. The same mismatch exists in `Compare()`.

**Risk:** Medium. Affects the `statemachine.gen2.fraction` trend metric used for regression tracking (P3-3) — an application with many small state-machine types beyond the top 20 would show an artificially low, and potentially misleadingly *decreasing*, Gen2 fraction over time even if the actual fraction is stable or worsening in the untracked tail. Does not affect the report itself (the section builder's per-row `Gen2Fraction` is correctly scoped to each individual type).

**Fix applied:** option (a) — added `AsyncStateMachineDomainResult.TotalGen2Count`, aggregated over all candidates in the analyzer's Step 2 (same scope as `TotalStateMachines`), and the trend comparer now reads it directly. A regression test (`ExtractMetrics_UsesTotalGen2Count_NotTopStateMachineTypesSum`) exercises a case where `TopStateMachineTypes`' sum and the correct aggregate diverge, to guard against this silently reverting.

### Finding — Dead Code (Low, RESOLVED as P1-8)

`ImplementsIAsyncStateMachine` — see Area 1. Removed. No correctness risk while present (unused), but was confusing for future maintainers who might have assumed it was still part of the detection path (especially relevant given the regex-drift finding above — a maintainer debugging that issue could have reasonably but incorrectly assumed this method was in the call path).

### New Finding — Test Coverage Gap (Medium)

There is no analyzer-level unit test for `AsyncStateMachineAnalyzer.Analyze` itself — only `AsyncStateMachineRegexTests` (tests a fourth disconnected regex copy — see Area 3), `AsyncStateMachineTrendComparerTests`, and `AsyncStateMachineFindingGeneratorTests` (both construct `AsyncStateMachineDomainResult`/`StateMachineTypeProfile` directly, bypassing the analyzer entirely). The one integration-level safety net, `AsyncStateMachineAnalyzerDiscrepancyTests.AsyncStateMachineAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap`, currently **fails on its own test-harness setup** — it throws `FileNotFoundException` inside `DiskBackedObjectIndexWriter.Build` (`new FileInfo(dumpPath).Length`) because the synthetic `freshDumpPath` it constructs never has a backing file, before `AnalyzeAsync` is ever invoked. This is a pre-existing harness bug (confirmed unrelated to the P2-1 histogram change), but it means the only test that would have caught the disk/live-heap fallback behavior in Step 3b — or the regex drift bug above, since it runs both a disk-index and in-memory cache over the same heap — is not currently exercisable.

**Risk:** Medium. This audit's two headline findings (regex drift, trend metric mismatch) were both found by code reading, not by any existing test. Neither has automated coverage today.

### Edge Cases (unchanged from original audit — still accurate)

- C# iterators (`yield return`) correctly excluded via the `IAsyncStateMachine` interface check (now performed once at Phase 1 index time).
- F# async workflows correctly excluded (no `IAsyncStateMachine` implementation).
- Generic state machines — **see the new regex-drift finding above; this edge case is currently mishandled**, superseding the original audit's "should be verified" note with a confirmed bug.

---

## Audit Area 7 — Industry Benchmark

Unchanged from original audit in substance — the population-level analysis (type counts, suspended method map) remains ahead of general-purpose tools for async-specific investigation, and the histogram addition (P2-1) narrows part of the gap against `!dumpasync`'s per-instance state reporting. The largest gaps remain:

1. **Async call tree reconstruction** (`!dumpasync`-equivalent) — still not implemented (P3-2), still the highest-value gap.
2. **Awaiter type identification** (`<>u__N` fields) — still not implemented, still available in the dump.
3. **Aggregate async call graph** — still not implemented.

The histogram (P2-1) is a genuine, if partial, step toward `!dumpasync` parity: it now answers "how many instances are stuck at which await point" for the top types, which `!dumpasync` also reports per-instance. What's still missing is the *linkage* — which awaiter/continuation each stuck instance is waiting on — not just the count distribution.

---

## .NET 11 Runtime Async — Forward Compatibility

Unchanged from original audit. Still tracked as P3-4; no action taken (correctly — the on-heap shape remains unstable pre-GA). See original guidance below, reproduced verbatim as it remains current.

**Status:** .NET 11 (preview, targeting 2026 GA) introduces **Runtime Async**, moving async-method suspension from a compiler-generated `IAsyncStateMachine` struct into the CLR itself. It is opt-in per project (`<Features>runtime-async=on</Features>` in preview; direction is for BCL/ASP.NET Core to ship built with it on, with `<UseRuntimeAsync>false</UseRuntimeAsync>` as the opt-out).

**What changes on the heap:**
- For methods compiled under Runtime Async, the compiler no longer emits a `<MethodName>d__N : IAsyncStateMachine` struct, no `<>1__state` field, no `<>t__builder` field. The method body instead calls `AsyncHelpers.Await<T>(...)` under `[MethodImpl(MethodImplOptions.Async)]`.
- Locals are kept on the JIT stack and only spilled to the heap when their lifetime actually crosses a suspension point — not unconditionally hoisted to fields as today. This is a *strictly smaller* heap footprint per suspension than the current model, not a superset, so `HighCaptureStateMachine` byte totals will under-report if only the old model is scanned (there is simply less to find, but what remains may live in an object this analyzer doesn't recognize).
- Continuation orchestration is exposed through new runtime types (`System.Runtime.CompilerServices.AsyncHelpers`, `RuntimeAsyncTask<T>`, `DispatchContinuations()`), not through `TaskAwaiter`/`<>t__builder` wiring.
- The exact object shape used for spilled locals (type name, field layout) is **not finalized as of .NET 11 preview** — this must be re-verified against the shipped GA runtime before any detection logic is written against it.

**Why this matters here:** `AsyncStateMachineAnalyzer`'s entire population (`TypeAggregates` flag match on the `<...>d__\d+` pattern + `IAsyncStateMachine` interface check) will **silently under-count or entirely miss** suspended async methods compiled with Runtime Async on, because those methods produce no `d__N`/`IAsyncStateMachine` type at all. This is not a crash risk — the analyzer degrades to reporting only the legacy-compiled subset of async methods — but it is a correctness/completeness risk that will get worse as more code (starting with the BCL and ASP.NET Core itself) ships Runtime Async-compiled.

**Compatibility constraint:** .NET Framework and all .NET versions ≤ 10 (and any .NET 11+ assembly that opts out) will continue to use the classic compiler-generated `d__N`/`IAsyncStateMachine` model indefinitely — mixed-mode dumps (old-style libraries calling into new-style application code, or vice versa) are the expected steady state for years. The existing detection path must **not** be removed or altered; it must remain the baseline, with Runtime Async detection added alongside it.

**Recommended action (tracked as P3-4):** Do not implement Runtime Async detection yet — the on-heap shape is unstable pre-GA. Instead:
1. Re-audit this analyzer once .NET 11 GA ships and ClrMD/DAC surfaces the finalized spilled-locals shape (watch `Microsoft.Diagnostics.Runtime` release notes for Runtime Async / `RuntimeAsyncTask` support).
2. When implemented, add detection as an *additive* signal (e.g. a second `TypeAggregateFlags` bit or a name/namespace check for `RuntimeAsyncTask`), never replacing the existing `d__N`/`IAsyncStateMachine` path.
3. Ensure the analyzer degrades gracefully (skip, not throw) on any dump where Runtime Async types are absent — which is every dump today and most dumps for the near future.

---

## Final Executive Summary

### Overall Assessment

**Score: 86 / 100** (was 62/100 at original audit; 80/100 immediately after re-audit, before P0-4/P1-7/P1-8 were fixed same-session)

**Production readiness:** Production-ready, including for codebases with generic async methods — the P0-4 regex-drift regression (found in this re-audit) has been fixed by unifying both the Phase 1 flag computation and Phase 2 candidate parsing on one shared regex. The `statemachine.gen2.fraction` trend metric (P1-7) now measures what its name claims. Remaining gaps (Task linkage, call tree reconstruction, analyzer-level test coverage) are capability/confidence gaps rather than correctness risks.

**Major strengths (updated):**
- Zero-heap-scan candidate selection via the Phase 1 `IsAsyncStateMachineType` flag — O(types), not O(types × regex/interface cost).
- Real suspend-state histogram (P2-1) replacing the old single-sample "average" — a genuine diagnostic upgrade, and implemented using the correct pattern (disk-index-first, bounded, early-exit).
- Gen2 count/fraction now surfaced — enables leak-vs-throughput triage that was previously impossible.
- `async void` detection with dedicated finding — closes a real production risk pattern.
- Finding coverage broadened from 2 to 4 distinct finding types, with proper severity escalation and multi-offender reporting.
- Capture estimate is honestly annotated rather than presented as exact.
- **New:** the state-machine name pattern now has exactly one source of truth (`AsyncStateMachineNamePattern.Regex`), eliminating the class of drift bug that caused P0-4 — and the regex unit tests now exercise that actual shared instance rather than a disconnected copy.
- **New:** `statemachine.gen2.fraction` is now scope-consistent (aggregated over all candidates, not just the reported top-N), with a dedicated regression test (`ExtractMetrics_UsesTotalGen2Count_NotTopStateMachineTypesSum`) guarding against the fix silently reverting.

**Major weaknesses (updated):**
- No analyzer-level unit tests for `AsyncStateMachineAnalyzer.Analyze` itself (P2-8, still open); the one integration test that could exercise the disk/live-heap fallback path is still broken on a pre-existing, unrelated test-harness bug (`FileNotFoundException` on a synthetic dump path).
- Task linkage and async call tree reconstruction remain unimplemented — the largest capability gaps vs. `!dumpasync`.
- Minor, still open: `ScanLimited` impact unquantified (P2-6), zero-result case has no narrative (P2-7), histogram pass (Step 3b) has no progress reporting (P2-5).

### Priority Roadmap

| ID | Recommendation | Classification | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|
| P0-1 | Add `IsAsyncStateMachineType` flag to `TypeAggregateFlags` (bit 5); set in `DiskBackedObjectIndexWriter.ComputeTypeFlags` using name pattern + `EnumerateInterfaces` | Evolution | High — eliminates full-type scan; enables O(matching types) Phase 2 | Medium | High | DONE |
| P0-2 | Add `Gen2Count` and `Gen2Fraction` to `StateMachineTypeProfile`; expose in section table | Improvement | High — enables leak vs. throughput distinction | Low | High | DONE |
| P0-3 | Rename `AvgStateValue` → `SampleStateValue` (or replace with distribution); update domain model, section builder column header, and any downstream consumers | Improvement | High — removes actively misleading column | Low | High | DONE |
| **P0-4** | Fix regex drift: `DiskBackedObjectIndexWriter.IsAsyncStateMachineType` still used the pre-P2-4 non-generic-aware pattern, silently excluding generic async methods before Phase 2's fixed regex was ever reached. Extracted one shared `AsyncStateMachineNamePattern.Regex` in `DumpDetective.Analysis.Utilities`, consumed by both `DiskBackedObjectIndexWriter` and `AsyncStateMachineAnalyzer`; repointed `AsyncStateMachineRegexTests` at the shared instance instead of a fourth private copy | Improvement | High — restores P2-4's intended coverage; correctness regression affecting common (generic async) code patterns | Low | High | DONE |
| P1-1 | Move `heap.GetTypeByMethodTable(kv.Key)` after name pre-check (or after flag check once P0-1 lands); avoids ClrMD resolution for non-candidate types | Improvement | Medium — reduces Phase 2 latency on large dumps | Low | High | DONE |
| P1-2 | Wrap `StateMachinePattern.Match` in try/catch for `RegexMatchTimeoutException`; log and continue | Improvement | Medium — prevents single-type failure from aborting analysis | Low | High | DONE |
| P1-3 | Remove `break` in fire-and-forget finding loop; report top-3 offenders above threshold, not just one | Improvement | Medium — surfaces all fire-and-forget sinks | Low | High | DONE |
| P1-4 | Escalate fire-and-forget finding severity based on `SuspendedCount` (e.g. Warning ≥100, Error ≥1000, Critical ≥10000) | Improvement | Medium — accurate severity triage | Low | High | DONE |
| P1-5 | Replace `FormatBytes` in both `AsyncStateMachineAnalyzer` and `AsyncStateMachineFindingGenerator` with `FormatHelper.FormatBytes` | Improvement | Low — removes duplication | Trivial | High | DONE |
| P1-6 | Add state value interpretation guidance to section narrative (table footnote or prose block) | Improvement | Medium — makes state column actionable without external docs | Low | High | DONE |
| **P1-7** | Fix `statemachine.gen2.fraction` trend metric: numerator (`Gen2Count` summed over `TopStateMachineTypes`, bounded by `TopTypeLimit`) vs. denominator (`TotalStateMachines`, bounded by `TypeCandidateLimit`) scope mismatch systematically understated the fraction when candidate types exceeded `TopTypeLimit`. Added `AsyncStateMachineDomainResult.TotalGen2Count`, aggregated over all candidates (same scope as `TotalStateMachines`) in the analyzer's Step 2; trend comparer now reads it directly instead of re-deriving from `TopStateMachineTypes` | Improvement | Medium — trend regression tracking (P3-3) now reports a metric that measures what its name claims | Low | High | DONE |
| **P1-8** | Remove dead `ImplementsIAsyncStateMachine` method (unused since P0-1 shipped) | Improvement | Low — removes confusing dead code, especially given P0-4 | Trivial | High | DONE |
| P2-1 | State value histogram per top type (bounded instance scan for top-10 types, max 1000 instances each) | Improvement | High — identifies specific stuck await points | Medium | High | DONE |
| P2-2 | Detect `async void` originating methods; add `IsAsyncVoid` flag to `StateMachineTypeProfile`; generate a dedicated Warning finding | Improvement | High — unobservable failure risk pattern | Medium | Medium | DONE |
| P2-3 | Annotate capture estimate as "shallow (direct references only)"; add explicit sharing-caveat note | Improvement | Medium — prevents misinterpretation of `HighCaptureStateMachine` table | Low | High | DONE |
| P2-4 | Verify regex `<(.+?)>d__\d+$` against generic async state machine type names emitted by ClrMD; add `Contains(">d__")` post-processing to handle trailing generic params if needed | Improvement | Medium — correctness risk for generic async methods | Low | Medium | DONE — see P0-4 for the follow-up fix. The Phase 2 regex was fixed as originally scoped, but the Phase 1 flag that gated candidate inclusion was not updated at the time, so the fix did not take effect end-to-end until P0-4 unified both call sites on one shared regex. |
| **P2-5** | **(New)** Add progress reporting to Step 3b (histogram pass) via `context.Progress`/`ObjectScanCounter`, consistent with `AsyncTaskAnalyzer.ScanRawHeapForTasks` | Improvement | Medium — Step 3b is the first potentially-large streaming pass in this analyzer; currently invisible if it runs long on a pathological large dump | Low | High | **NOT DONE** |
| **P2-6** | **(New)** Quantify `ScanLimited` impact — estimate skipped type count / byte fraction when `TypeCandidateLimit` is hit, not just a boolean flag | Improvement | Medium — makes truncation impact assessable instead of just visible | Low | Medium | **NOT DONE** |
| **P2-7** | **(New)** Add zero-result section narrative ("No async state machine types detected on this heap") | Improvement | Low — readability polish | Trivial | High | **NOT DONE** |
| **P2-8** | **(New)** Add analyzer-level unit tests for `AsyncStateMachineAnalyzer.Analyze` (synthetic `TypeAggregates`, verify candidate selection/histogram/async-void/Gen2 wiring); fix the pre-existing `AsyncStateMachineAnalyzerDiscrepancyTests` harness bug (`FileNotFoundException` on synthetic `freshDumpPath`) so the disk-vs-memory safety net is actually exercisable | Improvement | Medium — this audit's two headline findings (P0-4, P1-7) were both found by code reading; no existing test would have caught either | Medium | High | **NOT DONE** |
| P3-1 | Task linkage: read `AsyncTaskMethodBuilder.m_task` from state machine sample; cross-reference with `AsyncTaskAnalyzer` results (absorbs [AsyncTaskAnalyzer P1-2](async-task-analyzer-audit.md#priority-roadmap), which is superseded here) | Evolution | High — enables cross-analyzer async call graph | High | Medium | NOT DONE |
| P3-2 | Async call tree reconstruction analogous to `!dumpasync` (state machine → builder task → continuation → next state machine) | Evolution | Very High — flagship capability | Very High | Medium | NOT DONE |
| P3-3 | Add `statemachine.gen2.count` and `statemachine.gen2.fraction` to `AsyncStateMachineTrendComparer` metrics | Improvement | Medium — enables regression tracking of long-lived suspensions | Low | High | DONE (see P1-7 for a correctness follow-up on the fraction metric) |
| P3-4 | Re-audit against .NET 11 GA Runtime Async; add additive `RuntimeAsyncTask`/spilled-locals detection alongside (not replacing) `d__N`/`IAsyncStateMachine` scan once ClrMD exposes the finalized shape | Evolution | High — prevents silent under-counting as Runtime Async adoption grows | Medium | Low (spec not final) | NOT DONE (blocked on .NET 11 GA) |

### Final Verdict

1. **Is the analyzer production-ready?** Yes, including for codebases with generic async methods. The P0-4 regex-drift regression found in this re-audit was fixed same-session (one shared regex, referenced by both the Phase 1 flag computation and the Phase 2 candidate parser). The classic detection path, population counts, Gen2 signal, suspend-state histogram, and finding coverage are all solid and reliable.

2. **Highest-impact improvement remaining:** P2-8 (add analyzer-level test coverage, and fix the broken discrepancy-test harness) — this audit's headline findings (P0-4, P1-7) were both found by manual code reading, not by any existing test, and would recur silently without dedicated coverage. A regression test for P1-7 was added as part of its fix; P0-4 does not yet have an equivalent end-to-end (ClrType-level) regression test, only the now-shared regex's unit tests.

3. **Platform evolution opportunities:** Unchanged from original audit — Task linkage (P3-1) and async call tree reconstruction (P3-2) remain the largest gap against `!dumpasync` and the most significant available competitive capability improvement in this domain. The histogram (P2-1) closed part of the state-reporting gap but not the linkage gap.

4. **Highest engineering return going forward:** P2-8 (test coverage — cheapest way to prevent a repeat of this audit's findings), followed by the low-effort P2-5/P2-6/P2-7 polish items, before further P3 feature work (Task linkage, call tree reconstruction) which is high effort and high value but not urgent.

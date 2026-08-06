# JitAnalyzer Audit

> Protocol: `phase1-analyzer-architecture-review.md`
> Components reviewed: `JitAnalyzer.cs`, `JitDomainResult.cs`, `JitAnalysisOptions.cs`,
> `JitFindingGenerator.cs`, `JitSectionBuilder.cs`, `JitTrendComparer.cs`,
> `JitAnalyzerDiscrepancyTests.cs`, `JitFindingGeneratorTests.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

The analyzer covers three distinct but related concerns:

1. **JIT code-heap accounting** — byte totals from `EnumerateJitManagers()` / `EnumerateNativeHeaps()`
2. **Stack-frame distribution** — managed vs unmanaged frame counts across live threads
3. **Active method profiling** — type-level stack heatmap, large-method tracking, tiered compilation signal

Cohesion is good. All three concerns are logically JIT-runtime metadata and require no heap scan.

### Coverage Gaps

| Gap | Impact |
|---|---|
| Only methods appearing on live thread stacks are profiled; the complete set of JIT-compiled methods is invisible | High — a process may have hundreds of thousands of compiled but inactive methods consuming code heap |
| Dynamic method detection (`DynamicMethod`, `Reflection.Emit`, expression-compiled delegates) absent | Medium — dynamic codegen is a common source of code-heap growth |
| Per-JIT-manager heap breakdown not surfaced in the model | Low — total is shown, but which manager holds the large segment is not |
| ReadyToRun vs JIT-compiled method distinction missing | Medium — inflates apparent JIT footprint when R2R modules are loaded |
| Per-module JIT contribution absent | Medium — cross-referencing module list with frame heatmap would pinpoint hot assemblies |

### Expansion Opportunities

- Cross-reference `TopActiveFrameTypes` with `ModuleDomainResult` to produce a per-module JIT stack heatmap.
- Detect `<DynamicClass>` / `DynamicMethod` frames on stacks and surface a dedicated count.
- Expose per-JIT-manager byte breakdown rather than just the total.

### Architectural Observations

`JitHeapPctOfTotalProcess` is modelled in `JitDomainResult` but hardcoded to `0.0` with a comment stating it is "not computable from dump alone". This is inaccurate; the working-set or virtual-size of the process is available from `DataTarget.DataReader` (Windows) or from OS metadata embedded in minidumps. At minimum the field should be documented as intentionally absent rather than confusingly zero.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- JIT code-heap overview finding is always emitted — engineers always get baseline numbers.
- Large-method table in `JitSectionBuilder` includes hot/cold split with address and a size flag — actionable.
- Unmanaged frame ratio signal is well-calibrated (≥30 % and >50 frames) avoiding false positives on tiny stacks.
- Trend comparer tracks five metrics including `jit.heap.bytes` as `HigherIsWorse` — correct.

### Weaknesses

1. **`JitHeapPctOfTotalProcess` is always 0.0.** The section builder conditionally omits it (`if (d.JitHeapPctOfTotalProcess > 0.0)`), so engineers never see a process-relative figure. The field is dead weight in the current state.

2. **`JitMethodSnapshot.IsTiered` is always `false`.** `BuildTopMethods` hardcodes `IsTiered: false` regardless of whether the method was detected in `tokenToNativeCode` as tiered. The "Tiered" column in the section builder table is therefore always "No", making it misleading.

3. **Finding generator surfaces only the single highest-priority signal** plus the overview. If both JIT heap bloat and large methods are detected, the large-method signal is discarded. Engineers investigating a large-method problem while heap bloat also exists will miss it in the findings list.

4. **Hardcoded "64 KB threshold" in finding text** does not reflect the configurable `LargeMethodThresholdBytes` option. The string will be incorrect when the option is changed via profile or config file.

5. **No total unique method count** — engineers cannot distinguish "one method observed 500 times across threads" from "500 distinct methods each observed once."

6. **Tiered compilation signal is unreliable** (see Area 6). Its diagnostic value is low.

### Missing Diagnostics

- Whether any large method is a known generic instantiation (e.g., `List<T>.Sort` expanded multiple times) — ClrMD `ClrType.IsGeneric` and method signature parsing could help.
- Methods observed simultaneously on many threads (likely contention or hot-path bottleneck).
- Namespace-level aggregation of frame types for large codebases where thousands of types are present.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Well-Used APIs

| API | Usage |
|---|---|
| `ClrRuntime.EnumerateJitManagers()` | Correct; only reliable way to get native code heap totals |
| `ClrNativeHeapInfo.MemoryRange.Length` | Correct |
| `ClrMethod.HotColdInfo` (HotColdRegions) | Correct; exposes hot/cold native code size |
| `ClrMethod.MetadataToken` | Correct for tiered detection identity |
| `thread.IsAlive` guard | Correct; dead threads have stale/empty stack walks |

### Improvement Opportunities

1. **`ClrMethod.NativeCode` accessed inside the hot frame loop.** In ClrMD this may trigger a native heap lookup per call. Assign `method.NativeCode` to a local once per method rather than reading it multiple times (it is read twice: tiering and candidate tracking).

2. **`ClrMethod.CompilationType`** (available as `MethodCompilationType` enum in some ClrMD versions) distinguishes JIT, ReadyToRun, and NGEN. Using it would allow filtering R2R frames out of the large-method list and would eliminate the "R2R detection not available" disclaimer in `JitSectionBuilder`.

3. **`HeapAnalysisCache` is unused** — correct, since this is a runtime-metadata-only analyzer. No opportunity missed.

4. **No cancellation check inside the frame enumeration inner loop.** For threads with very deep stacks (or with `MaxFramesPerThread` large), a single thread's frame walk cannot be interrupted. The check should be inside the frame loop or at least every N frames.

### Index Opportunities

No new disk index is warranted. All data derives from in-memory runtime metadata that is cheap to re-read.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Opportunities

| Opportunity | Value | Difficulty |
|---|---|---|
| **Per-thread managed frame depth** — deepest stacks point to recursive loops or re-entrant paths | High | Low |
| **Multi-thread method overlap** — methods active on ≥ N threads are hot-path / contention candidates | High | Low |
| **Dynamic method detection** — count frames from `DynamicMethod`/`<DynamicClass>` type names | Medium | Low |
| **Method uniqueness ratio** — `ActiveMethodsOnStacks / (number of distinct method signatures seen)` exposes reuse vs thrash | Medium | Low |
| **Per-JIT-manager breakdown** — report each manager's heap size separately | Low | Low |
| **NativeCode == 0 method count** — interpreted/not-yet-JIT methods visible on stacks indicate startup or constrained JIT | Medium | Medium |
| **ReadyToRun frame fraction** — once `CompilationType` is used, report what percentage of active frames are R2R vs JIT | Medium | Medium |
| **Cold-region fraction of large methods** — a high cold/hot ratio indicates code rarely executed but JIT-compiled eagerly | Low | Low |

### Evidence Recommendations

- Surface `ActiveMethodsOnStacks` vs distinct method signatures seen as a reuse ratio in key metrics.
- Add `DynamicMethodFrameCount` to `JitDomainResult`; populate from frames whose declaring type matches `<DynamicClass>` or `DynamicMethod`.

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment

The analyzer performs no heap enumeration. Runtime cost is dominated by thread stack walks.

| Concern | Assessment |
|---|---|
| Stack walk cost | O(threads × frames). With 1000 threads × 200 frames = 200K frames, this is fast. With 400 frames (Full profile) and 5000 threads (large server dumps), 2M frames is still milliseconds. |
| `tokenToNativeCode` dictionary | Bounded by distinct token × NativeCode pairs seen on stacks — well within memory budgets. Initial capacity hint of 1024 is reasonable. |
| `methodCandidates` dictionary | Bounded by `LargeMethodThresholdBytes` filter — small in practice. |
| `frameTypeCounts` dictionary | Bounded by distinct type names on stacks — typically a few thousand at most. |
| `Array.Sort` over candidates | O(N log N) where N is small (filtered by threshold). No concern. |
| Cancellation granularity | Checked between threads but not inside the per-thread frame loop. For processes with abnormally deep stacks (recursive crash dumps) this could delay cancellation. |
| No progress reporting | Minor gap; stack walks are fast enough that progress is rarely needed. |

### Scalability Assessment

This analyzer scales well to dumps of any size because it is independent of heap object count. No changes are needed for the 10–100 GB target range. The only scenario that could cause latency is a dump with tens of thousands of live threads and `MaxFramesPerThread = 400`, which is still bounded.

---

## Audit Area 6 — Correctness & Confidence

### Critical Issues

**1. `IsTiered` is always `false` (bug)**

In `BuildTopMethods`, `IsTiered: false` is hardcoded for every `JitMethodSnapshot`. The tiered detection logic in the analysis loop populates `tokenToNativeCode` and increments `tieredMethodCount`, but never marks individual method candidates. Engineers who rely on the "Tiered" column in the report to identify which specific large methods have been tier-promoted will always see "No" regardless of actual state.

**2. Tiered detection is logically unreliable**

The approach increments `tieredMethodCount` when the same `MetadataToken` appears on two different threads with different `NativeCode` addresses. This conflates two distinct scenarios:

- A method genuinely at different tiers on different threads (valid tiering signal).
- A generic method instantiated differently on different threads, producing different native code bodies with the same token.

`MetadataToken` alone does not uniquely identify a JIT compilation unit; generic instantiations and the owning module also matter. Without `ClrMethod.MethodDesc` or a `(module, token, generic instantiation)` tuple, the count may over-report.

Additionally, a snapshot dump captures one instant in time. The same method being at different tiers on different threads simultaneously is possible but unlikely after the process has warmed up. In practice this counter will often be zero or near-zero for steady-state processes, reducing its diagnostic value.

### Other Correctness Issues

| Issue | Severity |
|---|---|
| Finding text hardcodes "64 KB threshold" — inaccurate when `LargeMethodThresholdBytes` is overridden by config | Low |
| `JitHeapPctOfTotalProcess = 0.0` is not documented as intentionally unavailable in the model — callers may misinterpret | Low |
| `MaxFramesPerThread` cap silently truncates stacks; methods below the cap are excluded from all counts with no indication | Low |

### Confidence Assessment

- JIT heap byte totals: **High** — direct enumeration of runtime heaps, not derived.
- Frame distribution counts: **High** — accurate over walked frames, subject to `MaxFramesPerThread` cap.
- Large-method list: **Medium** — only captures methods on live stacks; not the complete compiled corpus.
- Tiered method count: **Low** — logic is unreliable (see above).
- `IsTiered` flag per method: **None** — always false.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| SOS capability | DumpDetective coverage |
|---|---|
| `!eeheap -jit` — per-manager, per-segment code heap breakdown | Total only; per-segment missing |
| `!clrstack -all` → `!ip2md` heatmap | Automated via `TopActiveFrameTypes` — equivalent or better |
| `!sos.dumpmt` for large method bodies | Large method table with hot/cold split — comparable |
| Dynamic method detection via `!dumpheap -type DynamicClass` | Absent |

### PerfView

PerfView's JIT compile event log (from ETW) shows tier, duration, and method size at compile time. A dump snapshot cannot replicate this — DumpDetective correctly makes no claim about JIT compile duration. The tiered compilation heuristic is a weak substitute.

### Visual Studio Memory Usage

No significant JIT analysis capabilities; not a relevant benchmark for this analyzer.

### JetBrains dotMemory

dotMemory profiling mode shows JIT time per method. Static dump analysis cannot provide this. Not applicable.

### Competitive Opportunities

1. **Per-module JIT footprint** — WinDbg requires manual correlation; DumpDetective could automate this by joining `TopActiveFrameTypes` with module metadata.
2. **Dynamic method count** — SOS requires manual effort; automating frame classification for `DynamicMethod` origins would be unique value.
3. **ReadyToRun fraction** — distinguishing R2R from JIT in the large-method table is not straightforward in WinDbg; DumpDetective could surface this clearly via `CompilationType`.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

**Production readiness: Conditional** — safe to use but contains two correctness defects that produce misleading output (`IsTiered` always false, tiered count unreliable). The JIT heap total and frame distribution metrics are accurate and useful.

**Major strengths:**
- Zero heap enumeration; safe and fast at any dump size.
- JIT code heap total via `EnumerateJitManagers` is the correct API and produces accurate results.
- Large-method table with hot/cold split is immediately actionable for JIT inlining investigations.
- Frame type heatmap provides an automated equivalent of the `!clrstack + !ip2md` manual workflow.
- Trend metrics and finding generator cover the most important signals.

**Major weaknesses:**
- `IsTiered` always false on `JitMethodSnapshot` — dead column in the report.
- Tiered compilation detection logic is semantically flawed.
- `JitHeapPctOfTotalProcess` is always 0.0 — the model field is misleading.
- Only stack-visible methods are profiled; the JIT-compiled method corpus is otherwise invisible.
- Finding generator surfaces only the top signal, discarding concurrent signals.

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| **P0** | ✅ Fix `IsTiered` flag: wire tiered status from `tokenToNativeCode` to `JitMethodSnapshot` in `BuildTopMethods` | Medium — correct the misleading report column | Low | High | Improvement |
| **P0** | ✅ Expose `JitHeapPctOfTotalProcess` as `null` or remove the field rather than emitting 0.0 | Low — prevents misinterpretation | Low | High | Improvement |
| **P1** | Fix finding generator to emit all signals, not just the single highest-priority one | Medium — engineers lose concurrent signal context | Low | High | Improvement |
| **P1** | Fix hardcoded "64 KB threshold" string in `JitFindingGenerator` to reflect actual `LargeMethodThresholdBytes` option value | Low | Low | High | Improvement |
| **P1** | Add cancellation check inside the per-frame inner loop (every N frames) | Low — correctness under cancellation | Low | High | Improvement |
| **P2** | Replace tiered count heuristic with `(module handle, token, generic context)` tuple identity; mark the count as an estimate | Medium — improves confidence | Medium | High | Improvement |
| **P2** | Use `ClrMethod.CompilationType` to distinguish R2R vs JIT frames; remove the R2R disclaimer from the section builder and add an R2R frame fraction metric | Medium — removes misleading disclaimer, adds value | Medium | Medium | Improvement |
| **P2** | Add per-thread max frame depth to `JitDomainResult` and report; exposes recursive/re-entrant crash patterns | Medium | Low | High | Improvement |
| **P2** | Add `DynamicMethodFrameCount` — detect `<DynamicClass>` frames and surface a dedicated count and finding | Medium — dynamic codegen is a common code-heap growth cause | Low | Medium | Improvement |
| **P3** | Cross-reference `TopActiveFrameTypes` with `ModuleDomainResult` to produce a per-module JIT stack heatmap | Medium | Medium | Medium | Evolution |
| **P3** | Add method uniqueness ratio (`ActiveMethodsOnStacks / distinct signatures`) as a key metric | Low | Low | High | Improvement |

### Final Verdict

1. **Production-ready with caveats.** The JIT heap total and frame heatmap are accurate. The tiered compilation fields are not reliable and should be treated as approximate until corrected.
2. **Highest-impact improvements:** Fix `IsTiered` (P0, low effort), fix `JitHeapPctOfTotalProcess` (P0, low effort), emit all findings not just the top one (P1, low effort).
3. **Platform evolution opportunities:** Per-module JIT stack heatmap by joining with `ModuleDomainResult` is the clearest cross-analyzer value add.
4. **Highest engineering return:** The P0 and P1 items collectively take less than a day and correct misleading data in the current output. `ClrMethod.CompilationType` integration (P2) would eliminate the R2R disclaimer and is the next most impactful single change.

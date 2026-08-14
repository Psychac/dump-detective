# GC Root Owner Attribution — Platform Plan

> Status: ✅ **DONE.** Both mechanisms shipped. Mechanism A (static fields) —
> disk-persisted. Mechanism B (stack frames) — live, on-demand, top-N only.
> Remaining open item: no end-to-end test against a real multi-thread dump exercises
> `RootSetCache.BuildStackFrameOwnerMap` itself (see Mechanism B's Open
> questions/risks) — tracked there, not a blocker for this plan's completion.
> Supersedes the P1-1
> recommendation in [phase1/gcroot-analyzer-audit.md](phase1/gcroot-analyzer-audit.md)
> ("Capture `ClrRoot.RootName`") and folds in the field-attribution mechanism already
> shipped for
> [phase1/static-root-leak-detector-audit.md](phase1/static-root-leak-detector-audit.md)
> P1-1.

---

## Problem

`GCRootAnalyzer`'s `RootFinding.FieldDescription` is hard-coded `null`
(`GCRootAnalysisProjection.Build`) and always renders as `"—"` in the report. The
audit doc's P1-1 proposed fixing this by capturing `ClrRoot.RootName` during root
enumeration and persisting it on `RootRecord` / in `RootIndex.bin`.

**That proposal does not hold against this project's actual ClrMD dependency.**
Checked directly against `microsoft.diagnostics.runtime 4.0.732401` (the version this
branch — `upgrade/clrmd-4` — targets): `ClrRoot` exposes only `Address`, `Object`,
`RootKind`, `IsInterior`, `IsPinned`. There is no `RootName` or `StackwalkType`
property in ClrMD 4 — those were assumed present by the audit doc but don't exist in
this API surface.

What *does* exist, once you look past `ClrRoot` itself, are two independent,
kind-specific attribution mechanisms — neither of which is "capture `RootName`":

- **Mechanism A — static field identity**, for `StaticVar`/`ThreadStaticVar` roots,
  via `ClrType.StaticFields`. Exact and complete: every static root has exactly one
  declaring field.
- **Mechanism B — owning-method identity**, for `Stack` roots, via correlating a
  stack root's slot address against the thread's frame `StackPointer` ranges. Not
  exact (no local-variable name — that needs PDB data ClrMD doesn't expose) but a
  real improvement: `"Stack @ 0x7FF012345"` → `"Stack in MyService.ProcessRequest()"`.

For everything else — `StrongHandle`, `PinnedHandle`, `RefCountedHandle`,
`AsyncPinnedHandle`, `SizedRefHandle`, `FinalizerQueue` — there is no owner identity
to recover, from ClrMD or otherwise. These are GC handle table slots or finalizer
queue entries, anonymous by construction at the runtime level; the "name" only ever
existed in the source code that allocated the handle. This isn't a ClrMD gap, it's
structural, and `FieldDescription = null` is the permanently correct answer for these
kinds.

## Why this is a platform concern, not a `GCRootAnalyzer` concern

`GCRootAnalyzer` and `StaticRootLeakDetector` both consume `RootSetCache` as their
canonical root source. Mechanism A is already shared between them (see below).
Mechanism B is `GCRootAnalyzer`-only today (`StaticRootLeakDetector` is scoped to
static roots by definition, so it never needs stack-frame attribution), but should
still live in `RootSetCache` alongside Mechanism A rather than inline in the
analyzer, for the same reason: a single capture point for "what does this root
address resolve to," not two.

---

## Mechanism A — static field identity (`StaticVar` / `ThreadStaticVar`)

### A real bug in the existing implementation — fixed

`RootSetCache.GetStaticFieldsByTargetAddress` (live-only, called from
`StaticRootLeakDetector.AnalyzeStaticRoots`) used to build its map keyed by the
**target object's address**:

```csharp
ClrObject fieldValue = field.ReadObject(domain);
if (fieldValue.Address != 0 && !map.ContainsKey(fieldValue.Address))
    map[fieldValue.Address] = (type.Name, field.Name, domainId);
```

This was ambiguous: if two different static fields (from different types, or the
same type in different AppDomains) reference the same object, only the
first-encountered field wins — the map cannot express "this object is rooted by both
`A.s_cache` and `B.s_singleton`," and can attribute the wrong field to a given root.

`ClrStaticField.GetAddress(ClrAppDomain)` returns the **field's own storage
address** — the location holding the pointer, not the pointed-to object. For a
`StaticVar`/`ThreadStaticVar` root, that address is the same quantity as
`ClrRoot.Address` (i.e. `RootRecord.RootAddr`). Keying the map by
`field.GetAddress(domain)` instead of `fieldValue.Address`, and looking it up by
`RootAddr` instead of `TargetAddr`, gives an exact 1:1, unambiguous correspondence —
every static field has a unique storage address, even when multiple fields point at
the same object.

> ✅ **Fixed** (live path only, no disk-index changes yet). `RootSetCache` now builds
> the map keyed by `field.GetAddress(domain)` and exposes it as
> `GetStaticFieldsByRootAddress` (renamed from `GetStaticFieldsByTargetAddress`; the
> `ReadObject`/target-address branch is gone entirely — no live object read needed to
> build the map anymore). `StaticRootLeakDetector.AnalyzeStaticRoots` now sources both
> `TargetAddr` and `RootAddr` per root via a new `RootSetCache.GetOrBuildRootTriples` /
> `IHeapAnalysisCache.GetOrBuildRootTriples` projection (needed because the existing
> `GetOrBuildValidRoots` compatibility shape only carries `TargetAddr`), and looks the
> field map up by the real `RootAddr` instead of the misleadingly-named `rootAddress`
> local that was actually `TargetAddr`. `IHeapAnalysisCache`/`HeapAnalysisCache` and
> the `RunAnalyzersPipelineStageTests` fake were updated to match. No
> `RootIndexWriter`/`RootIndexReader` changes, no format version bump; the map is
> still built live, on every analysis run, same as before.

### Disk persistence — done

> ✅ **Done.** `RootIndexWriter.Write` now computes the same `RootAddr → (OwnerType,
> FieldName, AppDomainId)` map during the Roots section write (via a new shared
> `StaticFieldResolver.BuildMapByRootAddress`, extracted so `RootSetCache`'s live
> fallback and `RootIndexWriter`'s Phase-1 build compute the identical map instead of
> drifting), bounded to only the static/thread-static roots actually enumerated this
> run (not every static field in the dump). It's appended as a variable-length
> trailer on the `Roots` section — `RootAddr(8) | OwnerTypeLen(2) | FieldNameLen(2) |
> AppDomainId(4) | OwnerType(N) | FieldName(M)` — following the `Module records`
> precedent in `TypeAggregateIndexWriter`. The trailer's record count is stashed in
> the shared `IndexHeader`'s `Reserved` field via a new `IndexHeader.PatchReserved`
> (mirroring `PatchRecordCount`), and `RootHeaderVersion` bumped 1 → 2 — an old
> `cache.bin` now yields zero roots from disk entirely (not just missing names) until
> the next full rebuild, the accepted trade-off described below.
>
> `RootIndexReader.ReadRootFieldNames` reads the trailer back;
> `RootSetCache.GetStaticFieldsByRootAddress` tries it first and falls back to the
> live scan on a miss/error, same method name and key shape as before — only the
> backing implementation changed. `GCRootAnalysisProjection.Build` now populates
> `FieldDescription` for `StaticVar`/`ThreadStaticVar` findings from this map,
> formatted as `$"{OwnerType}.{FieldName}"` (`[AppDomain#N]` suffix when
> non-default), so the report's "Field" column (`GCRootIntelligenceSectionBuilder`,
> already wired to render `FieldDescription ?? "—"`) now shows real values with no
> report-layer changes needed.
>
> **Bug found and fixed along the way**: `IndexHeader.TryRead` never actually read
> the `Reserved` field from the file — it discarded bytes 16-23 and always
> constructed the header via the 3-arg constructor, which hardcodes `Reserved = 0`.
> Harmless until now (no prior consumer used `Reserved` for anything), but it broke
> the trailer-count read silently (`ReadRootFieldNames` always saw `Reserved == 0`
> and returned an empty dictionary even when the trailer was present and
> byte-correct on disk — confirmed by reading the raw file bytes directly, which
> matched the expected layout exactly). Fixed by adding a private 4-arg constructor
> and having `TryRead` read and pass the real `Reserved` bytes.
>
> Also fixed a checksum-ordering trap while writing the test coverage for this:
> `CacheContainerWriter.EndSection` computes and stores the section's checksum from
> its current bytes, so any header patch (`PatchRecordCount`/`PatchReserved`) must
> happen *before* `EndSection` is called, not after — patching afterward leaves the
> stored checksum stale, which `CacheContainerReader.TryOpenSection` then treats as
> section corruption (silently returns "section missing", not an exception).
> `RootIndexWriter.Write` already had this right (all patches happen inside `Write`,
> before the caller's `EndSection`); the test helper needed the same ordering.
>
> Test coverage: `RootIndexReaderTests` covers the v1→v2 rejection, the empty-file
> case, and a full trailer round-trip. `docs/binary-format.md` § Roots section
> updated with the v2 layout.

---

## Mechanism B — owning-method identity (`Stack`)

### Why this needs a different code path than Mechanism A

`ClrRoot` (whether from `heap.EnumerateRoots()` or `thread.EnumerateStackRoots()`)
carries no thread affiliation — there's no `Thread` property on it. The *only* way to
know which thread (and therefore which frame) a given `Stack`-kind root belongs to is
to enumerate roots **per thread**, via `ClrThread.EnumerateStackRoots()`, not via the
aggregate `heap.EnumerateRoots()` that `RootIndexWriter`/`RootSetCache` build from
today. This means Mechanism B cannot simply add a lookup keyed off the existing
`Roots` section data — it needs its own per-thread walk.

### Design

For each thread (`runtime.Threads`):

1. Call `thread.EnumerateStackTrace()` to get its `ClrStackFrame`s, each exposing
   `StackPointer` and `Method`. Sort by `StackPointer` ascending — on the
   conventional (x64) stack, the innermost/current frame has the lowest `SP`, and
   `SP` increases moving outward toward the caller.
2. Call `thread.EnumerateStackRoots()` to get that thread's `ClrRoot`s. Each root's
   `Address` is the stack slot holding the pointer (0 when the value is enregistered
   rather than spilled to the stack — those roots get no attribution, same as today).
3. For each root with a non-zero `Address`, binary-search the sorted frame list for
   the frame whose `[StackPointer, nextFrame.StackPointer)` range contains it. That
   frame's `Method` (`ClrMethod.Type?.Name` + `.` + `ClrMethod.Name`) is the owning
   method.
4. Build `RootAddr → (OwnerType, MethodName)` from this, keyed by the root's stack
   slot address — same key shape as Mechanism A (`RootAddr`), different source and
   different semantic (method that owns the frame, not field that owns the
   reference), reusing the same `(string, string, int)` tuple shape with
   `AppDomainId` populated from `thread.CurrentAppDomain?.Id ?? 0` (best-effort;
   less meaningful for stack roots than for static ones, since a thread's stack can
   span calls into multiple AppDomains over its lifetime and `CurrentAppDomain` only
   reflects the current one).

### Scope: live, on-demand, top-N only — not a full Phase-1 walk

Unlike Mechanism A, this should **not** run unconditionally over every thread/root
during Phase-1 build. Reasons:

- It requires a full stack unwind (`EnumerateStackTrace`) per thread, which is a
  separate, non-trivial ClrMD operation from the heap/root scan `RootIndexWriter`
  already does — adding it unconditionally to Phase-1 build time isn't justified
  when only a small subset of `Stack` roots (the top-severity ones `GCRootAnalyzer`
  actually surfaces in findings) need it.
- `GCRootAnalyzer` already has a "rank by cheap proxy, spend budget only on
  survivors" pattern for exactly this kind of situation
  (`RetainedSizeCandidateSelector`, see
  [retained-size-candidate-selection.md](retained-size-candidate-selection.md)) —
  Mechanism B should follow the same shape: resolve severity/ranking first (as
  `GCRootAnalysisProjection.Build` already does), then run the stack-frame
  correlation only for the top-`N` `Stack`-kind findings that make it into the
  report.

Concretely: a new `RootSetCache` method, e.g. `TryResolveStackFrameOwner(ClrHeap
heap, ulong rootAddr, out string ownerType, out string methodName)`, that performs
the per-thread walk on first call and caches the resulting `RootAddr`-keyed map for
the lifetime of the analysis run (same caching shape as
`GetStaticFieldsByRootAddress`), but is only invoked by `GCRootAnalysisProjection`
for the `Stack`-kind findings that survive severity ranking, not for every `Stack`
root in the dump.

Disk persistence for Mechanism B is explicitly **out of scope for v1** — revisit only
if profiling shows the live top-N walk itself is a bottleneck (unlikely, given it's
bounded by report size, not root count or thread count).

> ✅ **Done**, exactly as scoped above. `RootSetCache.TryResolveStackFrameOwner(ClrHeap
> heap, ulong rootAddr, out string ownerType, out string methodName)` lazily builds
> and caches a `RootAddr → (OwnerType, MethodName)` map on first call, walking
> `heap.Runtime.Threads` once via `ClrThread.EnumerateStackTrace(includeContext: false,
> maxFrames: 256)` (the bounded overload — `EnumerateStackTrace`'s own doc warns the
> unbounded one "may loop infinitely in the case of stack corruption") and
> `ClrThread.EnumerateStackRoots()`. The pure range-correlation logic (binary search
> for the frame whose `StackPointer` range contains a slot address) was extracted into
> a new `StackFrameRangeCorrelator` — decoupled from `ClrStackFrame`/`ClrThread` so it
> could be unit-tested directly (`StackFrameRangeCorrelatorTests`, 12 cases covering
> mid-range/boundary/outermost/below-innermost/empty/duplicate-`StackPointer` and the
> ascending-order guard) without a live heap.
>
> Wired into `GCRootAnalyzer.Analyze`, not `GCRootAnalysisProjection.Build` — severity
> ranking and the `TopSeverityLimit` slice already happen in the analyzer, so the
> top-N loop was added there, right after `topCount` is computed and before the
> `topFindings` slice, mutating `findings[i]` in place via `f with { FieldDescription =
> ... }` (formatted as `"in {OwnerType}.{MethodName}()"`, distinct from Mechanism A's
> `"{OwnerType}.{FieldName}"` so the report doesn't imply a field exists where there's
> only a frame) so both the truncated and untruncated `topFindings` branches see the
> attributed value. `IHeapAnalysisCache`/`HeapAnalysisCache` and the
> `RunAnalyzersPipelineStageTests` fake were updated to add the new method.
>
> The `ThreadStackScanDispatcher` (shared `EnumerateStackTrace()` pass already used by
> `ThreadAnalyzer`/`LockGraphAnalyzer`/`ThreadStackClusterAnalyzer`/`CrashAnalyzer` to
> avoid duplicate stack walks) was deliberately **not** reused here: it runs
> unconditionally over every thread whenever it has ≥1 participant, which is most
> runs — wiring `GCRootAnalyzer` in as a participant would make Mechanism B pay that
> cost on every analysis regardless of whether any `Stack`-kind root ends up in the
> top-N, contradicting the lazy/on-demand design goal above. It also only supplies
> frames, not `EnumerateStackRoots()` — correlating roots to frames would still need
> a second per-thread call the dispatcher doesn't provide.

---

## What neither mechanism fixes

- **Exact `Stack` local-variable names** — Mechanism B gets you the owning method,
  not the variable name. That needs PDB-based variable-slot mapping, which ClrMD
  doesn't expose. Stays a documented WinDbg-only gap (`!gcroot` does this via DAC
  internals DumpDetective doesn't have access to).
- **`StrongHandle`/`PinnedHandle`/`RefCountedHandle`/`AsyncPinnedHandle`/
  `SizedRefHandle`/`FinalizerQueue` identity** — structurally anonymous, no owner
  identity exists to recover for these kinds, from any tool.

---

## Implementation order

**Mechanism A:**

1. ✅ **Done.** `RootSetCache`: rekey the live static-field map by
   `field.GetAddress(domain)`, rename `GetStaticFieldsByTargetAddress` →
   `GetStaticFieldsByRootAddress`. Add `RootSetCache.GetOrBuildRootTriples` (and the
   `IHeapAnalysisCache`/`HeapAnalysisCache` mirrors) so callers needing `RootAddr` no
   longer have to consume the `RootRecord`-shaped `GetOrBuildRoots` directly.
2. ✅ **Done.** `StaticRootLeakDetector.AnalyzeStaticRoots`: migrate to
   `GetOrBuildRootTriples`, look up the field map by real `RootAddr`.
3. ✅ **Done.** Update test doubles (`RunAnalyzersPipelineStageTests` fake
   `IHeapAnalysisCache`) for the renamed method and new `GetOrBuildRootTriples`
   member.
4. ✅ **Done.** `RootIndexWriter`: compute the `RootAddr`-keyed static-field map
   during the Roots section write (via shared `StaticFieldResolver`, bounded to
   currently-enumerated static roots), append as trailer, bump internal header
   version to 2.
5. ✅ **Done.** `RootIndexReader.ReadRootFieldNames`: parses the v2 trailer into
   `Dictionary<ulong, (string OwnerType, string FieldName, int AppDomainId)>`; v1
   files (or any header-version mismatch) yield an empty dict, not an error.
6. ✅ **Done.** `RootSetCache.GetStaticFieldsByRootAddress`: disk-index read path
   ahead of the existing live-scan fallback.
7. ✅ **Done.** `GCRootAnalysisProjection.Build`: populates `FieldDescription` for
   static-kind roots via `cache.GetStaticFieldsByRootAddress(heap)`.
8. ✅ **Done** (partial — `RootIndexReaderTests` covers the trailer round-trip, v1
   rejection, and empty-file case). `RootCacheTests`/
   `GCRootAnalyzerDiscrepancyTests`/`RootSetCacheDiscrepancyTests` real-dump
   discrepancy coverage not yet extended for the disk-backed field map — those are
   gated behind `DD_RUN_DISCREPANCY_TESTS=1` and run one-at-a-time per
   [CLAUDE.md](../../CLAUDE.md), not exercised as part of this change.
9. ✅ **Done.** `docs/binary-format.md` § Roots section updated with the v2 trailer
   layout.

**Mechanism B:**

10. ✅ **Done.** `RootSetCache.TryResolveStackFrameOwner`, backed by a lazily-built,
    per-analysis-run cached `RootAddr → (OwnerType, MethodName)` map from the
    per-thread `EnumerateStackTrace`/`EnumerateStackRoots` correlation described
    above. `IHeapAnalysisCache`/`HeapAnalysisCache` mirrors added.
11. ✅ **Done** (wired in `GCRootAnalyzer.Analyze`, not `GCRootAnalysisProjection.Build`
    — see the ✅ note above for why). Populates `FieldDescription` as
    `$"in {OwnerType}.{MethodName}()"` for the top-`N` `Stack`-kind findings after
    severity ranking.
12. ✅ **Done** — but scoped narrower than originally planned: the pure
    range-correlation logic was extracted into `StackFrameRangeCorrelator` and covered
    by 12 unit tests (mid-range/boundary/outermost/below-innermost/empty/duplicate-
    `StackPointer` cases), since `ClrThread`/`ClrStackFrame` aren't mockable and no
    fake-heap infrastructure exists in this test suite to construct a multi-frame,
    multi-thread scenario end-to-end. `RootSetCache.TryResolveStackFrameOwner` and
    `BuildStackFrameOwnerMap` themselves are only exercised indirectly (build-time
    type check + the existing `GCRootAnalyzer` real-dump discrepancy test path) —
    a true end-to-end frame-attribution assertion against a real multi-thread dump
    is still an open gap, not covered by this change.

---

## Open questions / risks

**Mechanism A:**

- Confirm `ClrStaticField.GetAddress(ClrAppDomain)` returns a stable, dereferenceable
  address for all static field storage kinds ClrMD 4 supports (regular statics,
  `[ThreadStatic]`, generic-type statics) before relying on it as the join key —
  spot-check against a real dump during implementation, not just the XML doc
  signature.
- The Phase-1 static-field walk (`AppDomains → Modules →
  EnumerateTypeDefToMethodTableMap → StaticFields`) is the same cost whether done at
  Phase-1 build time or Phase-2 live-fallback time; moving it to Phase-1 doesn't make
  it cheaper, just amortizes it across every subsequent analysis run of the same
  cached dump instead of re-running it every time. Confirm this doesn't materially
  extend Phase-1 build time on large dumps with many types (worth a benchmark check
  against the GC-root timing numbers in `docs/cache/cache-architecture.md` § 8).

**Mechanism B:**

- ✅ **Mitigated, not eliminated.** The stack-growth-direction assumption (`SP`
  increasing outward toward caller) is guarded by
  `StackFrameRangeCorrelator.IsSortedAscending` — a thread whose frames aren't in
  ascending `StackPointer` order (wrong architecture assumption, or genuine stack
  corruption) is skipped entirely for correlation rather than silently
  misattributing. This converts "silently wrong" into "silently absent" (no
  `FieldDescription` for that thread's roots), which is the safe failure mode, but
  it hasn't been validated against a real non-x64 dump — the assumption is enforced
  defensively, not confirmed correct for every architecture/OS this project
  analyzes.
- ✅ **Handled.** `ClrStackFrame.Method` being `null` for non-managed/internal frames
  (native transitions, JIT helper frames) falls through cleanly: `FindOwningMethod`'s
  caller checks `method?.Type?.Name is string ... && method.Name is string ...`
  before inserting into the map, so such a root simply gets no attribution rather
  than a null-reference failure.
- ✅ **Handled.** `EnumerateStackTrace(includeContext: false, maxFrames:
  MaxFramesPerThread)` uses the explicit bounded overload (256 frames) rather than
  the unbounded one — `EnumerateStackTrace`'s own doc warns the unbounded form "may
  loop infinitely in the case of stack corruption," so this was a correctness
  requirement, not just a performance one.
- **Still open**: no end-to-end test exercises `RootSetCache.BuildStackFrameOwnerMap`
  against a real multi-thread, multi-frame stack (see implementation-order item 12's
  note) — only the pure range-correlation math is unit-tested. A discrepancy-style
  test against a real dump with a known call stack would close this gap.

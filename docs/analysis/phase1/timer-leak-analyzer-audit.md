# TimerLeakAnalyzer — Phase 1 Audit

**Analyzer:** `TimerLeakAnalyzer`
**File:** `src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs`
**Audit date:** 2026-08-03
**Protocol:** [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)



---

## Implementation Notes — P0 Double-Counting Fix

### Changes Made (2026-08-06)

**Model Enhancement:**
- Added `LogicalTimerCount` property to `TimerLeakDomainResult` record
- Semantics: equals `TimerQueueTimerCount` (each logical timer maps to exactly one `TimerQueueTimer`)
- Maintains backward compatibility: `TotalTimers` remains as raw object count for investigative use

**Analyzer Update:**
- Updated `TimerLeakAnalyzer.Analyze()` to pass `LogicalTimerCount: timerQueueTimerCount` when constructing result

**Finding Generator Fix:**
- Changed severity thresholds from `r.TotalTimers` to `r.LogicalTimerCount` (lines 20–22)
- Updated finding title to clarify "logical timers" vs raw object count
- Updated evidence string to show both logical count (in title) and raw breakdown (in evidence)
- Changed `MetricValue` to use `LogicalTimerCount`

**Section Builder Enhancement:**
- Added `["logical_timers"]` as the first key metric (visible in reports)
- Kept `["total_timer_objects"]` for detailed breakdown

**Trend Tracking:**
- Added `"timer.logical"` metric to `ExtractMetrics()` for snapshot analysis
- Added `"timer.logical"` delta to `Compare()` for trend comparison

### Result

Severity now reflects logical timer count (deduplicated), eliminating false-positive escalations from `TimerHolder` + `TimerQueueTimer` wrapper inflation. A system with 130 `TimerQueueTimer` + 130 `TimerHolder` objects now correctly reports 130 logical timers (≈ `Warning` at default thresholds) instead of 260 (≈ `Critical`).

### P0 PeriodicTimer Coverage Addition (2026-08-06)

**Changes Made:**

**Analyzer Enhancement:**
- Added `PeriodicTimer` value to `TimerObjectCategory` enum
- Added classification check for `System.Threading.PeriodicTimer` in `ClassifyType()` method (exact-match fast path)

**Model Update:**
- Added `PeriodicTimerCount` field to `TimerLeakDomainResult` record
- Tracked in analyzer's main loop, contributing to `TotalTimers` (raw object count)

**Reporting Updates:**
- Updated finding generator evidence string to include `PeriodicTimer` count alongside other categories
- Added `periodic_timer` metric to section builder key metrics
- Added `timer.periodic` metric to trend comparer for snapshot and delta tracking

**Test Updates:**
- Extended `TimerResult` helper with `periodic` parameter (defaults to 0)
- Added test coverage for PeriodicTimer instances in type summary

### Result

Analyzers now detect `System.Threading.PeriodicTimer` instances (shipped with .NET 6.0+), eliminating false negatives for modern async timer workloads that use the idiomatic `PeriodicTimer` API instead of legacy `System.Threading.Timer` or `System.Timers.Timer`.

### P1-1 Render Evidence RootPath & P2-2 Fix LINQ (2026-08-06)

**Changes Made:**

**Section Builder Enhancement:**
- Removed `using System.Linq;` import
- Replaced LINQ `.Select()` chain with manual loop for table row construction (line 70–79)
- Added evidence rendering section that displays root path per timer type with truncation warning indicator (⚠)
- Integrated truncated search warning into type name column for at-a-glance visibility
- Added "Retention evidence" section showing per-type root path chains with proper indentation

**Implementation Details:**
- Root paths from `Evidence.SampleRootPath` are now rendered in a dedicated section below the type summary table
- Truncated searches are flagged with ⚠ symbol next to type name for visual scan
- Added separate warning banner when any type has truncated search (reduced confidence signal)
- Manual loop construction removes hot-path LINQ violation while maintaining readability

### Result

Evidence root paths are now visible to engineers in the report, making retention chains immediately actionable without requiring debugger inspection. Truncated searches are explicitly flagged, allowing engineers to assess confidence in findings. All LINQ usage in hot paths has been eliminated.

### P1-2 Implement ITypedResourceInstanceSampler (2026-08-06)

**Changes Made:**

**New Model and Interface:**
- Added `TimerStateSnapshot` record with Address, Generation, PeriodMs, CallbackOwnerType fields
- Implemented `ITypedResourceInstanceSampler<TimerStateSnapshot>` on TimerLeakAnalyzer
- Added `MaxStateSamplesPerType` (100) and `TopSampleCap` (20) properties

**Field Reading Logic:**
- Implemented `TrySample()` method to extract _period field from TimerQueueTimer
- Implemented `TryReadCallbackOwner()` to traverse _timerCallback → _target → Type.Name
- Added error handling for corrupt/invalid objects, field not found, type casting failures
- Graceful fallback: returns -1 for period or null for callback owner if reading fails

**Integration:**
- Integrated sampler into `PopulateEvidence` to capture samples during evidence population
- Updated `TimerObjectTypeSummary` with optional `Samples` field (IReadOnlyList<TimerStateSnapshot>)
- Samples captured per type alongside root path evidence

### Result

Timer state fields (_period, callback owner type) are now captured per type instance during evidence collection. Foundation is set for displaying actionable timer interval categories (recurring, one-shot, suspended) and callback ownership attribution in reports (next: section builder update).

### P1-3 Pass CancellationToken through PopulateEvidence (2026-08-06)

**Changes Made:**

**Analyzer Robustness:**
- Added `CancellationToken` parameter to `PopulateEvidence` method signature
- Added cancellation check at method entry point (before root retrieval)
- Added cancellation check in for loop (before each root path search)

**Implementation Details:**
- Token is passed through from `Analyze()` method which already receives it
- Per-type root path searches now respect cancellation (critical since search is bounded but can run seconds on large heaps)
- Loop check allows early exit when operation is cancelled on large candidate sets

### Result

Root path finding operations on large dumps can now be cancelled instead of blocking indefinitely. Provides graceful shutdown path for long-running evidence collection phase, improving robustness on 10GB+ dumps.

### P2-3 Narrow OtherTimerCategory (2026-08-06)

**Changes Made:**

**Exclusion Logic:**
- Added `ClrInternalTimerTypes` array containing known CLR-internal timer types
- Added `IsKnownClrInternalTimerType()` method with O(n) lookup (n=2 known types)
- Updated `ClassifyType()` to exclude known CLR-internal types before OtherTimer pattern match

**Implementation Details:**
- Excludes: System.Threading.TimerQueue, System.Threading.TimerThread
- Prevents false positive matches for internal implementation detail types
- Explicit types (ThreadingTimer, TimersTimer, TimerQueueTimer, TimerHolder, PeriodicTimer) still handled first

### Result

OtherTimerCategory no longer captures CLR-internal infrastructure types, reducing false positive contributions to OtherTimerCount. The catch-all pattern match now correctly targets only third-party or user-defined timer types.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current role

`TimerLeakAnalyzer` scans the managed heap for framework timer objects that accumulate when callers
fail to dispose them. It produces per-type counts, byte totals, and a root-path evidence sample for
each type. The finding generator raises `Warning` / `Critical` at total-count thresholds and a
separate `Info` / `Warning` for timer-queue pressure based on `TimerHolder + TimerQueueTimer`.

The analyzer's cohesion is good: it stays narrowly focused on timer object accumulation and does not
reach into unrelated domains.

### Coverage gaps

| Missing type | Significance |
|---|---|
| `System.Threading.PeriodicTimer` | Ships with .NET 6+; the idiomatic replacement for `Timer` in async loops — entirely absent from the candidate list |
| `System.Threading.ITimer` (.NET 8) | The new interface type; any host-injectable timer that leaks goes undetected |
| Third-party schedulers (`Quartz.IScheduler`, `Hangfire.BackgroundJobServer`) | Common in enterprise dumps; not in scope, but noted as an adjacent opportunity |
| Timer callback delegate | Which method is being called is not extracted, making "who owns this timer?" unanswerable from the report alone |
| Timer state (`_period`, `_dueTime`, `_enabled`) | Cannot distinguish a recurring-every-100ms timer from a one-shot already-fired timer |

### Double-counting structural issue

`System.Timers.Timer` is a thin wrapper around `System.Threading.Timer`. Both coexist on the
managed heap. A single `System.Timers.Timer` instance contributes to both `TimersTimerCount` and
`ThreadingTimerCount`. Similarly, `TimerHolder` is the CLR's internal wrapper for
`TimerQueueTimer`: one logical timer produces two heap objects, each counted separately. The current
`TotalTimers = threading + timers + queue + holder + other` therefore over-counts logical timers by
up to 2× and inflates severity.

### Expansion opportunities

- Read `_period` / `_dueTime` to classify timers as *recurring*, *one-shot pending*, or
  *already-fired / infinite-delay suspended* (see Area 4 for detail).
- Extract callback delegate method name/type to attribute ownership.
- Cover `PeriodicTimer` (Area 3 has implementation notes).
- Feed `TotalTimers` (de-duplicated) into the `LeakCandidateAnalyzer` ranking engine as noted in
  the Phase 0 boundary review — this action item remains open.

### Architectural observations

The `IAnalyzer` interface exposes `Tags` and `Order` with default implementations. `TimerLeakAnalyzer`
uses neither — `Tags` is empty and `Order` is 0. This is consistent with `HttpObjectAnalyzer` (same
quartet), so not a defect, but modules relying on tag-based filtering lose the granularity.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Per-type breakdown in `ByType` is sorted by count descending — highest-volume type is immediately
  visible.
- Dual findings (aggregate count + queue pressure) target distinct failure modes.
- Severity escalation from `Warning` → `Critical` at 250 is deterministic.
- `EvidenceConfidence` is attached to the aggregate finding (via top-type evidence).

### Weaknesses

**Evidence is populated but not rendered.**
`TimerObjectTypeSummary.Evidence` carries a root path and a `searchTruncated` flag.
`TimerLeakSectionBuilder` never reads `Evidence`; the section table shows only `TypeName`, `Count`,
and `Heap Size`. An engineer looking at the report cannot see the retention chain without running the
analyzer in a debugger.

**Queue-pressure heuristic threshold is hardcoded to ≥ 50.**
The threshold does not scale with heap size, total managed thread count, or timer fire rate. A
system with 10 000 objects and only 55 holders is below `Warning` but likely has a real problem;
a test harness with 300 synthetic timers hits `Critical` immediately.

**No timer category is shown in the finding title.**
"1 234 timer-related objects on managed heap" does not indicate which framework type dominates.
An engineer must open the section table to correlate.

**`searchTruncated` is silently dropped.**
When root-path search is truncated (candidate-set limit hit), the section and findings report nothing;
confidence is silently degraded.

**`OtherTimerCount` contributes to severity but the finding evidence string omits it.**
The evidence string lists four named categories but uses the label `Other=N` only; when the spike is
in a third-party timer matched via the prefix/token heuristic, the type name is invisible.

**Section builder imports `System.Linq` for a single `.Select` on table rows.**
`SectionBuilderBase` helper methods exist for building compact tables; the inline LINQ chain (line 53)
conflicts with the project's LINQ-in-hot-paths prohibition and should use a manual loop.

### Missing diagnostics

- Root-path chain per type in the section output.
- "Callback owner" (type name of the timer callback delegate target).
- Timer interval / due-time for sampled instances.
- Trend delta in the section (current count vs prior snapshot, if available).
- `searchTruncated` warning banner in the section.

---

## Audit Area 3 — ClrMD & Platform Utilization

### What is used well

- `TypedResourceScanDriver.DiscoverCandidates` routes through the shared cache/index, avoiding a
  second full heap scan.
- `cache.GetOrBuildValidRoots` is used correctly — roots are not re-enumerated per type.
- `ReferenceGraph` and `RootPathFinder` are instantiated once in `PopulateEvidence` and reused
  across all type iterations.
- `cache.GetSampleInstanceAddress` provides a single representative address per type without
  iterating all instances.

### Missing ClrMD utilization

**`PeriodicTimer` is absent from `ClassifyType`.**
`System.Threading.PeriodicTimer` has been in .NET since 6.0. Adding it requires one `Equals` branch
and a new `PeriodicTimer` category enum value.

**Timer state fields are never read.**
ClrMD field access:
```csharp
// System.Threading.TimerQueueTimer fields (internal CLR type)
ClrInstanceField? periodField = type.GetFieldByName("_period");
ClrInstanceField? dueTimeField = type.GetFieldByName("_dueTime");
// System.Timers.Timer
ClrInstanceField? enabledField = type.GetFieldByName("enabled");
```
These are not secret — they are stable runtime implementation details used by WinDbg SOS and
PerfView for years. Reading them unlocks active vs. inactive classification without a second heap
pass.

**Callback delegate is never inspected.**
`TimerQueueTimer._timerCallback` (a `TimerCallback` delegate) holds a `_target` field pointing to
the subscriber object. Reading `_target.Type.Name` produces the owning type name, which is the most
actionable output possible for a leaked timer.

**`ITypedResourceInstanceSampler<T>` is not implemented.**
`DbConnectionAnalyzer` and `WcfChannelAnalyzer` implement `ITypedResourceInstanceSampler` to read
per-instance state within the shared scan pass, avoiding a second traversal. `TimerLeakAnalyzer`
does not implement this interface; its `PopulateEvidence` re-traverses objects post-scan using only
one sample address per type (no state fields). Implementing the sampler interface would allow the
shared pass to capture `_period` and callback target for up to N samples per type in O(1) per
object.

**`cancellationToken` is not passed to `PopulateEvidence`.**
The method signature is `private static void PopulateEvidence(ClrHeap, IHeapAnalysisCache?,
List<TimerObjectTypeSummary>)`. Root-path searches on a large heap can run for several seconds.
No cancellation path exists; the only escape is the internal `MaxCandidateNodes` limit.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-value opportunities (priority ordered)

**1. Per-instance timer state sampling (High value, Low difficulty)**

Read `_period` and `_dueTime` from `TimerQueueTimer` instances during or after the scan. Categorize
each sample as:

| Class | Condition |
|---|---|
| Recurring | `_period != Timeout.Infinite && _period != 0` |
| One-shot pending | `_period == 0 || _period == Timeout.Infinite` and `_dueTime` in future |
| Suspended / infinite | `_dueTime == Timeout.Infinite` |

Reporting "450 recurring timers firing every ~100 ms" is far more actionable than "450 timer
objects".

**2. Callback owner attribution (High value, Medium difficulty)**

For each `TimerQueueTimer` sample, walk:
```
_timerCallback → _target → Type.Name
```
Group the resulting type names and include the top-3 callback owners in the finding evidence. This
immediately answers "which component is not disposing its timers?"

**3. `PeriodicTimer` coverage (High value, Very Low difficulty)**

Add `System.Threading.PeriodicTimer` to `ClassifyType`. A single enum member and one `Equals`
branch. In .NET 6+ async workloads `PeriodicTimer` is the canonical timer; missing it produces a
false negative for a whole class of modern applications.

**4. De-duplicate logical timer count (Medium value, Low difficulty)**

Logical timer count = `TimerQueueTimerCount` (each logical `System.Threading.Timer` maps to exactly
one `TimerQueueTimer`). Using `TimerQueueTimerCount` as the authoritative metric eliminates the 2×
inflation from `TimerHolder` and the `System.Timers.Timer` → `System.Threading.Timer` wrapping.
Expose both: raw object count (current) and logical timer count (new).

**5. Enabled/disabled breakdown for `System.Timers.Timer` (Medium value, Low difficulty)**

`System.Timers.Timer.enabled` is a `bool` field. Distinguishing enabled vs. disabled instances
separates active leaks from stopped-but-not-disposed timers, reducing false urgency.

**6. Timer interval histogram (Low value, Medium difficulty)**

Group `TimerQueueTimer` instances by `_period` bucket (< 100 ms, 100–1000 ms, > 1 s, infinite).
High-frequency-interval groups correlate with CPU burn from timer flood; this is a separate problem
category from pure object accumulation.

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap scan

`TypedResourceScanDriver.DiscoverCandidates` uses the cache/index path; it does not re-scan the
heap when the index is available. This is correct.

### `PopulateEvidence` cost

The method constructs `ReferenceGraph` and `RootPathFinder` once and calls
`TryFindAnyRootPath` once per distinct timer type. In practice there are 4–6 distinct types. The
per-call cost is bounded by `MaxCandidateNodes = 5 000`, keeping it predictable even on large heaps.
No scalability concern in the current design.

### Missing cancellation

`PopulateEvidence` has no `CancellationToken` parameter. On a 20 GB dump with a pathological
reference graph, the candidate-set builder could saturate the budget and still loop over all roots
(Phase 3 of `RootPathFinder`). The effect is a delay of several seconds with no escape hatch short
of process kill. This is a correctness/robustness gap, not a hot-path concern.

### Allocation

- `new List<TimerObjectTypeSummary>(candidates.Count)` is appropriately sized.
- `byType.Sort` is in-place — no additional allocation.
- No `StringBuilder`/string interning issues in the hot path.

### Expected behavior at scale

| Dump size | Heap scan | Evidence |
|---|---|---|
| 1 GB | Fast — cache-backed, MethodTable filter | < 1 s for 4–6 type root searches |
| 10 GB | Fast — same path | < 5 s; well within acceptable range |
| 100 GB | Fast — same path | Root search still bounded by `MaxCandidateNodes`; no regression |

No scalability bottleneck identified. The analyzer does not enumerate all timer instances for
evidence — only one sample per type.

---

## Audit Area 6 — Correctness & Confidence

### Double-counting (P0 correctness risk)

As described in Area 1:

- `System.Timers.Timer` wraps `System.Threading.Timer`. Both appear on the heap simultaneously.
  `TotalTimers` counts both, inflating the number by up to `TimersTimerCount`.
- `TimerHolder` is a CLR internal wrapper for `TimerQueueTimer`. Both appear simultaneously.
  `TotalTimers` counts both, inflating by up to `min(TimerHolderCount, TimerQueueTimerCount)`.

**Observed consequence:** severity thresholds (100/250) are applied to an inflated metric. A
system with 130 `TimerQueueTimer` objects also has ~130 `TimerHolder` objects → `TotalTimers ≈ 260`
→ triggers `Critical` when the true logical timer count is 130 (which might warrant only `Warning`).

**Fix:** use `TimerQueueTimerCount` as the authoritative logical count for severity evaluation;
expose raw object count separately for investigative use.

### `OtherTimerCategory` false positives

The catch-all matches any type whose name contains "Timer" under `System.Threading.` or
`System.Timers.` namespaces. CLR internal types (e.g. `System.Threading.TimerQueue`) that are not
user-created timer objects could be captured and inflate `OtherTimerCount`. These types should be
explicitly excluded or the category narrowed.

### Confidence score gap

`EvidenceConfidence.Compute(topEvidence)` is used for the aggregate timer finding. If the
top-type's root search was truncated (`searchTruncated = true`), confidence is reduced, but there is
no separate finding or note to the engineer about why confidence is degraded.

### Missing `IsValid` / null guard in evidence population

`PopulateEvidence` calls `cache.GetSampleInstanceAddress(summary.TypeName)` and immediately passes
the result to `TryFindAnyRootPath`. If the cached address refers to an object that was already
collected (corrupt or partial dump edge case), the root search operates on an invalid address. The
`RootPathFinder` likely handles this gracefully via `ClrObject.IsValid` checks internally, but the
path is not explicitly guarded here.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!dumpheap -type Timer -stat` provides instance count and bytes by type. It does not classify
severity, does not detect double-counting, and requires manual field inspection to determine
intervals or callbacks. DumpDetective's automatic classification and root-path evidence are stronger.
Gap: WinDbg `!do <addr>` on a `TimerQueueTimer` shows `_period`, `_dueTime`, and `_timerCallback`
immediately — DumpDetective does not expose this.

### PerfView

No dedicated timer-leak view. Timer accumulation appears indirectly in GC heap snapshots under type
diffing. DumpDetective is ahead.

### Visual Studio Memory Usage / dotMemory

Both tools show type instance counts including timer types but do not produce actionable severity
findings or root retention paths. DumpDetective's `InsightFinding` with `Recommendation` text is
ahead for incident response.

### Competitive gap

The one capability that WinDbg + SOS provides which DumpDetective does not:
**callback method identification and timer interval**. A developer using WinDbg can in two commands
determine which component owns the leaked timers and how frequently they fire. DumpDetective requires
the engineer to cross-reference the heap address with source code manually. This is the highest-ROI
gap to close.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

The analyzer detects the right objects, uses shared infrastructure correctly, and produces
deterministic findings. The implementation is clean and maintainable. However it has a structural
double-counting defect that mis-classifies severity, does not extract available timer state fields
that would make findings actionable, does not cover `PeriodicTimer`, and populates evidence that
is never shown to the engineer.

**Production readiness:** Conditionally. Findings are directionally correct but severity is
unreliable due to double-counting. The analyzer is not harmful; it will not produce false negatives
for genuine leaks. The false-positive-severity risk is real.

**Major strengths:**
- Clean type classification with exact-match fast paths
- Correct use of `TypedResourceScanDriver`, cache, and `RootPathFinder`
- Dual-finding design separates aggregate accumulation from queue pressure
- Trend comparer covers all key metrics

**Major weaknesses:**
- `TotalTimers` double-counts implementation detail types; severity thresholds applied to inflated metric
- `PeriodicTimer` (.NET 6+) entirely absent
- Evidence (root path) is populated but never rendered in the section
- Timer state fields (`_period`, `_dueTime`, callback target) not read despite being available

---

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| **P0** | Fix double-counting: use `TimerQueueTimerCount` as the logical-timer count for severity thresholds; expose raw object count separately | High — current severity is unreliable | Low | High | Improvement | ✅ COMPLETE |
| **P0** | Add `System.Threading.PeriodicTimer` to `ClassifyType` | High — false negative for all .NET 6+ timer leaks | Very Low | High | Improvement | ✅ COMPLETE |
| **P1** | Render `Evidence.RootPath` per type in `TimerLeakSectionBuilder` | High — evidence exists but is invisible to engineers | Low | High | Improvement | ✅ COMPLETE |
| **P1** | Implement `ITypedResourceInstanceSampler` to read `_period` and callback `_target.Type.Name` per sample in the shared scan pass | High — makes findings actionable (who owns it, how often fires) | Medium | High | Improvement | ✅ COMPLETE |
| **P1** | Pass `CancellationToken` through `PopulateEvidence` | Medium — robustness on large dumps | Low | High | Improvement | ✅ COMPLETE |
| **P2** | Surface `searchTruncated` as a section warning banner and factor into finding confidence text | Medium — engineers need to know when evidence is incomplete | Low | High | Improvement | ✅ COMPLETE |
| **P2** | Fix `System.Linq` import in `TimerLeakSectionBuilder` (replace with manual loop) | Low — code style / correctness for hot paths | Very Low | High | Improvement | ✅ COMPLETE |
| **P2** | Narrow `OtherTimerCategory` to exclude known CLR-internal non-user types (e.g. `TimerQueue`) | Medium — avoids false positive contributions to OtherTimerCount | Low | Medium | Improvement | ✅ COMPLETE |
| **P3** | Add timer interval histogram (group `_period` into < 100 ms / 100 ms–1 s / > 1 s / infinite) | Medium — separates accumulation leak from timer flood CPU issue | Medium | Medium | Improvement | ✅ COMPLETE |
| **P3** | Feed de-duplicated logical timer count into `LeakCandidateAnalyzer` ranking (open Phase 0 action item) | Medium — cross-analyzer correlation | Medium | High | Evolution | ✅ COMPLETE |

### Final Verdict

**Status (2026-08-28): P0+P1+P2+P3 all COMPLETE — audit closed.**

1. **Production-ready?** Yes. The P0 count-deduplication fix landed first, so severity has been
   reliable (based on `LogicalTimerCount`, not the double-counted `TotalTimers`) since early in this
   audit's lifecycle; every subsequent P1/P2/P3 item has since shipped as well.

2. **Highest-impact improvements delivered:** deduplicated `TotalTimers` to a logical count,
   `PeriodicTimer` (.NET 6+) coverage, root-path evidence rendered in the section builder, per-sample
   `_period`/callback-target reads, and a `_period` interval histogram (< 100 ms / 100 ms–1 s / > 1 s /
   infinite) via a second exact heap pass over every `TimerQueueTimer` instance.

3. **Platform evolution — closed:** the `LeakCandidateAnalyzer` integration (this analyzer's own
   final open item) now correlates `LeakCandidateAnalyzer`'s per-type candidate rows with
   `TimerLeakDomainResult.LogicalTimerCount` via `completedRunResults`, adding a `LeakClass.TimerLeak`
   classification and score boost for named timer wrapper types once the count crosses the same
   100/250 warning/critical thresholds `TimerLeakFindingGenerator` uses. No pending items remain.

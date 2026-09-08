# RootPathFinder — Redesign

> Shared "why is this object alive" traversal. Consumed by `EventLeakAnalyzer`,
> `DominatorAnalyzer`, `StaticRootLeakDetector`, `TimerLeakAnalyzer` and
> `ReferenceChainAnalyzer`. Measured via EventLeak — see
> [event-leak-analyzer.md §3.1](event-leak-analyzer.md).

---

## 1. The problem

Measured on `Crash_IIS_BALTSTPRD` (3.35 GB, 1411 valid roots), via
`EventLeakAnalyzer.PopulateEvidence`:

- 3321 targets, **10.3ms each**, **229 paths found (6.9%)**, 0 reported truncations.
- 34.28s total — 36% of the analyzer's entire runtime — for a 93% failure rate.

Three defects, all structural:

**1. The search expands the wrong way.** Finding what *retains* an object requires its
**incoming** references. `CandidateSetBuilder` expands **forward** from the target. The
code comment concedes it: *"forward refs of target are not useful for reverse; instead we
do a second BFS from target outward… useful to collect the neighbourhood."* The target
frontier explores what the target points *at* — the one direction that cannot lead to a
root. A path is found only when some root's shallow forward shell happens to reach the
target, which is why the ~7% that succeed are objects already near a root (statics,
directly-referenced singletons) — i.e. exactly the objects that least need explaining.

**2. The node budget is spent on seeding.** All 1411 roots are inserted into the candidate
set before expansion begins, consuming 28% of `MaxCandidateNodes = 5_000`. ~3589 nodes of
expansion remain, split across two frontiers, over a heap with millions of objects.

**3. Failure costs full price.** The reverse index is built over the entire 5000-node
candidate set — ~5000 × (`heap.GetObject` + reference enumeration) — *before* any path
attempt. Across 3321 targets that is ~16M ClrMD object materialisations at ~2µs each.
That is where the 34s lives: work done in full, discarded 93% of the time.

**And the failure is invisible.** `searchTruncated` is set only if the final BFS reports
`limited`. That BFS runs inside an already-exhausted candidate set and terminates by
running out of component, never by hitting its cap. The phase that actually exhausts its
budget — candidate construction — has no truncation signal. Tuning decisions based on
this flag are unsound.

**Ground truth for calibration:** every live object is by definition reachable from a GC
root. A correct finder with an adequate budget should approach a 100% hit rate. 6.9% is
not a tuning shortfall; it is the wrong algorithm.

---

## 2. The binding constraint

**ClrMD 4.0.732401 exposes no reverse-reference or path-to-root API.** Verified against
the shipped API surface: `ClrHeap.EnumerateRoots`, `ClrHeap.EnumerateObjectReferences`,
`ClrObject.EnumerateReferenceAddresses` — all forward-only. The `GCRoot` helper from
ClrMD 2.x is gone.

Backward traversal must therefore be built on our own edge store. Since parent lookup for
even a single object requires knowing every object that references it, there is no
cheap per-query answer: **some pass over the object graph is unavoidable.** The design
question is only whether that pass is paid per query (today, badly) or once per dump and
shared.

---

## 3. Design — predecessor column

Pay one graph traversal per dump, persist the result, answer every query in O(depth).

### 3.1 Build

A BFS forward from the roots over the live object graph, recording each object's
**predecessor on first visit**:

```
visited  : bitmap over object ordinals          (1 bit  × N  →  ~11 MB at 87M objects)
parent   : ordinal column, memory-mapped        (4 bytes × N  →  ~350 MB at 87M objects)
queue    : ordinals, chunked                    (transient)

for each root r:                       parent[ord(r)] = ROOT_SENTINEL; enqueue r
while queue not empty:
    p = dequeue
    for each child c in EnumerateReferenceAddresses(p):
        i = ord(c)                     // binary search into the sorted ObjectAddresses column
        if visited[i]: continue
        visited[i] = true
        parent[i]  = ord(p)
        enqueue c
```

Why BFS from roots rather than recording "first referrer seen" during a linear heap sweep:
a linear sweep produces parent links that can form **cycles** (A↔B mutually referencing,
the component actually rooted via C), so a chain walk can loop forever without reaching a
root. BFS from roots yields a spanning forest — every chain terminates at a root by
construction, acyclic, no cycle detection needed at query time. It also yields the
*shortest* path in edges, which is the most explanatory one.

Ordinals rather than addresses: `ObjectAddresses` is already a sorted column, so
`ord(address)` is a binary search and `parent` becomes a 4-byte column (87M < 2³¹).
Halves the footprint versus storing addresses.

### 3.2 Query

```csharp
bool TryFindRootPath(ulong target, out string rootKind, out List<ulong> path)
```

Walk `parent[ord(target)]` upward to `ROOT_SENTINEL`, emitting addresses. O(depth) —
microseconds, no candidate set, no per-query reverse index, no ClrMD calls. Depth cap of
20 retained per the project's graph rules, reported honestly when hit.

Expected hit rate: near-100% for the Gen2/LOH population root-path queries actually
target, versus 6.9% today — see §4.1 for the measured number and why "100% of the whole
heap" is the wrong bar.

### 3.3 Persistence

`ObjectParents` becomes a new columnar section in `cache.bin`, parallel to
`ObjectAddresses` / `ObjectMethodTables` / `ObjectSizes` / `ObjectGenerations` — the same
extension shape used when `ObjectGenerations` was added (`FormatVersion` 2 → 3). This one
bumps 3 → 4, with a new `CacheSectionId`. See [binary-format.md](../../binary-format.md)
and [schema-versioning.md](../../schema-versioning.md).

Consequence: built once per dump, reused across all five analyzers **and across runs**.

---

## 4. Cost, and the gate on this design

The build cost is one full traversal of the live object graph, enumerating every object's
references exactly once. That is materially more expensive than the current index pass,
which reads only `Address|MethodTable|Size|Generation` and never touches references.

**This is the risk in the design and it must be measured before implementation, not
after.** The comparison is not "build cost vs zero" — it is:

| | Current | Predecessor column |
|---|---|---|
| Per-query | ~10.3ms, 6.9% success | ~microseconds, ~100% success |
| Per dump | 0 | one graph traversal (**unmeasured**) |
| Across 5 analyzers | paid independently by each | paid once |
| Across runs | paid every run | paid once, cached on disk |
| EventLeak alone | 34.3s | ~0s + amortised share |

**Gate:** prototype the traversal standalone against the reference dump and measure wall
time and peak RSS. Decision rule:

- **Under ~60s** — clearly worth it. One-time, cached, amortised over five analyzers and
  every subsequent run, and it converts a broken feature into a working one.
- **60–120s** — worth it only as an opt-in section built on demand when an analyzer first
  requests a path, not during the default index build.
- **Over ~120s, or RSS beyond budget** — this design is wrong at this scale. Fall back to
  §6 and re-scope.

The 87M-object figure comes from prior profiling notes, not this dump; size the columns
from the reference dump's actual object count during the prototype.

### 4.1 Measured (standalone prototype, `tools/ProfileRootPathBackfill/`)

Run against the same reference dump used throughout this doc and
[event-leak-analyzer.md](event-leak-analyzer.md) (`Crash_IIS_BALTSTPRD`, 3.35 GB,
14,343,664 non-free objects, 1411 roots → 755 unique after dedup, 7 unresolved `Stack`
roots — all expected, same as `RootSetCache.BuildFromLiveHeap`):

| | Measured |
|---|---|
| Collect + sort addresses | ~20s |
| BFS (multi-source, roots → whole graph) | ~9–11s |
| **Total build** | **~30s** |
| Peak working set | ~1.8 GB (dump is 3.35 GB) |
| `addresses[]` (8 B × N) | 109 MB |
| `parent[]` (4 B × N) | 55 MB |

**Under the 60s line — gate passes.** And the ~30s here is a pessimistic upper bound on
what production would cost: this standalone prototype rebuilds and sorts the address
array from scratch because it has no access to the disk index. In production,
`ObjectAddresses` already exists, sorted, from the Phase-1 scan — the predecessor build
would only pay the BFS itself (~9–11s), not the ~20s collect+sort. The `addresses[]` and
`parent[]` columns (164 MB combined) are a small fraction of the 1.8 GB peak working set;
most of that is ClrMD's own per-object materialisation cost during traversal, the same
cost every analyzer already pays per object it visits.

**Hit rate — the number that actually validates the design — broken down by generation
(`segment.GetGeneration`, since ClrMD's ephemeral segment doesn't separate Gen0/1/2 by
segment kind alone):**

| Generation | Total | Visited | Hit rate |
|---|---:|---:|---:|
| Gen0 | 6,610,015 | 180,481 | 2.7% |
| Gen1 | 725,066 | 98,118 | 13.5% |
| **Gen2** | **7,007,364** | **6,407,504** | **91.4%** |
| Large (LOH) | 1,219 | 382 | 31.3% |
| **Whole heap** | 14,343,664 | 6,686,485 | 46.6% |

The whole-heap figure (46.6%) is *not* the number to compare against the current 6.9% —
it includes Gen0/Gen1, which churn heavily between GCs and are expected to be mostly
garbage at any snapshot instant; a dump is not a post-GC state, so a large ephemeral-dead
population there is normal, not a defect in the traversal. The population that
root-path queries are actually asked about — the long-lived Gen2/LOH candidates every
consuming analyzer selects ("top types by size, long-lived (Gen2/LOH)" per this project's
leak-detection rules) — hits **91.4%**, more than 13× today's 6.9%.

LOH's 31.3% is on a sample of only 1219 objects and is noted as an open question, not a
blocker — worth a follow-up look (dependent handles / `ConditionalWeakTable` targets and
COM/RCW-only reachability are the likely candidates for edges a plain
`EnumerateReferences(carefully: true)` walk wouldn't see) once the design moves past the
prototype stage.

### 4.2 Measured at scale (25.6 GB dump)

Same prototype, re-run twice against `w3wp.exe_260421_175618.dmp` (26,244 MB, 86,546,865
non-free objects, 5037 roots → 2389 seeded after dedup, 4 unresolved `Stack` roots) to
check whether the §4.1 numbers hold as dump size grows roughly 8×, and to see how much
run 1 was noise versus signal:

| | Run 1 | Run 2 | Avg |
|---|---:|---:|---:|
| Collect + sort addresses | 123.2s | 106.4s | 114.8s |
| BFS (multi-source, roots → whole graph) | 553.5s | 804.2s | 678.8s |
| **Total build** | **676.7s** | **910.6s** | **~793s (~13.2 min)** |
| Peak working set | 5.1 GB | 5.0 GB | ~5.1 GB (dump is 25.6 GB) |
| `addresses[]` (8 B × N) | 660 MB | 660 MB | 660 MB |
| `parent[]` (4 B × N) | 330 MB | 330 MB | 330 MB |
| Edges traversed | 137,033,360 | 137,033,360 | 137,033,360 |
| Objects visited (rooted) | 58,339,932 (67.4%) | 58,339,932 (67.4%) | identical |

Run-to-run variance is ~35% on BFS wall time (554s vs 804s) despite the hit rate and edge
count coming out **byte-for-byte identical** between runs — the traversal itself is
deterministic, so the swing is entirely environmental: run 2 executed with noticeably less
free system memory throughout (observed ~1.7–2.9 GB free vs run 1's ~2.6–5.1 GB), most
likely extra paging/GC pressure rather than anything in the algorithm. Treat 677–911s as
the realistic range for this dump on this machine, not either endpoint alone.

**This fails the gate either way.** Per-object cost held roughly constant across both runs
relative to §4.1 (~8× the objects, ~8-14× the BFS time — noisy but not superlinear, so the
algorithm doesn't degrade pathologically) — but linear-ish is not good enough here: even
the faster run (677s) is 5–8× past the "over ~120s" line, on a dump size (25.6 GB)
squarely inside this project's stated target range ("1GB-25GB+"). Peak memory is not the
problem (~5.1 GB, well bounded, columns are a tiny fraction of it, stable across both
runs) — wall-clock time is, and it's a resilient signal now that it's confirmed across two
independent runs rather than a possible one-off.

Hit rate at this scale, by generation:

| Generation | Total | Visited | Hit rate |
|---|---:|---:|---:|
| Gen0 | 2,213,545 | 28,656 | 1.3% |
| Gen1 | 857,463 | 778,260 | 90.8% |
| **Gen2** | **83,447,037** | **57,529,716** | **68.9%** |
| Large (LOH) | 28,820 | 3,300 | 11.5% |
| **Whole heap** | 86,546,865 | 58,339,932 | 67.4% |

Gen2 hit rate dropped from 91.4% (3.35 GB dump) to 68.9% here — still a >9× improvement
over today's 6.9% baseline, but the gap versus "near-100%" is real, not noise, and worth
tracking as a second open question alongside LOH's (§4.1). One plausible factor: a larger,
longer-running process accumulates more legitimately-dead Gen2 objects not yet collected
at snapshot time (Gen2 collections are rarer), which would show up as unreachable-from-root
regardless of traversal correctness — but this is a hypothesis, not yet verified against
this dump's actual GC history.

**Revised verdict:** the fixed wall-time gate in §4 is dump-size dependent, not a single
pass/fail. A prototype run on one dump size cannot stand in for the whole "1GB-25GB+"
range this project targets. Given both data points:

- The design is correct and the cost model is linear-not-worse — the algorithm itself is
  not the problem.
- On dumps in the multi-GB range (the reference dump here, and most real-world crash
  dumps), the build comfortably passes and should run eagerly during the default index
  build, per §4's "under 60s" bucket.
- On dumps approaching 25 GB+, an eager full-heap build is a 10+ minute blocking cost —
  unacceptable as part of the default index build, and even the §4 "opt-in, built on
  demand" bucket needs a caveat at this size: an on-demand 11-minute wait the first time
  any analyzer asks for a root path is still a bad interactive experience.
- This pushes the large-dump case toward §6's "scope to a suspect superset" fallback
  rather than a whole-heap predecessor column: build the predecessor column only over a
  candidate-reachable subgraph (Gen2/LOH suspects plus their forward neighbourhoods).
  Sizing that scoped build is unmeasured and should be the next prototype step before
  committing to §3 for large dumps.

### 4.3 Threading investigation — multi-runtime parallel BFS does not fix this

Before committing to the scoped-subgraph fallback, we checked whether the §4.2 wall-time
failure is fixable with parallelism rather than a smaller scope.

**Single-runtime multithreading does not help.** `ClrRuntime.IsThreadSafe` is `true`
(verified empirically, `scratchpad/ThreadSafeCheck/`), but "thread-safe" here only means
concurrent calls don't corrupt state — an internal lock (most likely guarding the shared
`ClrType`/metadata cache and DAC entry points) serializes concurrent `GetObject` /
`EnumerateReferences` calls on one runtime, so `Parallel.For` over a single `ClrHeap` comes
out *slower* than sequential, not faster. Ruled out via elimination (chunked `Parallel.For`
vs `Parallel.ForEach`, `GetObject`-only vs `GetObject`+`EnumerateReferences`): the
contention is in object/type resolution itself, not scheduling or reference enumeration.

**Multiple independent runtimes over the same dump file does help — on a sample.**
Opening N separate `DataTarget`/`ClrRuntime` instances (each with its own `ClrHeap`, no
shared state) and dividing work across them, with `UseLockFreeMemoryMapReader` enabled,
gave real, correctness-verified speedups on bounded samples
(`scratchpad/MultiRuntimeCheck/`):

| Dump | Sample | Workers | Sequential | Parallel | Speedup |
|---|---:|---:|---:|---:|---:|
| Small (3.35 GB) | 4M objects | 8 | 2.36s | 0.87s | ~2.7× |
| Small (3.35 GB) | 4M objects | 4 | 2.15s | 1.04s | ~2.1× |
| Large (25.6 GB) | 2M objects | 4 | 16.57s | 1.09s | ~15× |

The larger win on the large dump reflects both avoided lock contention and I/O
parallelism across independent memory-mapped views — promising enough to prototype for
real, per "we work on facts."

**A real level-synchronous parallel BFS prototype (`tools/ProfileParallelRootPathBackfill/`)
confirms the algorithm is correct but the memory cost does not scale to whole 25GB+
heaps.** Design: shared sorted `addresses[]` + `parent[]` columns as in the sequential
tool; each worker owns its own independent `ClrRuntime`/`ClrHeap`; per-level frontier is
chunked across workers; first-visit ownership of a child ordinal is decided by
`Interlocked.CompareExchange(ref parent[childOrd], curOrd, Unvisited)`, so concurrent
discovery from multiple parents in the same level is race-free without a shared queue.

- **Correctness, small dump (3.35 GB, 4 workers):** Gen2 hit rate 91.4%, identical to the
  sequential §4.1 baseline. BFS wall time 8.09s (1,139 levels) — the algorithm itself is
  correct and level-synchronous batching works.
- **4 workers, 25.6 GB dump:** aborted mid-BFS. Working set climbed past 13 GB (vs the
  sequential tool's ~5.1 GB peak for the *entire* run) and system free memory dropped
  below 50 MB before it was killed. Each additional runtime's memory-mapped view of the
  25.6 GB file, plus its own independently-populated type/metadata cache built while
  visiting a large fraction of the heap, costs far more than the 2M-object sample test
  suggested — that test never touched enough of the file or type universe to reveal it.
- **2 workers, 25.6 GB dump:** aborted before BFS even started, during the plain
  single-threaded `EnumerateObjects()` collect/sort pass — the same code path the
  sequential tool runs at ~5.1 GB peak. The only difference was a second, idle,
  independent runtime open in the background. Free memory dropped to ~300 MB during
  address collection alone.

**Conclusion: multi-runtime parallelism is not a fix for the §4.2 gate failure on this
machine.** It scales cleanly on bounded samples (few million objects, low type diversity)
but the per-runtime cost of mapping and touching a large fraction of a 25GB+ dump — even
with just 2 runtimes — exceeds available memory before parallel BFS work even begins. This
is a different failure mode than §4.2's (that one was CPU/time-bound with memory to spare;
this one is memory-bound before the bottleneck we were trying to parallelize even runs).
Root cause is believed to be concurrent memory-mapped views of the same large file plus
duplicated per-runtime type-metadata caches scaling with heap coverage, not the BFS/CAS
logic — worth flagging as a `UseLockFreeMemoryMapReader` / ClrMD scaling caveat if
revisited, but not worth further worker-count tuning on this class of machine.

This reinforces §4.2's revised verdict: the scoped candidate-reachable subgraph fallback
(§6), not whole-heap parallelism, is the right next step for 25GB+ dumps. A scoped build
naturally sidesteps this failure mode too, since it never needs to touch a large fraction
of the heap in the first place, single-runtime or multi.

---

## 5. Ship first, independent of all the above

**Early-out before the reverse index.** In today's implementation, Phase 2 builds the full
5000-node reverse index before Phase 3 discovers there is no path. Phase 1 already knows
whether the two frontiers ever intersected. If they did not, return immediately.

- Removes the dominant cost in the 93% case.
- Changes no successful result — only skips work that provably cannot produce one.
- Small, local, independently testable, and it benefits all five analyzers now.
- Estimated: most of EventLeak's 34.3s, without touching the index format.

**Fix the truncation signal at the same time.** Replace the boolean with an outcome enum
(`Found` / `NoPathInCandidateSet` / `BudgetExhausted` / `DepthCapped`) so that callers and
future measurements can distinguish "searched and found nothing" from "never really
searched." Without this, every subsequent tuning decision is guesswork — as ours was.

---

## 6. Fallbacks if §4's gate fails

- **Rebalance the budget.** Stop seeding all roots into the candidate set; treat root
  membership as a test rather than a seed, freeing 28% for expansion. Cheap, but still
  expanding the wrong direction — a mitigation, not a fix.
- **Scope the predecessor column to a suspect superset** rather than the whole heap,
  consistent with the project rule "build partial indexes scoped to types/suspects."
  Requires knowing targets up front, which means batching path queries across analyzers —
  a pipeline change.
- **Accept the limitation and say so.** If no affordable design reaches a useful hit rate,
  the honest move is to stop presenting root paths as evidence and report only the
  direct-root hint, which is cheap and correct as far as it goes.

---

## 7. Sequencing

1. **§5 early-out + outcome enum.** Immediate, low risk, benefits five analyzers.
   Re-measure EventLeak's `PopulateEvidence` to confirm the drop.
2. **§4 prototype and gate.** Measure the traversal standalone. Decide by the rule in §4
   before writing any production code. **Done** — see §4.1/§4.2: passes eagerly on
   multi-GB dumps, fails at 25 GB+; the whole-heap design needs a scoped variant for
   large dumps before it can ship unconditionally (see §4.2's revised verdict).
   Multi-runtime parallelism was checked as an alternative to scoping and ruled out —
   **done**, see §4.3: correct but memory-unsafe on whole 25GB+ heaps, even at 2 workers.
3. **§3 predecessor column** behind the `ObjectParents` section, `FormatVersion` 4, with
   graceful degradation to §5 behaviour when the section is absent. Built eagerly during
   the default index build below a size threshold (TBD, informed by §4.1/§4.2 — the 3.35
   GB case passes at ~30s, the 25.6 GB case fails at ~677s); above it, deferred to an
   on-demand scoped build per §4.2's revised verdict rather than skipped outright.
4. **Migrate consumers** one at a time — `EventLeakAnalyzer` first (it has the measurement
   harness), then `StaticRootLeakDetector`, `TimerLeakAnalyzer`, `ReferenceChainAnalyzer`,
   `DominatorAnalyzer`.
5. **Revisit `MaxGroupsToEnrich`** in [event-leak-analyzer.md §3.2](event-leak-analyzer.md)
   — the bound exists to limit the damage of a 7% hit rate and should be re-derived once
   the hit rate is ~100%.

**Validation:** hit rate is the headline metric, not just wall time. Report
`found / attempted` before and after on the reference dump; a faster finder that still
finds nothing is not an improvement. Paths must also be spot-checked for plausibility —
a shortest path through a root is verifiable by hand for a handful of known statics.

**Measurement harness:** the `[PERF]` brackets currently in `PopulateEvidence` report
per-instance timing, found count and truncation count. Keep them until step 4 completes.
Run as a single filtered test (`DD_RUN_DISCREPANCY_TESTS=1`), never the full suite.

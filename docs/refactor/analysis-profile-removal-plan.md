# Exact analysis: removing scan caps, sampling, and the AnalysisProfile system

Status: **audit complete, §11 pre-implementation checklist fully closed out** — 33 of 33 registered
analyzers (**26 GREEN, 6 AMBER, 1 RED**). Blockers **B1-B4** (§11.1), decisions **D1-D10** (§11.2),
verifications **V1-V4** (§11.3, surfaced 6 additional dead knobs beyond the original 3), and
measurements **M1-M10** (§11.4, all measured or explicitly flagged as needing a different dump) are
all resolved — see each section for the individual outcomes, including two real defects found in the
process (F10's `List<T>`-capacity `OutOfMemoryException`, D8's `AdaptForSize` double-scaling bug) and
one production defect confirmed cheap-but-real (M6's HeapTopology live walk, with a free exact
alternative identified). **Nothing left to decide before implementation starts** — the per-analyzer
exactness migration (§9) and ordering constraints (§11.5) are next. The goal throughout (§1) is
exactness/correctness, not just cap removal: every analyzer's reported numbers should be measured,
not estimated or silently capped.

**Implementation progress: 25 of 33 registered analyzers done — §9.1 Boxing, §9.2 ObjectShape,
§9.3 Module, §9.4 GCGeneration, §9.5 GCHandle, §9.7 LockGraph, §9.8 LohFragmentation, §9.9
SegmentReservation, §9.10 Jit, §9.11 Array, §9.12 String (partial — AMBER, not GREEN; see its
implementation notes for what's deliberately still deferred), §9.13 AsyncStateMachine, §9.14
StaticRootLeak, §9.15 FinalizableObject, §9.16 GCRoot, §9.20 ReferenceChain (partial — moved from
RED to AMBER, not GREEN; see its implementation notes for the search-layer caps deliberately kept),
§9.17 Collection (partial — AMBER; the `Profile`-branch fix landed, two knobs recategorized as
real work-scoping thresholds and kept), §9.18 Dominator (only one confirmed-dead field deleted;
`Preset`/`Default` removed and all kept fields stopped tier-varying), §9.19 EventLeak (partial —
AMBER; `MaxGroupsToEnrich` fully deleted thanks to the existing wall-clock budget, but
`EnableLowIncomingRefsCheck`'s underlying correctness bug documented and deliberately left for a
follow-up), §9.21 TimerLeak (had no `AnalysisOptions` knobs to begin with; found and fixed a dead
`ITypedResourceInstanceSampler` implementation outside the original audit scope — a bogus `Generation`
sentinel and a fully-unconsumed sample payload — and wired the sample data into the report instead of
deleting it), §9.23 Thread (a third size-tier scaling system, `AdaptForSize`, deleted along with its
own double-applying bug found in D8; the reservoir-sampled "other threads" feature redesigned into a
complete deterministic `STCompact` table rather than left capped or deleted outright), §9.24
ThreadStackCluster (6-frame lossy signature deleted — cluster identity is now the whole stack, free
once §9.23's unbounded frame capture landed; found and fixed a dead always-`false` `Truncated` render
flag in passing), §9.25 Hang (`MaxTasksToScan` was corrupting `PendingTasks`/`FaultedTasks`/
`CanceledTasks` past the cap, not just report width; found the real waiting-thread display cap was a
hardcoded `.Take(10)` masquerading behind the already-dead `TopWaitingThreadsPerGroup` option), §9.26
Crash (corrected its own "options class deleted outright" claim — `MaxExceptionsPerType` gates real
per-object stack-trace/inner-exception-chain extraction, kept as a fixed constant; the other seven
knobs deleted as originally planned, plus one more confirmed-dead knob and a genuinely-uncapped live
stack walk found in passing), §9.27 Memory (deleted the weighted quota-merge type-selection entirely,
stronger than the original "keep, fix one value" verdict for the four ranking weights; found
`TopTypesCount` also bounds a real per-type retained-size BFS and re-scoped that one concern to an
internal constant instead of deleting it outright)
(§9.6's orphaned
`DependentHandleAnalysisOptions` was also deleted alongside GCHandle — not a separate registered
analyzer, per the row-4 cross-reference below).** See each section and the §7 verdict table for what
shipped in each.

> **Correction (roster built from the wrong source):** the audit was originally built by walking
> `src/DumpDetective.Core/Options/`, not the analyzer registry — so any analyzer with no dedicated
> options class was invisible to it. Cross-checking against
> [DefaultAnalyzerFeatureModuleCatalog.cs](../../src/DumpDetective.Reporting/Capabilities/DefaultAnalyzerFeatureModuleCatalog.cs)
> (33 registered modules, the actual ground truth) found two further defects in the table itself:
> a "Retention" row that duplicated the Dominator audit (`RetentionOptions` is consumed exclusively
> by `DominatorAnalyzer` — merged into §9.18), and a "DependentHandle" row for an analyzer that does
> not exist (§9.6 already found the options class orphaned — kept as a note under GCHandle, not a
> row). Net: 28 of the original 30 rows were real distinct audits; 5 registered analyzers
> (WeakReference, DbConnection, WcfChannel, HttpObject, LeakCandidate) were missing entirely and are
> added below as group 6. **Lesson for any future re-audit: enumerate from the module catalog, not
> from the options folder.**
Supersedes: the earlier profile-only removal plan (profile deletion is now a subset of this work, see §2)

---

## 1. Goal

Make every analyzer produce **exact** results on dumps up to ~25 GB, within a **30-minute** hard
ceiling, and delete the `AnalysisProfile` system as a consequence.

Exact means: no sampling strides, no scan caps that silently truncate, no `Top-N` limits applied
inside analysis, no BFS node/depth budgets standing in for a real traversal. Where a number is
reported, it is measured rather than estimated.

### The real time target is ~10 minutes, not 30

[dominator-tree-memory-profile.md § 9](../analysis/phase1-redesigns/dominator-tree-memory-profile.md)
records that identical work measured **9.9 s to 27.3 s across runs** depending on OS page-cache
state — a 2.7x wall-clock spread that has nothing to do with the code. A 30-minute ceiling therefore
needs ~3x headroom to hold on a cold cache. **Design against ~10 minutes nominal.**

### Non-goals

- Retuning any threshold that defines *what is interesting* (§3, Category 5). Those are semantics.
- Removing bounded **memory**. CLAUDE.md's bounded-memory rule stands, unmodified. This work removes
  bounded *work*, which is a different constraint (§6.1).

---

## 2. Why the profile deletion is now a subset, not a parallel effort

Deleting the caps deletes the presets for free in most cases.

Boxing has five knobs: exactness deletes `TypeScanCap` and moves three `Top-N` limits to the render
layer, leaving only `OversizedThresholdBytes`. A `Preset()` that varies nothing but a Category 5
threshold is meaningless — varying the *definition of interesting* by tier never made sense — so
`BoxingAnalysisOptions.Preset` dies as a side effect. Array is the same shape: strip `SampleStride`,
`SparseSampleLimit` and three `Top-N`, and only `SparseSampleMinLength` survives.

**For every GREEN analyzer, `Preset()` disappears without being the target of the change.** Explicit
profile work is only needed where Category 4 restructuring or surviving thresholds leave real knobs
behind.

The residual profile-only cleanup that is *not* emergent — dead parsers, the enum's home, the
resolver plumbing, the config key — is preserved in §8.

### Commit discipline

Two commits per analyzer, in order:

1. **Cap/profile removal that provably changes no output.** For a GREEN analyzer whose caps never
   bit on the test dump, this is behaviour-neutral and bisectable.
2. **The exactness change**, which does change output.

Never combine them. The value of (1) is that it can be reverted independently and that a regression
in output can be attributed unambiguously to (2).

---

## 3. The five categories

Every knob in `src/DumpDetective.Core/Options/` falls into exactly one. The category determines the
action; the audit's job is to assign it correctly.

| # | Category | Bounds | Action |
|---|---|---|---|
| 1 | **Top-N report limits** | rows | **Relocate to render layer.** Analyzer emits the complete ranked aggregate; the renderer slices. Complete data, truncated *view*, reversible. |
| 2 | **Sampling strides** | accuracy | **Delete.** Cost is linear over already-mapped data; the benefit is that the number stops being a guess. |
| 3 | **Linear scan caps** | wall-clock | **Delete** — provided the pass accumulates aggregates, not per-object state (Q3 below). |
| 4 | **Graph traversal budgets** | wall-clock *and* bytes | **Delete the per-suspect traversal**, not the bound. Re-point at the dominator tree / reverse index, which compute the exact answer once. |
| 5 | **Semantic thresholds** | nothing | **Keep** as analyzer constants — but stop varying them by tier (§3.1). "Unbounded" is meaningless: `WasteThresholdBytes = 0` makes every collection a finding, which is the same as none. |

### 3.1 The presets vary semantics, not just effort — found across group 1

Category 5 thresholds define *what a finding means*. The preset system varies them anyway, so the
same dump produces contradictory findings depending on a knob users read as "how thorough."

| Analyzer | Threshold | Fast | Balanced | Full |
|---|---|---:|---:|---:|
| SegmentReservation | `ThirtyTwoBitPressureThresholdBytes` | 2.0 GB | 1.5 GB | 1.0 GB |
| SegmentReservation | `RatioHighPressureThreshold` | 12.0 | 10.0 | 8.0 |
| Jit | `LargeMethodThresholdBytes` | 96 KB | 64 KB | 32 KB |
| Boxing | `OversizedThresholdBytes` | 96 | 64 | 48 |
| Module | `HeavyModuleWarningThresholdBytes` | 300 MB | 200 MB | 100 MB |
| Module | `DensityAnomalyMinBytes` | 100 MB | 50 MB | 20 MB |
| Module | `DensityAnomalyMaxTypes` | 3 | 5 | 10 |
| Hang | `LongWaitThreshold` (s) | 8 | 5 | 3 |
| Hang | `HighThreadPoolThreshold` | 150 | 100 | 60 |

A 32-bit process reserving 1.2 GB is *under* pressure at Fast and *over* pressure at Full. A 48-byte
struct is oversized at Full and fine at Fast. A thread waiting 6 seconds is not long-waiting at Fast
and is at Full — so whether a dump is diagnosed as hung depends on the tier. Nothing about the
analysis got more thorough; the verdict changed.

**Two analyzers go further and vary the *algorithm*, not just a threshold:**

- **Memory** (§9.27) re-tunes the four `TopTypesBy*Weight` selection weights per tier, so Full is not
  a superset of Fast — a type surfaced at one can be absent at the other.
- **AllocationPattern** (§9.30) switches `SelectionMode`, `ScanStrategy` and `SelectionPriority`
  enums per tier, and re-weights `Gen0Weight`/`Gen2Weight` at Full.

A knob labelled "how thorough" silently substituting a different ranking function or scan strategy is
the least defensible form of this defect.

**This is a defect independent of the exactness work**, and it is the strongest standalone argument
for deleting the preset system: even an analyzer with no caps at all (§9.9 SegmentReservation) is
made incoherent by it. It also means §2's "commit 1 changes no output" property holds only where the
test config does not set a profile — which is the default, but worth stating.

**Action (D4, decided):** every Category 5 threshold keeps its Balanced value as its single constant,
non-blocking. Add a one-line rationale comment next to each when it lands as a standalone value — see
D4 (§11.2) for the per-threshold anchor/shaky breakdown and which ones to flag for revisit once field
data exists rather than re-deriving them abstractly now.

---

## 4. Why this is affordable — the budget arithmetic

Measured, from the dominator-tree memory profile:

| Work | 3.3 GB dump | 25.6 GB dump |
|---|---:|---:|
| Reachable graph | 6.69M nodes / 17.37M edges | 58.34M nodes / 137.03M edges |
| **Exact dominator tree (Lengauer-Tarjan, full graph)** | 9.4 s | **218.5 s** (measured) |
| Phase 1 index build, cold | ~40 s / 14.6M objects | ~250-350 s (scaled) |
| Full analyzer run | 44.1 s | — |

Index build plus **exact Lengauer-Tarjan over 137 million edges** costs roughly 8-9 minutes on the
largest dump. That is the heaviest exact computation in the codebase, and it already runs.

Every cap under audit sits *downstream* of it, operating on an index that is already built. A linear
pass over the columnar index is seconds, not minutes.

**The caps are vestigial.** They were calibrated before the forward index, the full reverse index,
and the dominator tree existed. That is why values like `MaxBfsNodes = 200` look absurd against a
50M-object heap today.

---

## 5. Audit template

Eight questions per analyzer, in three groups:

- **Q1-Q3 — can we?** Feasibility. A no here sets the verdict.
- **Q4-Q6 — what's the work?** Structure, cost, and dependencies.
- **Q7-Q8 — what's the payoff, and how big is the diff?** Added after the first three audits, where
  both turned out to carry more signal than Q1-Q6 and were being found by accident rather than by
  method.

| Q | Question | Why it matters |
|---|---|---|
| **Q1** | What does each knob bound — rows, wall-clock, or resident bytes? | Assigns the category (§3). Only resident bytes can violate CLAUDE.md's bounded-memory rule. Beware misleading names: `ObjectShapeAnalysisOptions.InstanceCountCap` bounds neither instances nor counts, it bounds *types*. |
| **Q2** | Does removing it change the asymptotic class? | Linear pass over an index → safe. Per-suspect graph walk → O(suspects x graph) → not safe. |
| **Q3** | Does anything materialize per-object? | The one real disqualifier. Check where the collection is *sized*, not where the cap is applied — ObjectShape's candidate list is already O(distinct types) before its cap bites, so the cap was never buying memory. |
| **Q4** | Does an exact structure already supersede this approximation? | If yes, **delete** the approximation rather than unbounding it. Dominator tree, full reverse index, forward index, `TypeAggregates`, `TypeShapeCache`. |
| **Q5** | Estimated exact-mode cost against the ~10 min nominal budget. | State whether this is an estimate or a measurement. Estimate freely for index-backed aggregate passes; **measure** anything that iterates ClrMD metadata per item (see §9.3 Q5). |
| **Q6** | Does exactness here depend on the reverse index? | If so it is gated on `MaxParentsPerChild` (§6.2) and cannot be called exact yet — verdict is capped at AMBER until that is resolved. |
| **Q7** | **What does the cap corrupt beyond report width?** | The exactness payoff, and the argument that justifies the change to a reviewer. Row limits are cosmetic; caps that feed an accumulator silently redefine a total. 3 for 3 so far: Boxing's `TotalBoxedObjects`, ObjectShape's `TotalGcScanWork` and `AvgRefFieldsPerType`, Module's `EstimatedManagedBytes`. No renderer change can fix these — the wrong number is computed in the analyzer. |
| **Q8** | **What else dies with it?** | Consistently the largest part of the diff, and invisible from the options file alone. Look for: determinism sorts that exist only to make truncation reproducible (Boxing :78-94, Module :137-139), truncation notices, excluded-item counters, selection modes that only choose *which* items survive a cap, and hard-coded sampling budgets. Module: one deletion retires four knobs and two enums. |

### Procedural step: check for dead knobs

Before assigning categories, confirm every knob is actually read:

```bash
grep -rn "KnobName" --include=*.cs src | grep -v "Options/"
```

Module's `ModuleSelectionMode` and `IncludeExcludedModuleSummary` are read **nowhere** in `src` while
being set to three different values across three presets. Neither would have surfaced from reading the
options file. Assume nothing is live until grep says so.

### Verdict scale

- **GREEN** — delete the caps; exact is reachable with no structural change.
- **AMBER** — exact is reachable but requires re-pointing at an existing structure, or is Q6-gated.
- **RED** — needs a genuine bound, or is blocked on a global issue.

---

## 6. Global blockers

### 6.1 The performance checklist mandates the caps — resolved, rewritten

**Done.** [performance-checklist.md](../performance-checklist.md) now leads with an explicit
bounded-memory (non-negotiable, unconditional) vs. bounded-work (case-by-case; illegitimate only when
it silently caps a reported total/count/exactness flag) split, replacing the old blanket rules:

> - Do NOT recursively traverse without depth limits
> - Always use bounded traversal (BFS with limits)
> - Analyze only top N types (default: 20-50)
> - Avoid full reverse index

Each is now reframed: the root-path BFS depth limit is scoped to display-path *selection*, not
reachability; top-N candidate selection for leak detection is scoped to *which* items get expensive
follow-up, not to capping reported totals; "avoid full reverse index" is retired outright — the
reverse-edge index is now a full, disk-backed, uncapped index (per §6.2 /
[phase1-integration.md §3](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md#3-stage-a--reachability-walk-shipped)),
with the old working-set concern resolved by disk-backed storage rather than by refusing to build it.

### 6.2 `MaxParentsPerChild = 10_000` caps the graph itself — resolved, deleted

**Resolved by [phase1-integration.md §3](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md#3-stage-a--reachability-walk-shipped):**
the cap was deleted outright, not raised. `ReverseIndexMetadata.MaxParentsPerChild` is kept only for
on-disk format stability and is always written as `int.MaxValue`. Rest of this subsection kept for
the original problem framing.

[ReverseEdgeExtractor.cs:83-87](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeExtractor.cs#L83-L87)
**drops edges** past 10,000 parents per child during index extraction and records the child as
truncated. This sits *below* every analyzer option: no amount of unbounding at the analyzer layer
recovers those edges.

The affected objects are exactly the ones that matter — singletons, static caches, interned strings,
`string.Empty` — and they are disproportionately likely to sit on a retention path.

This changes the failure mode in the wrong direction if ignored. Today a capped search reports
`searchTruncated` and [Evidence.cs:27](../../src/DumpDetective.Analysis/Models/Evidence.cs#L27) drops
confidence 0.8 → 0.6. Unbound the analyzers without addressing this and you get *full confidence on
a silently incomplete graph*, which is worse than the status quo.

**Action, early:** `ReverseIndexMetadata.TotalTruncatedChildren` is already recorded per build. One
real-dump run answers whether this is a footnote or a blocker, and determines whether every Q6-yes
analyzer is GREEN or RED. Do this before auditing group 4.

### 6.3 A second frame cap sits below the analyzer layer — resolved, scope was narrower than described

[RootSetCache.cs:189](../../src/DumpDetective.Analysis/Cache/RootSetCache.cs#L189) declares
`private const int MaxFramesPerThread = 256` and passes it to
`thread.EnumerateStackTrace(includeContext: false, maxFrames: 256)` inside
`BuildStackFrameOwnerMap` ([:191-201](../../src/DumpDetective.Analysis/Cache/RootSetCache.cs#L191-L201)).

**This subsection's original premise was wrong: the cap does not affect the root set.** The two
canonical root-discovery paths — `RootIndexWriter.Write`
([:56](../../src/DumpDetective.Analysis/Indexing/Satellite/RootIndexWriter.cs#L56), the Phase 1 disk
index) and `RootSetCache.BuildFromLiveHeap`
([:275](../../src/DumpDetective.Analysis/Cache/RootSetCache.cs#L275)) — both call
`heap.EnumerateRoots()` with no frame cap at all, and that is what feeds every root-path search and
the dominator tree's GC-root seeding. `MaxFramesPerThread` only bounds `BuildStackFrameOwnerMap`,
which backs `TryResolveStackFrameOwner` alone — a narrow, on-demand feature that labels a stack
root's owner type/method name for the small set of top-severity Stack-kind findings shown in a
report. A root whose slot lies past frame 256 is still discovered, still contributes to retained
bytes and path-finding exactness; it just renders without an owner-attribution label in that one
report field.

This is independent of `JitAnalysisOptions.MaxFramesPerThread` (§9.10), which is a genuine
work-bounding cap in `ThreadStackScanDispatcher`/JIT frame analysis and out of scope here.

**No real-dump measurement needed** — resolved by code inspection, not empirically: the cap's blast
radius is a single cosmetic report field, not root exactness.

---

## 7. Verdict table

| # | Analyzer | Group | Verdict | Knobs deleted | Notes |
|---|---|---|---|---|---|
| 1 | Boxing | aggregator | **GREEN** ✅ DONE | 4 of 5 | §9.1 — cap bites today; deleting it also deletes a determinism workaround |
| 2 | GCGeneration | aggregator | **GREEN** ✅ DONE | 2 of 5 | §9.4 — pure output slicing; `PohThresholdPercent` (flagged dead by V4) was instead wired up to a real POH-share finding rather than deleted — see implementation notes; 3 thresholds survive |
| 3 | GCHandle | aggregator | **GREEN** ✅ DONE | 1 of 5 | §9.5 — pure output slicing; 4 thresholds survive, but were only reachable in the analyzer — the finding generator read its own disconnected copies until this pass rewired them through the domain result |
| 4 | *(GCHandle, cont'd)* | — | ✅ DONE | — | `DependentHandleAnalysisOptions` is **not a registered analyzer** — orphaned options class, folded into §9.6 below rather than counted as its own row; deleted outright |
| 5 | LockGraph | aggregator | **GREEN** ✅ DONE | 1 of 1 | §9.7 — options class deleted outright; two independent lists shared one cap, plus a second unrelated render-layer cap found and fixed |
| 6 | LohFragmentation | aggregator | **GREEN** ✅ DONE | 2 of 2 | §9.8 — one cap applies during collection, truncating a type aggregation; the index fast path was also missing a sort entirely, found during implementation |
| 7 | ObjectShape | aggregator | **GREEN** ✅ DONE | 2 of 2 | §9.2 — cap of 200 types corrupts three whole-heap aggregates |
| 8 | SegmentReservation | aggregator | **GREEN** ✅ DONE | 0 of 2 | §9.9 — already exact; preset varies *semantics*, so it must still die |
| 9 | Module | aggregator | **GREEN** ✅ DONE | 8 of 11 | §9.3 — 2 knobs are dead code; one deletion cascades into 4 more |
| 10 | Jit | aggregator | **GREEN** ✅ DONE | 3 of 4 | §9.10 — one cap corrupts **six** accumulators; worst Q7 so far; render layer also hardcoded a stale 64 KB flag threshold, fixed |
| 11 | Array | sampling | **GREEN** ✅ DONE | 5 of 6 | §9.11 — `WastedBytes` is an extrapolation of an extrapolation of one sample object; also found an orphaned dead-code reader and an obsolete `ScanLimited` field |
| 12 | String | sampling | **AMBER** ⚠️ PARTIAL | 9 of 16 (+`MaxDedupUnique`, a non-`StringAnalysisOptions` cap) | §9.12 — the 9 safely-removable knobs are gone (incl. the real Q7 cap, `MaxStringsToDedup`); full exact dedup still needs a restructured hash-count pass, not shipped this pass |
| 13 | AsyncStateMachine | sampling | **GREEN** ✅ DONE | 6 of 7 | §9.13 — a domain-result comment already documented its own corrupted sum; the per-type histogram cap was replaced with an exact-count early-exit instead of being dropped outright |
| 14 | StaticRootLeak | retained-size | **GREEN** ✅ DONE | 4 of 4 (2 kept, Category 5) | §9.14 — §10's dominator provider shipped; `MaxRetainedObjectsToScan`/`SampleRetainedObjectsToInspect`/`MaxRootsToReport`/`TopRetainedTypesToReport` all resolved via `EnumerateRetainedSet` + render-layer pagination |
| 15 | FinalizableObject | retained-size | **GREEN** ✅ DONE | 5 of 5 | §9.15 — the fourth private BFS copy (`BfsEstimateRetained`) deleted outright, replaced by `TryGetRetainedBytes`; options class deleted |
| 16 | GCRoot | retained-size | **GREEN** ✅ DONE | 4 of 4 (2 hardcoded, not deleted) | §9.16 — `PathSearchTopN` deleted per M4; `MaxBfsDepth`/`MaxBfsNodes` moved off the profile surface to internal constants (dominator-tree rewire for the path-type-name walk itself stays deferred, see notes) |
| 17 | Collection | retained-size | **AMBER** ⚠️ PARTIAL | 6 of 9 (2 recategorized to Category 5, kept) | §9.17 — `Profile`/`AnalysisProfile` branch replaced and deleted; `TopWastefulCollectionsToShow`/`PathAnalysisTopN` recategorized as real in-scan work-scoping thresholds, not display caps; the embedded `ReferenceChainOptions` turned out to be entirely dead (analyzer reads the top-level one instead) and was deleted; inherits §9.20's residual AMBER for root-path descriptions |
| 18 | Dominator | retained-size | **GREEN** ✅ DONE | 1 of 8 (7 recategorized/already-resolved, kept) | §9.18 — already exact; owns `RetentionOptions` exclusively (see row 22 note); only confirmed-dead `TopFinalizerTypesToShow` deleted; `Preset`/`Default` deleted, all kept fields stopped tier-varying |
| 19 | EventLeak | root-path | **AMBER** ⚠️ PARTIAL | 8 of 10 (`MinSubscribers` also deleted, a correction; `EnableLowIncomingRefsCheck` deliberately deferred) | §9.19 — wall-clock budget let `MaxGroupsToEnrich` be fully deleted (rare win); found `CountIncomingRefs` is not just slow but wrong (arbitrary 500-object sample) — documented, not fixed this pass |
| 20 | ReferenceChain | root-path | **AMBER** ⚠️ PARTIAL (was RED) | 5 of 8 (+3 dead `ExecutionPolicy`/CLI knobs found and deleted) | §9.20 — parallel profile enum deleted, no longer RED; `LargeFanoutThreshold`/`MaxCandidateNodes`/`MaxRootExpansionDepth` recategorized and kept (real search-layer caps, not the now-resolved index-layer one) — real hub fan-in measured up to 10.76M keeps this AMBER |
| 21 | TimerLeak | root-path | **GREEN** ✅ DONE | 0 (no options class) | §9.21 — no knobs of its own; inherits every shared traversal bound; found and fixed a dead `ITypedResourceInstanceSampler` implementation (bogus `Generation` sentinel, unconsumed sample payload) outside the original audit and wired the sample into the report |
| 22 | *(= Dominator, row 18)* | — | — | — | "Retention" is **not a separate analyzer** — `RetentionOptions` belongs to `DominatorAnalyzer`. Originally audited as a second, duplicate row; findings (`RootPathLargeFanoutThreshold` exclusion, `MaxLeakScanObjects` vs. 87M-object heap, etc.) merged into §9.18. |
| 23 | Thread | non-heap | **GREEN** ✅ DONE | 9 of 10 (1 kept, `PrewarmCacheInBackground`) | §9.23 — a **third** tier system (`AdaptForSize`) deleted along with its double-applying bug; unbounded stack walk via a named 100K sentinel, not `int.MaxValue`; reservoir-sampled "other threads" redesigned into a complete deterministic `STCompact` table |
| 24 | ThreadStackCluster | non-heap | **GREEN** ✅ DONE | 6 of 7 (1 kept, `MinClusterSize`; `ProduceClusterExports` stays pending D6's deferred cross-cutting move) | §9.24 — 6-frame signatures no longer merge genuinely different stacks; fixed a dead always-false `Truncated` render flag found in passing |
| 25 | Hang | non-heap | **GREEN** ✅ DONE | 4 of 5 (1 kept, `HighThreadPoolThreshold`) | §9.25 — `MaxTasksToScan` corrupted `PendingTasks`/`FaultedTasks`/`CanceledTasks` past the cap, not just report width; found the real waiting-thread cap was a hardcoded `.Take(10)`, not the dead `TopWaitingThreadsPerGroup` option |
| 26 | Crash | non-heap | **GREEN** ✅ DONE | 7 of 8 (1 kept, `MaxExceptionsPerType` — corrects the row's own "8 of 8" claim) | §9.26 — already implements the §10 render-layer pattern for 7 knobs; `MaxExceptionsPerType` gates real per-object stack-trace/inner-exception extraction and was kept |
| 27 | Memory | non-heap | **GREEN** ✅ DONE | 5 of 6 (1 kept, `LohThresholdBytes`) | §9.27 — deleted the weighted quota-merge selection entirely (stronger than "keep, fix one value"); found `TopTypesCount` also bounds a real per-type retained-size BFS, corrected and re-scoped to an internal constant rather than deleted outright |
| 28 | HeapTopology | non-heap | **GREEN** | 1 of 1 | §9.28 — a literal exact/not-exact switch, defaulting to **not** |
| 29 | AsyncTask | non-heap | **GREEN** | 8 of 8 | §9.29 — options class deleted outright |
| 30 | AllocationPattern | — | **AMBER** | ~8 of 12 | §9.30 — three enums; the tier changes the *algorithm* |
| 31 | WeakReference | — | **GREEN** | 2 of 5 | §9.31 — `HandleScanCap` truncates the handle table, not a derived list |
| 32 | DbConnection | typed-resource | **GREEN** | 1 of 2 | §9.32-9.34 — **no options class at all**; bounds are `private const`, never preset-varied |
| 33 | WcfChannel | typed-resource | **GREEN** | 1 of 2 | §9.33 — same shape; caps faulted-channel detection specifically |
| 34 | HttpObject | typed-resource | **GREEN** | 1 of 2 | §9.34 — headline counts already exact; only the drill-down sample is capped |
| 35 | LeakCandidate | typed-resource | **GREEN** | 1 of 1 | §9.35 — already index-backed and exact; a template for "built right from the start" |

> Rows 4 and 22 are cross-references, not distinct analyzers (roster correction, top of document) —
> **33 real rows for 33 registered analyzers.**

---

## 8. Residual profile-only cleanup (not emergent from the audit)

These do not fall out of any analyzer's exactness work and must be done explicitly, after the audit
retires the per-analyzer `Preset()` methods.

1. **Delete the dead duplicate parser** — `ConfigurationResolver.ParseAnalysisProfile`
   ([:608-621](../../src/DumpDetective.Cli/Configuration/ConfigurationResolver.cs#L608-L621)).
   Unreferenced; `ResolveAnalyzerProfile:510` calls the `ConfigurationParseHelpers` copy. Safe today,
   independent of everything else.
2. **Delete the `Deep = Full` enum alias** ([CollectionAnalysisOptions.cs:10](../../src/DumpDetective.Core/Options/CollectionAnalysisOptions.cs#L10)) —
   zero references in `src`, `tests`, `docs`. Keep the `"deep"` *string* arm for config back-compat
   until step 6.
3. **`CollectionAnalyzer.cs:1154`** reads `options.Profile == AnalysisProfile.Fast` — the only place
   the profile escapes configuration into runtime logic. Replace with `options.PathAnalysisTopN <= 0`
   (equivalent: the Fast preset already sets it to 0, nothing else does). **Own commit** — it is the
   only change here that rewrites a predicate rather than deleting a factory.
4. **Collapse the resolver plumbing** — drop `Func<AnalysisProfile, T>` from
   `BuildAnalyzerOptionsFromConfig` ([:588](../../src/DumpDetective.Cli/Configuration/ConfigurationResolver.cs#L588)),
   delete `ResolveAnalyzerProfile` (:509-512) and `GetAnalyzerProfile` (:532-545).
5. **Delete the typed `Profile` keys** on `CollectionAnalysisOptionsModel` (:193) and
   `CrashAnalysisOptionsModel` (:114), plus the `Profile` property on `CollectionAnalysisOptions`
   (:66) and its propagations at :154 and :177.
6. **Delete the enum, `ParseAnalysisProfile`, and `CliConfigurationFileModel.Profile`** (:16). A
   legacy `Profile` key in a user config should emit a deprecation warning naming the replacement,
   not throw.
7. **`RetentionOptions.cs:51`** carries `<see cref="AnalysisProfile"/>` in an XML comment — a
   compile-time cref that **breaks the build** if the enum is deleted first. Rewrite the sentence.
8. **Delete `tests/.../Unit/Analysis/PresetBehaviorTests.cs`** and the preset-comparison assertions in
   `AllocationPatternAnalyzerTests` (:68-85), `StringAnalyzerOptionsTests` (:13-27),
   `ThreadAnalysisOptionsTests` (:12-38), `WeakReferenceOptionsTests` (:12-34),
   `ThreadStackClusterAnalyzerOptionsTests` (:15). Replace `Preset(Balanced)` with `X.Default` in
   `ConfigurationResolverTests` (:129-159); delete the `Collection.Profile` assertions (:261, :289).
   These test a relationship between tiers; with no tiers there is nothing to preserve.
9. **Update the five docs** referencing `AnalysisProfile`: `phase1/allocation-pattern-analyzer-audit.md`,
   `phase1/crash-analyzer-audit.md`, `phase1-redesigns/dominator-tree-implementation-plan.md`,
   `phase1-redesigns/dominator-tree-lengauer-tarjan.md`, `analysis/root-path-search-blast-radius.md`.

**Done-check** — all must return zero:

```bash
grep -rn "AnalysisProfile"        src tests docs
grep -rn "Preset(AnalysisProfile" src tests
grep -rn "ParseAnalysisProfile"   src tests
grep -rn "ResolveAnalyzerProfile\|GetAnalyzerProfile" src
grep -rn "options.Profile"        src
```

---

## 9. Per-analyzer audits

### 9.1 Boxing — **GREEN** ✅ IMPLEMENTED

Source: [BoxingAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs) ·
[BoxingAnalysisOptions.cs](../../src/DumpDetective.Core/Options/BoxingAnalysisOptions.cs)

**Q1 — knob categories**

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TypeScanCap` | 10,000 | 3 — wall-clock | delete |
| `TopBoxedTypeLimit` | 20 | 1 — rows | move to render |
| `TopPaddingLimit` | 20 | 1 — rows | move to render |
| `TopOversizedTypeLimit` | 20 | 1 — rows | move to render |
| `OversizedThresholdBytes` | 64 | 5 — semantic | **keep** |

**Q2 — asymptotic class.** O(distinct types), *not* O(objects). The analyzer never touches an object:
every count and byte total comes pre-aggregated from `HeapIndexBuildResult.TypeAggregates`. Per type
it does one `heap.GetTypeByMethodTable` (cached) and, for value types, one field enumeration.
Unbounding does not change the class.

**Q3 — materialization.** Four structures, all O(distinct types): `boxedByTypeName`,
`paddingCandidates`, `oversizedByTypeName`, and the `ordered` list at :87. Tens of thousands of small
entries — single-digit MB. Nothing per-object. **No memory risk.**

**Q4 — superseding structure.** None needed; `TypeAggregates` is already the exact source.

**Q5 — cost.** One pass over `typeAggregates` (~50-100k entries at 25 GB scale), one cached ClrMD
lookup each. Seconds against a 600 s nominal budget. **Negligible.**

**Q6 — reverse index.** Not used. Not gated on `MaxParentsPerChild`.

#### The cap bites today

`TypeScanCap = 10,000` bounds **distinct types**, and a large .NET service routinely loads more than
that. [:83](../../src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs#L83) sets
`scanCapped = typeAggregates.Count > TypeScanCap` and the result carries a `TypeScanCapped` flag —
the analyzer already reports that it truncated. This is not a hypothetical cap; it is one that fires
and silently under-reports `TotalBoxedObjects` on exactly the large dumps the tool targets.

#### Deleting the cap also deletes a bug class

[:78-94](../../src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs#L78-L94) exists **solely** to
make the truncation deterministic. The comment records the original defect: dictionary iteration
order varies with parallel segment-merge order, so capping on raw order truncated to a different
arbitrary subset of types on every run, making `TotalBoxedObjects` non-deterministic. The fix was to
sort by `TotalSize` descending before capping.

Remove the cap and the entire sort, the branch, and the 17-line comment go with it. **A workaround
for a truncation artifact stops being needed when the truncation stops happening.**

Note also that `AggregatePaddingWasteBytes` is already computed across *all* padding candidates
([:203-208](../../src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs#L203-L208)), not just the
reported top 20 — the analyzer already holds the complete set and truncates only on output. That is
the Category 1 move already half-done, and confirms it is natural here.

#### Work items

1. Delete `TypeScanCap` and the :78-94 determinism sort.
2. `TopBoxedTypes`, `TopPaddingWasteTypes`, `TopOversizedTypes` become complete ranked lists in
   `BoxingDomainResult`; the three `Top*Limit` knobs move to the render layer.
3. `BoxingAnalysisOptions` retains only `OversizedThresholdBytes`; `Preset` and `Default` collapse.
4. **Schema check:** `TypeScanCapped` and `TypeScanCapUsed` become permanently false/meaningless.
   Confirm whether `BoxingDomainResult` reaches the serialized report; if it does, removing them is a
   JSON schema change requiring a version bump per
   [schema-versioning.md](../schema-versioning.md).

#### Implementation notes (as shipped)

- **Schema check resolved as D2 predicted:** `BoxingDomainResult` never reaches JSON — confirmed no
  version bump needed, `TypeScanCapped`/`TypeScanCapUsed` were deleted outright.
- **`BoxingSectionBuilder` had a second, independent truncation** beyond the analyzer cap: it built
  `CompactTable` rows from `d.TopBoxedTypes.Take(TopTypesToShow)` (and similarly for padding waste),
  plus a "N additional type(s) omitted" text block — a Mechanism-1/Mechanism-2 mix per §11.2 D5. Fixed
  by feeding the *full* list into `STCompact` and passing the old `TopTypesToShow`/`TopPaddingToShow`
  constants as `STCompact`'s `rowLimit` (the initial page size), not a hard cutoff — no more omitted
  text block needed since nothing is actually dropped. **Correction (D5 amendment, post-§9.7):** those
  two constants and their `rowLimit` arguments were later removed entirely in favor of `STCompact`'s
  uniform default — see D5's amendment note.
- **Trend comparer — got this wrong once, corrected.** First pass added a `TopTypeMetricLimit = 50`
  cap in `BoxingTrendComparer.ExtractMetrics` to bound per-type trend-metric volume now that
  `TopBoxedTypes` is unbounded. Reverted after review: per §11.2 D5, the right shape is *full data at
  the model layer, paginate only at render* — capping inside the comparer reintroduces exactly the
  kind of silent truncation this whole effort is removing. `BoxingTrendComparer` now emits one
  `boxing.type.bytes`/`boxing.type.count` metric pair per boxed type with no limit. Generalize this
  lesson to every other trend comparer touched later in §9: don't add a "reasonable-sounding" cap at
  the comparer/analyzer boundary to solve a display-volume concern — that's a render-layer job
  (`TrendMetricTimelineSectionBuilder`'s `TableBlock` output doesn't paginate today, which is a
  pre-existing gap in the render layer, not a reason to cap the data feeding it).
- **`ConfigurationResolver` wiring:** the generic `BuildAnalyzerOptionsFromConfig<T>(..., Func<AnalysisProfile,T> createPreset)` helper assumes every options type still has a profile-based `Preset`. With `BoxingAnalysisOptions.Preset` deleted, `BuildBoxingAnalysisFromConfig` was rewritten as a
  one-off: apply JSON section overrides (or legacy `config.BoxingAnalysis`) on top of
  `new BoxingAnalysisOptions()` directly, bypassing profile resolution entirely. The CLI-flags-only
  fallback path (`AnalyzerOptionsBuilder.BuildBalancedPresetFromCli`) was likewise replaced with
  `_ => new BoxingAnalysisOptions()`. This is the pattern every other analyzer whose options class
  loses its `Preset` will need (see §9.2, §9.3 below).

### 9.2 ObjectShape — **GREEN** ✅ IMPLEMENTED

Source: [ObjectShapeAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs) ·
[ObjectShapeAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ObjectShapeAnalysisOptions.cs)

**Q1 — knob categories**

| Knob | Default | Category | Action |
|---|---:|---|---|
| `InstanceCountCap` | 200 | 3 — wall-clock | delete |
| `TopListLimit` | 20 | 1 — rows | move to render |

Nothing survives. `ObjectShapeAnalysisOptions` is deleted outright, not reduced.

**`InstanceCountCap` is misnamed.** It caps neither instances nor instance counts — it takes the
**top 200 types by instance count**
([:59-61](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs#L59-L61)) to bound
`ClrType` metadata lookups. The class XML doc states this correctly; the option name does not.

**Q2 — asymptotic class.** O(distinct types). No heap enumeration at all — the class doc opens
*"Pure Phase-2 type-metadata analyzer: no heap object enumeration."* Per surviving type it does one
cached `GetTypeByMethodTable`, one `ComputeBaseTypeDepth` walk (already depth-capped at 20 against
cycles — that is a correctness guard, not a budget, and stays), and one
`EnumerateInterfaces().Count()`.

**Q3 — materialization.** The `candidates` list at
[:52](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs#L52) is built at
`shapes.Count` capacity **before** the cap applies, so it is already O(distinct types) today —
removing the cap does not change it. The cap only bounds how many `TypeShapeProfile` records get
built: ~13 fields plus a type-name string, so 100k types is tens of MB. **No memory risk.**

> This corrects my earlier flag that `InstanceCountCap` might be memory-bound. It is not; it is a
> wall-clock bound on ClrMD metadata calls.

**Q4 — superseding structure.** None needed; `TypeShapeCache` and `TypeAggregates` are already exact.

**Q5 — cost.** One pass over types present in both caches, three cached ClrMD calls each. Seconds.

**Q6 — reverse index.** Not used.

#### The cap corrupts three aggregates, not just the lists

`typesAnalyzed`, `totalRefFields` and `totalGcScanWork` are all accumulated **inside** the capped
loop ([:82-84](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs#L82-L84)). So
`TotalGcScanWork` — which reads as a whole-heap GC-cost metric and is the analyzer's headline
output — is actually *the GC scan work of the top 200 types*. `AvgRefFieldsPerType` is likewise an
average over 200 types, not over the heap's types.

This is the same class of defect as Boxing's `TotalBoxedObjects` (§9.1): a cap intended to bound
report width silently redefining a total. Unlike the row limits, no renderer change can fix it —
the wrong number is computed in the analyzer.

#### Work items

1. Delete `InstanceCountCap` and the sort/cap at :59-61. The loop iterates all candidates.
2. `TopReferenceHeavyTypes` / `TopValueHeavyTypes` / `TopBalancedTypes` become complete ranked lists;
   `TopListLimit` moves to the render layer. Note :139-141 uses `.Take().ToList()` — replace with
   explicit slicing at the render layer per the no-LINQ rule.
3. Delete `ObjectShapeAnalysisOptions` entirely.
4. **Schema check:** `InstanceCountCap` is a field on `ObjectShapeAnalyzerDomainResult`
   ([:44, :152](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs#L152)). Same
   version-bump question as §9.1 item 4.

#### Implementation notes (as shipped)

- **This was the first "delete the whole options class" case**, and it cascades further than §9.1's
  "options survive with fewer fields" shape: `ObjectShapeAnalysisOptions` had to come out of
  `AnalysisOptions`, `CliConfigurationModels` (property *and* its `[JsonSerializable]` roster entry),
  `ConfigurationResolver` (builder method + call site), `AnalyzerExecutionService`, and
  `ResolvedExecutionOptions` — plus three test call sites that constructed
  `ResolvedExecutionOptions`/`ObjectShapeAnalyzerDomainResult` positionally
  (`ResolvedExecutionOptionsFactory`, `StartupValidatorTests`, `ReportingCompositionTests`).
  `ObjectShapeAnalyzer.AnalyzeAsync`/`Analyze` no longer take an options parameter at all — there was
  nothing left to configure once both knobs were gone. **Any other §9 row marked "options class
  deleted outright" (LockGraph §9.7, AsyncTask §9.29, WcfChannel/HttpObject/DbConnection §9.32-34)
  should expect this same wiring surface, not just a file deletion.**
- **§10's D5 pagination point didn't need any work here** — `ObjectShapeSectionBuilder` was already
  passing full `TopReferenceHeavyTypes`/`TopValueHeavyTypes`/`TopBalancedTypes` lists into
  `STCompact` with no `.Take()`, unlike Boxing (§9.1). The only render-layer change was deleting the
  now-false "(Avg ref fields is computed over at most N types…)" caveat sentence.
- **No dedicated `ObjectShapeAnalyzer` unit tests existed** to update — the only test-side fallout was
  the positional-constructor breakage above, not behavioral test rewrites.

---

### 9.3 Module — **GREEN** ✅ IMPLEMENTED (with the largest cascade so far)

Source: [ModuleAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs) ·
[ModuleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ModuleAnalysisOptions.cs)

**Q1 — knob categories**

| Knob | Default | Category | Action |
|---|---:|---|---|
| `ModuleEnumerationLimit` | 50 | 3 — wall-clock | **delete** (drives the cascade) |
| `TypeEnumerationMode` | `Full` | 2 — sampling | delete enum; always full |
| `ModuleSelectionMode` | `TopBySize` | — **dead** | delete |
| `IncludeExcludedModuleSummary` | false | — **dead** | delete |
| `EmitTruncationNotice` | false | — cascade | delete |
| `PreferIndexOnly` | true | fallback policy | hard-code the behaviour, delete the knob |
| `TopLoadedAssembliesCount` | 30 | 1 — rows | move to render |
| `TopModulesByHeapCount` | 20 | 1 — rows | move to render |
| `TopModuleTypeCountLimit` | 20 | 1 — rows | move to render |
| `HeavyModuleWarningThresholdBytes` | 200 MB | 5 — semantic | **keep** |
| `DensityAnomalyMinBytes` | 50 MB | 5 — semantic | **keep** |
| `DensityAnomalyMaxTypes` | 5 | 5 — semantic | **keep** |

#### Two knobs are dead code today

`ModuleSelectionMode` and `IncludeExcludedModuleSummary` are **never read anywhere in `src`**. Both
are set to three different values across the three presets and consumed by nothing. The module sort
at [:136-139](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L136-L139) is
unconditional, and its comment — *"both selection modes benefit from processing largest modules
first"* — records why the mode stopped mattering without the option being removed.

This is worth noting as a general signal: an options surface nobody can see through is one where
dead knobs survive indefinitely.

#### The cascade

Deleting `ModuleEnumerationLimit` removes, by consequence:

- `EmitTruncationNotice` — its only use is the truncation warning at
  [:145](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L145), which can no longer fire.
- `totalExcludedModules` / `ExcludedModuleCount` — nothing is excluded.
- `ModuleSelectionMode` — existed only to choose *which* modules survive the limit.
- The per-domain `Array.Sort` at :137-139 — only needed to make the truncation deterministic, the
  same pattern as Boxing's §9.1 determinism sort.

Deleting `TypeEnumerationMode`'s `Sampled` and `Skip` leaves only `Full`, so the enum and the
hard-coded `SampleBudgetPerModule = 1024`
([:184](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L184)) go too. **One deletion
retires four knobs and two enums.**

#### What the caps corrupt

- **`EstimatedManagedBytes` per domain** accumulates only over the enumerated modules
  ([:199](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L199)), so any domain with
  more than 50 modules reports a truncated byte total. The field name says "Estimated," but the
  inaccuracy is truncation, not estimation.
- **`Sampled` mode is a prefix, not a sample.** It takes the first 1024 entries of
  `EnumerateTypeDefToMethodTableMap()` and breaks
  ([:203-204](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L203-L204)). That is
  biased by metadata token order rather than uniformly sampled, so `TypeCount`, `LiveTypeCount`,
  `ObjectCount` and `TotalBytes` for large modules are not merely imprecise, they are systematically
  skewed toward whatever the compiler emitted first.

#### `PreferIndexOnly` is not a thoroughness knob

It means: when no `TypeAggregates` index exists, skip type enumeration entirely, because
`LiveTypeCount`/`ObjectCount`/`TotalBytes` would all be empty anyway
([:116-119](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L116-L119),
[:180](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs#L180)). That is a correct
degraded-path policy, not a tier. In the real pipeline Phase 1 always builds the index, so it never
fires. **Keep the behaviour, hard-code it, delete the option.**

#### Latent trap this fixes

`new ModuleAnalysisOptions()` and `ModuleAnalysisOptions.Default` **disagree**. The property
initializers set `IncludeExcludedModuleSummary = false` (:61) and `EmitTruncationNotice = false`
(:66), while the Balanced arm sets both to `true` (:109-110). Two things both called "the default"
produce different behaviour depending on which one a caller reaches for. Promoting Balanced into the
initializers (§2 commit 1) resolves it; the knobs then disappear entirely.

**Q2 — asymptotic class.** O(modules x types per module) over ClrMD metadata. No heap enumeration.

**Q3 — materialization.** `moduleTypeData` is O(distinct modules) — hundreds. `moduleEntries` the
same. **No memory risk.**

**Q5 — cost, measured (M1, §11.4).** Full `EnumerateTypeDefToMethodTableMap()` across all modules in
all domains. The Full preset's comment ([:92](../../src/DumpDetective.Core/Options/ModuleAnalysisOptions.cs#L92))
warns *"the analysis time grows quickly with the number of modules,"* but on a real 3.35GB dump with
266 modules (already past the Balanced cap of 50), fully uncapped `ModuleAnalyzer.AnalyzeAsync` ran in
178 ms — actually faster than the capped run's 506 ms (almost certainly warm-cache ordering noise, not
a real negative cost). Confirmed: metadata-table iteration, not heap work, and negligible against the
~10-minute nominal budget. Safe to delete `ModuleEnumerationLimit`.

**Q6 — reverse index.** Not used.

#### Implementation notes (as shipped)

- **The per-domain `Array.Sort` by module size was kept, not deleted**, despite the plan text above
  framing it as existing "only to make the truncation deterministic." On inspection it does double
  duty: it also drives `AppDomainSnapshot.TopModules` — a hard-coded top-8-by-size narrative list per
  domain, unrelated to `ModuleEnumerationLimit`. Deleting the sort would have made that list
  enumeration-order-dependent instead of size-ranked. Kept the sort, renamed its comment to describe
  the surviving purpose, and dropped only the truncation-driven pieces (`enumerationBound`,
  `totalExcludedModules`, the conditional truncation warning). The `8` itself stays a local constant
  per §11.2 D5 ("true inline prose lists embedded in a sentence... can stay small analyzer-local
  constants").
- **`PreferIndexOnly`'s hard-coded replacement is a plain `hasIndex` check**, not `hasIndex || !true`
  simplified by hand — same behavior, clearer code: `if (!hasIndex) continue;` before the per-module
  type-enumeration loop, with the domain-level warning text reworded to drop the now-nonexistent
  setting name.
- **Deleted `tests/.../ModuleAnalyzerUncappedRealDumpTests.cs` outright.** That test existed
  specifically to A/B-measure `ModuleEnumerationLimit`'s capped-vs-uncapped cost — the exact
  measurement recorded here as M1. Once the cap is gone there is only one code path left to run, so
  the "capped baseline" side of the comparison no longer compiles or means anything; the measurement
  it produced is preserved in this doc's M1 entry (§11.4), which is the artifact that actually
  justified the deletion.
- **Cleaned the stale example out of `src/DumpDetective.Cli/config.json`** — its commented-out
  `"Module": { "Profile": ..., "TopLoadedAssembliesCount": ..., "TopModulesByHeapCount": ... }` block
  referenced three names that no longer exist. Left the other commented analyzer examples
  (Crash/Collection/String/GCRoot) alone since those analyzers haven't been migrated yet.
- **Same `ConfigurationResolver` profile-bypass pattern as §9.1's Boxing notes** — `Preset` deleted,
  so `BuildModuleAnalysisFromConfig` applies overrides directly onto `new ModuleAnalysisOptions()`.
- **`ModuleAggregator.Aggregate` still takes `ModuleAnalysisOptions`** (for the surviving
  `DensityAnomalyMinBytes`/`DensityAnomalyMaxTypes` thresholds) — only its `TopModulesByHeapCount`
  cap was removed; `ModuleSectionBuilder` already rendered that list via `STCompact` with no
  additional `.Take()`, so no render-layer change was needed there either.

### 9.4 GCGeneration — **GREEN** ✅ IMPLEMENTED

[GCGenerationAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs) ·
[GCGenerationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCGenerationAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TopLohTypeLimit` | 15 | 1 — rows | move to render |
| `TopGenProfileLimit` | 20 | 1 — rows | move to render |
| `LohThresholdPercent` | 20.0 | 5 | keep |
| `Gen0PressureThresholdPercent` | 40.0 | 5 | keep |
| `PohThresholdPercent` | 5.0 | 5 | flagged dead by V4 (nothing thresholded against it at audit time) — **as shipped: kept, wired up to a new POH-share finding instead of deleted; see implementation notes** |

The preset varies only the two row limits, so `Preset` dies and the three thresholds move to
initializers unchanged.

**Q7 — nothing corrupted.** Both limits are applied as `Math.Min(limit, candidates.Count)` after
candidate collection is complete ([:90](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs#L90),
[:104](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs#L104),
[:174](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs#L174)). This is the clean
Category 1 shape — cosmetic truncation only. First analyzer audited where the answer to Q7 is "none."

**Precedent worth noting.** The class XML doc records that `LohThresholdBytes` was previously removed
because it was *"defined but never applied… a correctness trap where users could configure a setting
with no effect."* The codebase has already found and fixed one dead knob by hand, which is exactly
what §5's procedural grep step systematises.

#### Implementation notes (as shipped)

- **Both analyzer paths capped independently** (`BuildFromIndex` and the no-index fallback
  `BuildFromTypeStatistics`), each with its own `Math.Min(options.Top*Limit, candidates.Count)` +
  indexed loop. Converted both to `foreach` over the full sorted list — no shared helper introduced,
  since the two paths build different snapshot types (`TypeAggregateIndexEntry`-backed vs
  `CachedTypeStatistics`-backed) and forcing a shared abstraction here would be more machinery than
  the duplication justifies.
- **`GCPressureSectionBuilder` had the same double-truncation shape as Boxing's §9.1**: analyzer-side
  caps *and* independent render-side `Math.Min(..., 15)`/`Math.Min(..., 30)` re-slicing on top of the
  (already capped) domain lists. Fixed the same way — feed the full list into `STCompact`, pass the
  old local constants (`TopLohTypesToShow = 15`, `TopGenProfilesToShow = 30`) as the `rowLimit`
  (initial page size) instead of a hard slice. Three call sites needed this: the `PerTypeGenerationProfiles`-derived "Top LOH types" table, the `TopLohTypes`-fallback "Top LOH types" table, and
  the "Per-type generation profiles" table. **Correction (D5 amendment, post-§9.7):** both constants
  and their `rowLimit` arguments were later removed in favor of `STCompact`'s uniform default.
  **Known pre-existing accuracy gap, not touched here:** when `PerTypeGenerationProfiles` is
  available, "Top LOH types" is built by filtering that list (ranked by *instance count*) down to
  entries with `LohCount > 0` — a materially different, weaker ranking than `TopLohTypes` (ranked
  directly by *LOH byte size*, the fallback path's source). A type with huge LOH bytes but low overall
  instance count could rank in `TopLohTypes` but be excluded from the `PerTypeGenerationProfiles`-derived table's candidate pool. This predates the exactness work and isn't a truncation defect — the
  candidate pool itself is now unbounded — but is worth a dedicated follow-up: consider sourcing "Top LOH types" from `TopLohTypes` unconditionally rather than switching sources based on which list happens to be non-empty.
- **`GCGenerationTrendComparer` needed no change** — it already iterated the full `TopLohTypes` list
  with no local cap, consistent with §9.1's "don't cap at the comparer" correction.
- Same `ConfigurationResolver` profile-bypass pattern as §9.1/§9.3 — `Preset` deleted, so
  `BuildGCGenerationAnalysisFromConfig` applies overrides directly onto `new GCGenerationAnalysisOptions()`.
- **Correction from initial implementation: `PohThresholdPercent` was wired up instead of deleted.**
  V4 (§11.3) flagged it dead because `GCGenerationAnalyzer.cs` computed `PohBytes`/`PohObjects` but
  never gated a finding on the threshold — that's a real gap, not evidence the knob should go away.
  Deleting a semantically-meaningful threshold "because nothing reads it yet" reproduces the exact
  defect this whole effort exists to fix (a config value with no effect), just one step earlier in the
  knob's life — the fix for dead-but-meaningful is to finish wiring it, not remove it. Added a POH-share
  `InsightFinding` to `GCGenerationFindingGenerator` (mirrors the existing LOH-share finding: emits
  Info/Warning based on `PohThresholdPercent`/a 20% escalation line, evidence includes POH bytes and
  object count, recommendation points at interop/pinning/`Span<T>` usage). `PohThresholdPercent` is
  back on `GCGenerationAnalysisOptions` and threaded through `GCGenerationDomainResult` from both
  analyzer paths. **General lesson for the rest of §9: a knob found dead by V4 needs the analyzer/finding-generator checked for "should this be wired up" before defaulting to deletion** — V4's grep only
  proves *nothing reads it today*, not that it's semantically vestigial the way `ModuleSelectionMode`
  or `IncludeExcludedModuleSummary` are.

---

### 9.5 GCHandle — **GREEN** ✅ IMPLEMENTED

[GCHandleAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCHandleAnalyzer.cs) ·
[GCHandleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCHandleAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TopTypeCount` | 15 | 1 — rows | move to render |
| `TotalHandlesWarningThreshold` | 10,000 | 5 | keep |
| `PinnedHandleTargetsWarningThreshold` | 1,000 | 5 | keep |
| `PinnedRetainedBytesWarningThreshold` | 100 MB | 5 | keep |
| `DependentUnresolvedPercentWarningThreshold` | 50.0 | 5 | keep |

**Q7 — nothing corrupted.** `TopTypeCount` feeds six `ToTopEntries` / `ToTopByteEntries` calls at
[:226-232](../../src/DumpDetective.Analysis/Analyzers/GCHandleAnalyzer.cs#L226-L232), all after
aggregation. Pure output slicing — but note the same limit governs six independent lists, so the
render layer needs six separate display limits or one shared, a decision to make when building the
Category 1 mechanism (§10).

This analyzer also handles dependent handles (hence
`DependentUnresolvedPercentWarningThreshold`), which is relevant to §9.6.

#### Implementation notes (as shipped)

- **Renamed `ToTopEntries`/`ToTopByteEntries` to `ToRankedEntries`/`ToRankedByteEntries`** and dropped
  the `take` parameter — each now builds the full sorted list from its source `Dictionary`, replacing
  the `.OrderByDescending(...).Take(take)` LINQ with an explicit `List<T>.Sort` per the no-LINQ-in-hot-paths
  rule (the LINQ chain here wasn't itself hot-path-relevant at ~thousands of distinct handle target
  types, but there's no reason to keep it once touching the call site anyway).
- **`GCHandleSectionBuilder` needed no data-flow change** — its seven `STCompact` calls already
  rendered whatever `IReadOnlyList` the domain result gave them with no additional `.Take()`.
  **Correction (D5 amendment, post-§9.7):** initially added an explicit `TopTypesToShow = 15`
  `rowLimit` to each call (matching the deleted option's old default); removed again once D5 was
  amended to drop custom `rowLimit`s in favor of `STCompact`'s uniform default everywhere.
- **Second, independent defect found and fixed while touching this analyzer — the same "V4 grep says
  dead, but the real bug is a wiring gap" shape as §9.4's `PohThresholdPercent`, but one layer deeper.**
  `GCHandleFindingGenerator` did not read `GCHandleAnalysisOptions.TotalHandlesWarningThreshold`/
  `PinnedHandleTargetsWarningThreshold`/`PinnedRetainedBytesWarningThreshold`/
  `DependentUnresolvedPercentWarningThreshold` at all — it declared its own same-named `{ get; init; }`
  properties with independently hardcoded defaults, and nothing ever set them from the resolved
  options (finding generators are constructed once via the module catalog and only ever see
  `AnalyzerDomainResult` in `Generate`, not `AnalysisContext`/`AnalysisOptions` — there's no injection
  path from resolved CLI/config options to a finding generator's properties today). So these four
  "keep, semantic" thresholds were fully plumbed through config/CLI, reached `GCHandleAnalysisOptions`
  correctly, and then went nowhere — configuring them in a config file did nothing, silently, exactly
  the failure mode this whole doc exists to eliminate. **Fixed using the pattern `GCGenerationDomainResult`
  already established successfully**: added the four thresholds to `GCHandleDomainResult` (populated by
  `GCHandleAnalyzer` from `options`), and changed `GCHandleFindingGenerator` to read `r.<Threshold>`
  instead of its own disconnected copies. **General lesson, now confirmed twice: before trusting a
  "keep, semantic threshold" audit row, check that the finding generator (not just the analyzer) actually
  reads it — a threshold can be alive in the analyzer's options class and still be practically dead
  because the consumer reads a different, unconnected copy of the same value.**
- Same `ConfigurationResolver` profile-bypass pattern as prior sections — `Preset` deleted, so
  `BuildGCHandleAnalysisFromConfig` applies overrides directly onto `new GCHandleAnalysisOptions()`.

---


### 9.6 DependentHandle options — **GREEN** ✅ IMPLEMENTED, and the class looks orphaned

[DependentHandleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/DependentHandleAnalysisOptions.cs)

**Not a registered analyzer** — `DependentHandleAnalysisOptions` does not correspond to any entry in
the 33-module catalog (see the roster correction near the top of this document). Kept as a note
under GCHandle (§9.5, which handles dependent-handle *analysis*) rather than its own numbered row.

One knob, `TopCount = 15`, varied 8 / 15 / 40 across the three presets.

**No analyzer reads it.** `DependentHandleAnalysisOptions` appears in exactly five files, all of them
configuration plumbing:

- `Core/Options/DependentHandleAnalysisOptions.cs` (itself)
- `Core/Options/AnalysisOptions.cs`
- `Cli/Configuration/CliConfigurationModels.cs`
- `Cli/Configuration/ConfigurationResolver.cs`
- `Cli/Models/ResolvedExecutionOptions.cs`

There is no `DependentHandleAnalyzer.cs`, and nothing in `DumpDetective.Analysis` references the
type. Dependent-handle analysis is performed by `GCHandleAnalyzer` using `GCHandleAnalysisOptions`
(§9.5).

So this is a fully plumbed, config-file-exposed, preset-varied, CLI-resolvable option that **no code
consumes** — the most complete instance yet of the failure mode §5's dead-knob grep exists to catch.
Setting it in a config file does nothing and reports no error.

**Confirm before deleting:** verify no reflection-based or serialization-based consumer exists, and
check whether a `DependentHandleAnalyzer` was planned rather than removed. If it was planned, the
options class is dead *today* regardless and should be reintroduced with the analyzer rather than
kept as a placeholder.

**Work item:** delete the class and its four plumbing references. Removing it from
`ResolvedExecutionOptions` / `AnalysisOptions` may be a schema-visible change — apply the §9.1 item 4
check.

#### Implementation notes (as shipped)

- **Confirmed orphaned exactly as audited** — no reflection/serialization consumer, no planned-but-missing
  analyzer found. Deleted `DependentHandleAnalysisOptions.cs` outright and its five plumbing references
  (`AnalysisOptions`, `CliConfigurationModels` property + `[JsonSerializable]` entry,
  `ConfigurationResolver` builder method + call site + constructor argument, `ResolvedExecutionOptions`,
  `AnalyzerExecutionService`) plus two test call sites (`ResolvedExecutionOptionsFactory`,
  `StartupValidatorTests`). No schema-bump concern — same D2 reasoning as §9.1/§9.2 (never reaches the
  JSON report surface).

---

### 9.7 LockGraph — **GREEN** ✅ IMPLEMENTED

[LockGraphAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs) ·
[LockGraphAnalysisOptions.cs](../../src/DumpDetective.Core/Options/LockGraphAnalysisOptions.cs)

Single knob `MaxContestedLocksToShow = 15`, Category 1. The options class is deleted outright.

**Q7 — nothing corrupted.** Applied at
[:61, :68, :71-72](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs#L68) after the
lock graph is built. The name is honest for once: `…ToShow`.

**Q8 — minor.** [:68](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs#L68) uses
`.OrderByDescending(…).Take(…)`; moving the limit to the render layer is the moment to replace the
LINQ per the project's no-LINQ-in-hot-paths rule.

#### Implementation notes (as shipped)

- **Two independent lists were capped by the same knob, not one** — `topContestedTypes` (per-type
  cumulative-waiter aggregation) and `contestedLockDetails` (per-lock-object detail rows) each had
  their own `Take`/`Math.Min(..., options.MaxContestedLocksToShow)` call. Both now return the full set;
  `topContestedTypes` replaced `.OrderByDescending(...).Take(...)` with a `List<T>.Sort` per Q8.
  `ContestedLockCount`/`MaxWaitersOnSingleLock` were already computed from the uncapped
  `graph.ContestedLocks` list before this change, so nothing needed fixing there (Q7 was right).
- **`LockGraphSectionBuilder` had its own separate, unrelated local cap** — `Math.Min(topTypes.Count, 8)`
  for the "Top contested lock types" table, independent of `MaxContestedLocksToShow` (15). This is the
  same shape flagged by D5 for `GCPressureSectionBuilder`/`BoxingSectionBuilder`: render-side hard
  slicing stacked on top of an analyzer-side cap, two different limits governing the same data with no
  relationship to each other. Fixed by feeding the full list into `STCompact` instead of a hard cutoff.
  `ContestedLockDetails`'s own `STCompact` call already rendered the full list with no `.Take()`, so it
  needed no change beyond what the analyzer fix already gave it. **Note (D5 amendment, post-§9.7):**
  the old local `8` was briefly carried forward as an explicit `rowLimit` before being dropped in favor
  of `STCompact`'s uniform default, per this discussion's outcome — this section was in fact the trigger
  for that amendment.
- **Options class deleted outright, same as §9.2 ObjectShape** — `LockGraphAnalyzer` no longer takes
  an options parameter at all. Removed from `AnalysisOptions`, `CliConfigurationModels` (property +
  `[JsonSerializable]` entry), `ConfigurationResolver` (builder method + call site + constructor
  argument), `ResolvedExecutionOptions`, `AnalyzerExecutionService`, and two test call sites
  (`ResolvedExecutionOptionsFactory`, `StartupValidatorTests`).

---

### 9.8 LohFragmentation — **GREEN** ✅ IMPLEMENTED

[LohFragmentationAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs) ·
[LohFragmentationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/LohFragmentationAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TopSegments` | 10 | 1 — rows | move to render |
| `TopLargeObjectsCount` | 20 | 1 **and** 3 | move to render; see below |

Options class deleted outright.

**Q7 — `TopLargeObjectsCount` truncates an aggregation, not just a list.** `TopSegments` is clean
output slicing ([:148](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L148),
[:338](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L338)), but
`TopLargeObjectsCount` is applied **inside** the `LargeObjectTracker.ReadRecords` callback at
[:345](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L345):

```csharp
if (topLargeObjects.Count >= options.TopLargeObjectsCount) return;
```

The callback also populates `typeAggregation` (a `Dictionary<string,(int Count, ulong TotalBytes)>`),
and the guard returns before reaching it. So the per-type aggregation of large objects is truncated
to whatever the first 20 records happened to be, in index order.

**Q8 — the cap barely saves anything.** The early return is inside the callback, so
`ReadRecords` still reads every record in `LargeObjectIndex.bin`; the cap only skips the ClrMD
`GetTypeByMethodTable` resolution. Removing it costs one cached metadata lookup per large object.

**Doc drift:** the comment at
[:340](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L340) says *"resolve
type names (≤ 100 objects)"* while the effective default is 20.

#### Implementation notes (as shipped)

- **`AnalyzeFromIndex`'s early-return bug was worse than Q7 described — it wasn't just truncating
  `typeAggregation`, the surviving `topLargeObjects` list was never sorted by size at all.** The
  heap-scan fallback path (`AnalyzeFromHeap`) sorts `largeObjectCandidates` by size before building
  snapshots; the index fast path built `topLargeObjects` straight from `LargeObjectTracker.ReadRecords`
  callback order (`LargeObjectIndex.bin`'s on-disk order) with no sort call anywhere afterward. Once
  the cap capped the list at "however many records satisfied it first," this went unnoticed because
  20 items in mostly-arbitrary order can still look plausible in a report; a fully unbounded list in
  file order would have been an obviously-wrong "Top large objects" table. **Fixed by adding
  `topLargeObjects.Sort(static (a, b) => b.Size.CompareTo(a.Size))` after the `ReadRecords` loop** —
  this was a real, independent correctness defect this pass found, not something Q7/Q8 called out.
- **Removed the early-return cap entirely** from the `ReadRecords` callback — every record is now
  resolved and aggregated. Per Q8, `ReadRecords` already read every record regardless of the cap, so
  this only adds one cached `GetTypeByMethodTable` lookup per large object (negligible, matches Q8's
  own cost analysis).
- **Heap-scan fallback's `AccumulateSegmentObject` used a genuinely different technique than the index
  path** — a proper bounded top-K-by-size accumulator (`if (largeObjectCandidates.Count > maxLargeObjects) { find-and-remove smallest }`), not an arbitrary-order cutoff. This means the heap-scan path's
  `topLargeObjects` were already correctly ranked even before this fix — only `typeAggregation` was
  fine here too, since it's built unconditionally, outside the size-based eviction. **So the heap-scan
  path had no correctness bug, only the removed capacity limit; the index path had two** (aggregation
  truncation *and* the missing sort). Deleted the min-eviction logic anyway since LOH-threshold-sized
  objects (≥ 85 KB) are a naturally small population on any real heap — no bound needed, matches this
  doc's "vestigial cap" pattern.
- **`TrimLargeObjectCandidates` helper deleted outright** — it existed solely to serve the cap that no
  longer exists.
- **Options class deleted outright** (both knobs fully move to render, matching the plan's call).
  Collapsed two now-redundant private `Analyze` overloads in the process — one 4-arg overload
  (heap/cache/progress/token) was dead code, never called; `AnalyzeAsync` always went through the
  5-arg options-taking overload. With options gone there was only one shape left, so it became the
  sole private `Analyze`. Removed from `AnalysisOptions`, `CliConfigurationModels` (property +
  `[JsonSerializable]`), `ConfigurationResolver` (builder + call site + constructor arg),
  `ResolvedExecutionOptions`, `AnalyzerExecutionService`, and two test call sites
  (`ResolvedExecutionOptionsFactory`, `StartupValidatorTests`).
- `LohFragmentationSectionBuilder` needed no changes — already rendered full lists via `STCompact`
  with no `.Take()`, and (per the D5 amendment) uses the table's default page size uniformly.

---

### 9.9 SegmentReservation — **GREEN** ✅ IMPLEMENTED, but for an unusual reason

[SegmentReservationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/SegmentReservationAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `ThirtyTwoBitPressureThresholdBytes` | 1.5 GB | 5 | **keep** |
| `RatioHighPressureThreshold` | 10.0 | 5 | **keep** |

**This analyzer has no caps, no limits, and no sampling. It is already exact.** Zero knobs are
deleted by the exactness work.

**The preset must still die — and this is the clearest example of why.** Both knobs are Category 5
semantic thresholds, and the preset varies them by tier:

| | Fast | Balanced | Full |
|---|---:|---:|---:|
| `ThirtyTwoBitPressureThresholdBytes` | 2.0 GB | 1.5 GB | 1.0 GB |
| `RatioHighPressureThreshold` | 12.0 | 10.0 | 8.0 |

A dump reserving 1.2 GB on a 32-bit process is *under* pressure at Fast and *over* pressure at Full.
The tier is not changing how hard the analyzer looks — there is nothing to look harder at — it is
changing **what the answer means**. That is a defect independent of this refactor: the same dump
yields contradictory findings depending on a knob the user reads as "how thorough."

This is the general argument for Category 5 in one case: thresholds define semantics, and semantics
must not vary by effort level.

#### Implementation notes (as shipped)

- **Analyzer itself untouched** — confirmed no caps/limits/sampling exist to remove, matching the
  audit exactly.
- **`Preset`/`Default` deleted, Balanced's values promoted straight to field initializers**, each with
  the D4-mandated rationale comment: `ThirtyTwoBitPressureThresholdBytes` (1.5 GB) documents the
  32-bit VA-space anchor; `RatioHighPressureThreshold` (10.0x) documents that it's unanchored and
  flagged for revisit with field data, per D4's own classification of these two thresholds.
- Same `ConfigurationResolver` profile-bypass pattern as every other section so far — `Preset` deleted,
  so `BuildSegmentReservationAnalysisFromConfig` applies overrides directly onto
  `new SegmentReservationAnalysisOptions()`.
- No section builder, finding generator, or trend comparer changes needed — none of them referenced
  `Preset`/`Default`, and there was no capped list to unbound.

---

### 9.10 Jit — **GREEN** ✅ IMPLEMENTED, worst Q7 finding so far

[JitAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/JitAnalyzer.cs) ·
[JitAnalysisOptions.cs](../../src/DumpDetective.Core/Options/JitAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxFramesPerThread` | 200 | 3 — wall-clock | **delete** |
| `TopMethodsLimit` | 20 | 1 — rows | move to render |
| `TopFrameTypesLimit` | 20 | 1 — rows | move to render |
| `LargeMethodThresholdBytes` | 64 KB | 5 | keep (but see below) |

**Q7 — one cap corrupts six accumulators.** `MaxFramesPerThread` breaks the stack-walk loop at
[:74](../../src/DumpDetective.Analysis/Analyzers/JitAnalyzer.cs#L74), and everything computed inside
that loop is truncated for any thread deeper than 200 frames:

| Accumulator | Line | Effect when truncated |
|---|---|---|
| `managedFrameCount` | :82 | under-counts |
| `activeMethodsOnStacks` | :86 | under-counts |
| `frameTypeCounts` histogram | :90-93 | missing deep-stack types |
| `tokenToNativeCodes` | :99-107 | **tiered-compilation detection misses methods** |
| `methodCandidates` | :110-125 | large methods deep in the stack never found |
| `unmanagedFrameCount` | :129 | under-counts |

Threads exceeding 200 frames are not exotic — deep async continuation chains and runaway recursion
are precisely what hang and stack-overflow dumps contain, which is when this analyzer matters most.
The cap is therefore most likely to fire exactly when its output is most needed.

**`LargeMethodThresholdBytes` is a semantics-by-tier defect** in the §9.9 mould: 96 KB at Fast,
64 KB at Balanced, 32 KB at Full. Whether a method counts as "large" should not depend on the
analysis tier. Keep the knob, stop varying it.

**Q5 — cost.** Unbounded stack walks across all live threads. Frame count is bounded by real stack
depth, not heap size, so this does not scale with dump size. Negligible.

**Q8 — a second frame cap exists one layer down.** See §6.3.

#### Implementation notes (as shipped)

- **`MaxFramesPerThread` deleted; the stack-walk loop now runs to completion** for every live thread.
  `frameIdx` is kept (for the every-50-frames cancellation check cadence) but the `break` on the cap
  is gone — all six accumulators Q7 listed are now exact.
- **`TopMethodsLimit`/`TopFrameTypesLimit` deleted; `BuildTopMethods`/`BuildTopFrameTypes` now return
  the complete sorted lists** (no `limit` parameter, no `Math.Min`/truncated loop).
- **`LargeMethodThresholdBytes` promoted to a plain initializer with a D4 rationale comment** ("64 KB
  — arbitrary round number, no theoretical basis; revisit with field data"), matching D4's own
  classification of this threshold as shaky. `Preset`/`Default` deleted.
- **`JitSectionBuilder` had the same double-truncation shape found in every prior section**:
  `d.TopActiveFrameTypes.Take(TopFrameTypesToShow)`/`d.TopLargestMethods.Take(TopMethodsToShow)` on
  top of the (formerly capped) domain lists. Fixed by feeding the full lists into `STCompact`; per the
  §11.2 D5 amendment, no custom `rowLimit` was reintroduced — both tables fall through to the default.
- **Found and fixed an independent, unrelated inconsistency while touching this file**: the "large
  method" flag column in the report table hardcoded `total > 64_000` regardless of the actual
  configured `LargeMethodThresholdBytes` (65,536 by default — close enough to `64_000` to hide the bug,
  but wrong on its face and silently stale if the threshold is ever overridden via config). Changed to
  compare against `d.LargeMethodThresholdBytes` (already carried on `JitDomainResult`) and format the
  flag text from the real threshold instead of a hardcoded "64 KB" string.
- `JitFindingGenerator`/`JitTrendComparer` needed no changes — neither referenced any of the deleted
  knobs.
- Same `ConfigurationResolver` profile-bypass pattern as every prior section — `Preset` deleted, so
  `BuildJitAnalysisFromConfig` applies overrides directly onto `new JitAnalysisOptions()`.
- **Confirmed independent of §6.3's `RootSetCache.MaxFramesPerThread = 256`** (Q8) — that's a
  different constant in a different class serving stack-root owner-attribution labeling, already
  resolved out-of-scope there; no interaction with this section's changes.

### 9.11 Array — **GREEN** ✅ IMPLEMENTED

[ArrayAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs) ·
[ArrayAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ArrayAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `SampleStride` | 100 | 2 — sampling | **delete** |
| `SparseSampleLimit` | 500 | 3 — wall-clock | delete |
| `TopSparseLimit` | 10 | 1 **and** 3 | move to render; it is also a loop bound |
| `TopTypeLimit` | 20 | 1 — rows | move to render |
| `TopLargeLimit` | 20 | 1 — rows | move to render |
| `SparseSampleMinLength` | 10,000 | 5 — semantic | **keep** |

#### Q7 — three stacked approximations reported as integers

The sparse-array probe compounds inaccuracy at three levels:

1. **One sample object per type.** Candidates are `(sampleAddr, elemName, TotalSize)` tuples — the
   probe opens a *single* array instance and treats its density as the type's density.
2. **Every 100th element of that one array**
   ([:259](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L259)):
   `for (int i = 0; i < arr.Length; i += options.SampleStride)`.
3. **Both reported figures are then extrapolated back up:**

```csharp
// :272
ulong wastedBytes = (ulong)(arr.Length * sparseRatio * (double)elemSize);
// :278
NullOrZeroCount: (int)Math.Min((long)(nullCount * ((double)arr.Length / sampleLen)), int.MaxValue)
```

`NullOrZeroCount` and `WastedBytes` are presented as exact counts and byte totals. They are
estimates derived from 1% of one instance. This is the single clearest case in the codebase for
Category 2 deletion: the cost of exactness is a linear walk over elements that are already mapped.

#### Q8 — `TopSparseLimit` is a loop bound, not just a row limit

[:243](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L243) reads
`for (ci = 0; ci < candidateLimit && topSparseArrays.Count < options.TopSparseLimit; ci++)` — probing
**stops** once 10 sparse arrays are found. Combined with the `TotalSize`-descending sort at :236, any
sparse array ranked 11th or later is never evaluated at all. Moving this to the render layer changes
what is discovered, not just what is shown.

**Q3 — no materialization risk.** `sparseCandidates` is O(distinct array types); the element walk
holds no per-element state beyond two counters.

**Q5 — measured (M3, §11.4).** Full element walks over qualifying arrays (length ≥ 10,000). On a real
3.35GB dump (1.73M array objects): capped 39 ms vs. fully uncapped 42 ms — negligible, confirms the
estimate. Also surfaced a real defect: `TopSparseLimit` is used directly as a `List<T>` constructor
capacity ([:241](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L241)) — setting it to
`int.MaxValue` to remove the loop bound throws `OutOfMemoryException` rather than just uncapping the
search. See F10 (§11.6).

#### Implementation notes (as shipped)

- **All three stacked approximations from Q7 are gone.** `sparseCandidates` is no longer capped at
  `SparseSampleLimit` before probing — every 1-D ref-type array candidate is walked.
  `SparseSampleLimit`/`SampleStride` deleted entirely; the element loop now walks every index
  (`for (int i = 0; i < arr.Length; i++)`) instead of every 100th. Since the sample now always covers
  the whole array, `NullOrZeroCount`/`WastedBytes` collapsed from extrapolated estimates
  (`nullCount * (arr.Length / sampleLen)`) to exact values (`nullCount` directly, `nullCount * elemSize`)
  — the extrapolation math is gone, not just uncapped.
- **`TopSparseLimit`'s F10 `List<T>` capacity `OutOfMemoryException` defect is moot, not "fixed"** —
  the loop bound that motivated pre-sizing the list is deleted, so `topSparseArrays`/`topLargeArrays`
  are now plain growable lists seeded with a small fixed capacity (64), never derived from a
  user-controllable value. Same fix applied to `topArrayTypes`.
- **`TopTypeLimit`/`TopLargeLimit`/`TopSparseLimit` all deleted, moved to render** — `ArraySectionBuilder`
  had the by-now-familiar double-truncation shape (`Math.Min(..., TopTypeRows/TopLargeRows/TopSparseRows)`
  slicing on top of the analyzer cap, plus "N additional omitted" text blocks for all three tables).
  Fixed by feeding full lists into `STCompact`; per the §11.2 D5 amendment, no `rowLimit` was
  reintroduced — all three fall through to the default.
- **Deleted dead code found while touching this file**: `ReadLargeArraysFromIndex`, a full alternate
  large-object-index reader, was defined but never called anywhere — the live code path uses
  `LargeObjectTracker.ReadRecords` instead. Removed it along with the now-unused
  `DumpDetective.Analysis.Indexing.Container` / `System.Buffers.Binary` usings it required.
- **`ArrayDomainResult.ScanLimited` deleted** (permanently-false once `SparseSampleLimit` is gone) —
  removed from the domain result, `ArraySectionBuilder`'s `scan_limit_reached` key metric, and
  `ConfidenceSectionBuilder`'s "Arrays" limitation row + `BuildArrayText` helper. Left the sibling
  `AsyncTaskDomainResult.TaskScanLimited`/`AsyncStateMachineDomainResult.ScanLimited` rows in
  `ConfidenceSectionBuilder` untouched — those analyzers haven't been migrated yet (§9.29-ish, later
  in this doc) and their caps are still real.
- **Deleted `tests/.../ArrayUncappedRealDumpTests.cs`** — existed solely to produce the M3
  capped-vs-uncapped measurement recorded above; same reasoning as the `ModuleAnalyzerUncappedRealDumpTests.cs` deletion in §9.3.
- **`MaxSparseFindings = 3` in `ArrayFindingGenerator` left untouched** — that's a deliberate
  findings-count throttle (how many low-value sparse-array findings surface in the report), not a
  data-completeness cap; `r.TopSparseArrays` itself is the full, unbounded list, so this doesn't
  interact with anything §9.11 changed.
- Same `ConfigurationResolver` profile-bypass pattern as every prior section — `Preset` deleted, so
  `BuildArrayAnalysisFromConfig` applies overrides directly onto `new ArrayAnalysisOptions()`.

---

### 9.12 String — **AMBER** ⚠️ PARTIALLY IMPLEMENTED, the first analyzer where cap removal is not sufficient

[StringAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs) ·
[StringAnalysisOptions.cs](../../src/DumpDetective.Core/Options/StringAnalysisOptions.cs)

Sixteen knobs — the largest options surface in the codebase.

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxStringsToDedup` | 50,000 | 3 | delete (see below) |
| `MaxUniqueStringTracking` | 200,000 | 3 — **memory** | restructure |
| `SamplingMode` | `Moderate` | 2 — multiplier | **delete** |
| `MaxDuplicateStringLength` | 500 | 3 — **memory guard** | **keep**, see Q3 |
| `EnableDeduplication` | true | toggle | delete; always on |
| `DeduplicationMode` | `FallbackToHeapScan` | fallback policy | collapse to the fallback path |
| `DeduplicationStringCountThreshold` | `int.MaxValue` | auto-disable | delete |
| `DetectInterning` | true | toggle | hard-code true |
| `TopDuplicatesToShow` | 20 | 1 — rows | move to render |
| `PreviewMaxLength` | 80 | 1 — display | move to render |
| `ProduceRawExports` | false | report artifact | move to report options |
| `VeryLongStringThresholdBytes` | 85,000 | 5 | keep |
| `LohThresholdBytes` | 85,000 | 5 | keep |
| `MinDuplicateStringCount` | 10 | 5 | keep |
| `MinDuplicateCharLength` | 4 | 5 | keep |

#### Why AMBER: Q3 answers yes, twice

- **`MaxUniqueStringTracking` bounds a dictionary**, and its own XML doc says so: *"Prevents
  unbounded dictionary growth on dumps with millions of unique strings."* Removing it makes the
  fingerprint map O(unique strings). At ~10M unique strings this is hundreds of MB — survivable, but
  it is a genuine resident-bytes cap, not a wall-clock one, and it is the only thing standing between
  the current design and unbounded growth.
- **Found via M5 (§11.4): this isn't even the cap that binds in production.** `StringAnalyzer.Analyze`'s
  fast path reads `heapIndex.StringDedupIndex`, a Phase 1 satellite index built once during
  `PrebuildHeapIndex` and governed by its own hardcoded `const int MaxDedupUnique = 500_000`
  ([DiskBackedObjectIndexWriter.cs:167](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L167)) —
  not part of `StringAnalysisOptions` at all, and not touched by anything in this table. Once Phase 1's
  index exists (always, in production), `MaxStringsToDedup`/`MaxUniqueStringTracking` only govern
  fallback branches that essentially never execute. **Decided: delete `MaxDedupUnique` too**, same
  reasoning as the audited options — confirmed via measurement not to bind on a 3.35GB/321K-unique-string
  real dump, so the ~10M-unique-string memory-growth question (estimated at 2-2.5GB by linear
  extrapolation from this measurement, not directly confirmed) needs a larger dump to validate exactly.
- **`MaxDuplicateStringLength` is a materialization guard.** [:1185](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L1185)
  calls `obj.AsString(maxLength: MaxDuplicateStringLength - 1)`. Removing it means materializing
  every string at full length — a single 100 MB string becomes a 200 MB managed allocation. This
  cap must survive in some form.

**Exactness here needs a different shape, not a bigger number.** Exact duplicate detection over
every string requires a streaming fingerprint-and-count pass whose per-string state is a hash rather
than content, with content read only for the patterns that end up reported. That is the same
disk-backed, hash-partitioned pattern already used by
[`ReverseEdgeExtractor`](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeExtractor.cs)
— an existing structure to imitate, but not one that can simply be pointed at.

**`MaxStringsToDedup` is the cap that can just go.** It bounds *how many strings get read at all*
([:177](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L177)), so at the default the
analyzer inspects 50,000 strings on a heap that may hold tens of millions. Every duplicate statistic
is drawn from that subset.

#### Q8 — two knobs compound into one effective cap

`SamplingMode` does not bound anything itself; it *multiplies* the other two caps
([:1146-1159](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L1146-L1159)):

```csharp
case StringSamplingMode.Aggressive: maxToDedup = Math.Max(1_000, (int)(maxToDedup * 0.25)); …
case StringSamplingMode.Full:       maxToDedup = Math.Min(int.MaxValue/2, (int)(maxToDedup * 2)); …
```

So the effective cap is `preset value x mode multiplier`, and both factors are set by the same
profile. `Fast` sets `MaxStringsToDedup = 10,000` *and* `SamplingMode = Aggressive`, yielding an
effective 2,500. Nothing surfaces the composed number to the user; the config value they set is not
the value that applies. Deleting `SamplingMode` removes the compounding.

#### Q8 — `DeduplicationStringCountThreshold` is inert by default

Defaults to `int.MaxValue`, so the auto-disable never fires, and no preset overrides it. It is
reachable only by explicit config. Live code ([:157](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L157),
[:546](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L546)), but dead in practice.

#### Implementation notes (as shipped) — scope explicitly bounded to what AMBER allows

**What shipped:** every knob the table marks `delete`/`toggle`/`hard-code`/`collapse`/`move to render`
is gone — `MaxStringsToDedup`, `SamplingMode` (+ its compounding multiplier and the whole
`StringSamplingMode` enum), `EnableDeduplication`, `DeduplicationStringCountThreshold`,
`DeduplicationMode` (+ enum, + the `PreferPrebuiltOnly`/`Disabled` branches), `DetectInterning`
(now unconditional whenever FOH segments exist), `TopDuplicatesToShow`, `PreviewMaxLength`. The two
genuine memory guards Q3 identified — `MaxUniqueStringTracking` and `MaxDuplicateStringLength` — are
kept, exactly as the table specifies. `MaxDedupUnique` (the Phase 1 satellite-index cap Q8/M5 found
actually binds in production, not part of `StringAnalysisOptions` at all) is also deleted, per the
decision recorded in that finding.

**What did NOT ship, and why this stays AMBER, not GREEN:** the doc is explicit that "exactness here
needs a different shape, not a bigger number" — a disk-backed, hash-partitioned streaming
fingerprint-and-count pass imitating `ReverseEdgeExtractor`. That is a new subsystem, not a knob
deletion, and building it was out of scope for this pass. What shipped instead is the full set of
knobs that were safe to remove without that redesign: `MaxStringsToDedup` no longer truncates *how
many strings get read* (the real Q7 finding), so duplicate statistics are now drawn from every string
that fits within `MaxUniqueStringTracking`'s dictionary, not an arbitrary 50,000-string prefix. That
is a genuine, large exactness improvement even without the full redesign — but it is not the same as
proving every duplicate pattern on a heap with, say, 10M unique strings is exact, which is precisely
the scenario `MaxUniqueStringTracking` still guards against (Q3, unconfirmed, needs a larger dump).

**Simplification found while implementing, not called out by the audit:** the analyzer had two
parallel implementations of "collect stats + dedup in one pass" — a `runDedup`-gated no-index-fallback
branch inside the `if (runDedup)` block, and a *separate* `else if (typeAggregates is null)`
stats-only branch that ran only when `runDedup` was false. Once `EnableDeduplication`/
`DeduplicationStringCountThreshold`/`DeduplicationMode.Disabled` are gone, `runDedup` is
unconditionally true, so the second branch became dead code — deleted, and the no-index-fallback
branch (which already computed both stats and dedup together) is now the sole no-index path.

**Replaced the two-`PriorityQueue`-plus-merge top-K selection with a single sort over the full set.**
`TopDuplicatesToShow` fed two bounded `PriorityQueue<StringLeakInfo, TKey>` (by-waste, by-count) that
were drained and merged (`MergeTopDuplicates`) to approximate "top by either ranking." Since nothing
is truncated anymore, the merge is moot — the analyzer now builds one list of every pattern meeting
`MinDuplicateStringCount`, sorted by wasted bytes descending (count, then total size, as tiebreaks).
`DrainToDescendingWaste`/`DrainToDescendingCount`/`MergeTopDuplicates` collapsed into one
`BuildDuplicateSnapshots` helper.

**`PreviewMaxLength` "moved to render" concretely means: a fixed local constant, not a rowLimit.**
Unlike the row-count knobs in other sections, this controlled how long a *stored* preview string is
(`CreatePreview` at fingerprint time) — there's no equivalent to "send the full data, let the table
paginate" for a single already-truncated string field. Hardcoded `PreviewLength = 80` at creation time
in the analyzer (matching the old Balanced default) and a matching `PreviewDisplayLength = 80` local
const in `StringSectionBuilder` (the previous `Math.Max(32, d.PreviewMaxLength)` re-truncation was
redundant anyway since both values came from the same options object).

**`DeduplicationSkipped`/`DedupSkipReason` deleted — permanently-false/null once the skip conditions
that drove them are gone**, same pattern as Boxing's `TypeScanCapped` (§9.1) and Module's
`ExcludedModuleCount` (§9.3). Removed from `StringDomainResult`, `StringSectionBuilder`'s
`dedup_skip_reason` key metric and `dedupLine` ternary, and `StringFindingGenerator`'s low-coverage
finding condition.

**`SamplingMode`/`DeduplicationMode`/`DeduplicationThreshold`/`MaxStringsToDedup` metadata fields on
`StringDomainResult` deleted** along with their `sampling_mode`/`dedup_mode`/`dedup_threshold`/
`max_to_dedup` key metrics in `StringSectionBuilder` — all became permanently-fixed/meaningless once
the underlying options were deleted.

**`ProduceRawExports` "move to report options" deliberately deferred, left on `StringAnalysisOptions`
unchanged.** A generic `ReportOptions` class already exists (`Format`/`StyleVersion`/`PreRender`/
`SeparateJson`), which made this item look like a natural fit — but `ReportOptions` is only
constructed at the CLI/report-generation layer, never passed into `AnalysisContext`, while raw-export
generation currently happens *inside* `StringAnalyzer.Analyze` (JSON/CSV/NDJSON written mid-analysis).
Moving the toggle there properly means either wiring `ReportOptions` into `AnalysisContext` or moving
export generation itself into a later report-building stage — a real design decision, not a rename,
and `WeakReferenceAnalyzer` has the exact same `ProduceRawExports` pattern and hasn't been migrated
yet either. Doing this for String alone would create an inconsistency with WeakReference; flagged
here as a cross-cutting item for whenever WeakReference's own audit section is implemented, not solved
in this pass.

**Config/CLI wiring:** `StringAnalysisOptions.Preset` deleted like every other section, but the CLI
special-case for `--max-duplicate-string-length`/`--min-duplicate-string-count`
(`AnalyzerOptionsBuilder.BuildBalancedPresetFromCli`'s `StringAnalysisOptions`-specific branch) needed
its own extraction into `BuildStringAnalysisFromCli` rather than the generic `Preset`-bypass pattern,
since it wasn't just "apply overrides onto `new T()`" — it only overrides when the CLI request
actually sets one of those two fields. **Caught a real bug while doing this**: an early version of
`BuildStringAnalysisFromConfig`'s "config file used, but no String section present" fallback branch
called into the CLI-override helper, which broke `ConfigurationResolverTests.Resolve_ShouldUseProfileBaseline_WhenConfigMissingThatField`
— the original `BuildAnalyzerOptionsFromConfig` never consulted CLI flags once a config file was in
play at all (config-file mode and CLI-only mode were strictly separate paths in the old code). Fixed
by falling back to plain `new StringAnalysisOptions()` in that branch, matching original behavior.

**Deleted `tests/.../StringAnalyzerOptionsTests.cs` outright** (tested `Preset`/`SamplingMode`/
`ComputeEffectiveCaps`, all gone) and **`tests/.../StringAnalyzerUncappedRealDumpTests.cs`** (the M5
measurement test — its "capped baseline" comparison for `MaxStringsToDedup` no longer has a capped
alternative to compare against). `StringAnalyzerHeapIndexScanTests.cs`'s reflection-based `SeedState`
helper needed one field removed (`_indexScanMaxToDedup`) but otherwise required no changes.
**M5's `MaxUniqueStringTracking` question remains genuinely open** — the ~10M-unique-string
memory-growth estimate (2-2.5GB) was never directly measured, only extrapolated; validating it needs
a larger real dump than the 3.35GB/321K-unique-string one used for M3/M5, which is exactly why this
option survives as a guard rather than being deleted alongside `MaxDedupUnique`.

---

### 9.13 AsyncStateMachine — **GREEN** ✅ IMPLEMENTED

[AsyncStateMachineAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs) ·
[AsyncStateMachineAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AsyncStateMachineAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TypeCandidateLimit` | 200 | 3 | delete |
| `HistogramInstanceCapPerType` | 1,000 | 2 — sampling | **delete** |
| `HistogramTopTypeLimit` | 10 | 3 | delete |
| `SuspendedMethodMapLimit` | 20 | 1 — rows | move to render |
| `TopCapturedSizeEntries` | 10 | 1 — rows | move to render |
| `TopTypeLimit` | 20 | 1 — rows | move to render — **confirmed live by V2/§11.3** (original grep was incomplete; genuinely read at [AsyncStateMachineAnalyzer.cs:109](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L109), distinct from `AllocationPatternAnalysisOptions`'s same-named property) |
| `LargeCaptureThresholdBytes` | 1 MB | 5 | keep |

#### Q7 — the domain result documents its own corruption

[AsyncStateMachineDomainResult.cs:39](../../src/DumpDetective.Analysis/Models/AsyncStateMachineDomainResult.cs#L39)
carries the comment *"Summed over ALL candidate types (bounded by `TypeCandidateLimit`), not just…"*.
A field described as a sum over *all* types is, parenthetically, a sum over at most 200. The comment
is honest; the field name is not. `TypeCandidateLimit` is applied at
[:77](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L77).

**`StateDistribution` is a sample, not a distribution.** The suspend-state histogram covers only the
top `HistogramTopTypeLimit` (10) types ([:218](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L218)),
and within each of those, at most `HistogramInstanceCapPerType` (1,000) instances
([:229](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L229)). Every other
type keeps `StateDistribution` empty. For a service with 500,000 pending state machines across 60
types, the reported distribution reflects at most 10,000 instances from 10 types.

**Q8 — clean.** `SuspendedMethodMapLimit` is a post-build `RemoveRange`
([:341-342](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L341)) and
`TopCapturedSizeEntries` a `Math.Min` at :309 — both pure output slicing with no cascade.

**Q5 — measured (M2, §11.4), partially.** On a real 3.35GB dump, the second full-index scan itself
costs ~2s and is cap-independent (uncapped vs. capped: 1,728ms vs. 2,132ms, no real difference). But
that dump only has 11 total state machines, so the per-type instance cap never actually bound in
either run — the interesting case (this section's own 500,000-instance-across-60-types example,
directly above) needs a dump with a large state-machine population to measure the per-instance field-read
cost directly; estimated (not measured) at hundreds of ms to low seconds even at that scale.

**Q3 — no risk.** Histogram state is a per-type state-value counter, O(types x distinct states).

#### Implementation notes (as shipped)

All six knobs deleted from `AsyncStateMachineAnalysisOptions`; only `LargeCaptureThresholdBytes`
(Category 5, kept) remains as a plain field initializer, no `Preset`/`Default`.

- `TypeCandidateLimit` deleted: `candidates` in `AsyncStateMachineAnalyzer.cs` now holds every
  type flagged `IsAsyncStateMachineType`, no cap and no `scanLimited`/`skippedTypeCount`/
  `skippedBytes` bookkeeping.
- `TopTypeLimit` deleted: the `i < typeLimit` gate that previously only built profiles for the
  top-N candidates is gone; every candidate gets a `pendingProfiles` entry (moved to render —
  the section builder already fed `STCompact`'s default 20-row pagination, per §11.2 D5).
- `HistogramTopTypeLimit` deleted: the suspend-state histogram second pass now tracks every
  type in `pendingProfiles` that has a resolvable `<>1__state` field, not just the top 10.
- `HistogramInstanceCapPerType` deleted, but the early-exit optimization was **preserved**
  rather than dropped: instead of an arbitrary per-type cap, `histogramRemaining[mt]` is seeded
  from the type's *exact* `TypeAggregates` instance count (`p.Count`). Once that many instances
  of a type have been seen, the counter naturally reaches zero and the type stops being
  tracked — the second heap pass still exits early via `typesStillOpen == 0` once every type is
  exhausted, but now the histogram is complete rather than sampled.
- `SuspendedMethodMapLimit` / `TopCapturedSizeEntries` deleted (Category 1, moved to render):
  `AsyncStateMachineSectionBuilder.cs`'s three `BuildXRows` helpers no longer take a `limit`
  parameter and iterate the full analyzer-returned list; the local `TopTypeRows`/`TopCaptureRows`/
  `TopSuspendedRows` consts and their `Math.Min`/`RemoveRange` truncation were removed.
- `AsyncStateMachineDomainResult.ScanLimited`, `SkippedTypeCount`, `SkippedBytesFraction` deleted
  (permanently-false/zero vestiges, same pattern as prior sections). `TotalGen2Count`'s comment
  updated — it's summed over the *same* population as `TotalStateMachines` now, so
  `AsyncStateMachineTrendComparer`'s gen2-fraction calculation is exact rather than an
  understatement whenever candidates exceeded `TopTypeLimit`.
- `ConfidenceSectionBuilder.cs`: removed the "Async state machines" `AddLimitation` row and
  `BuildAsyncStateText` helper (mirrors the Array §9.11 removal). `AsyncTaskDomainResult`'s
  separate `TaskScanLimited` row is untouched — that analyzer hasn't been migrated yet.
- `ConfigurationResolver.cs`: `BuildAsyncStateMachineAnalysisFromConfig` switched from the
  generic `BuildAnalyzerOptionsFromConfig<T>(..., Preset)` helper to the section/options-override
  Preset-bypass pattern (same as Array/Boxing), and the CLI-request fallback now constructs
  `new AsyncStateMachineAnalysisOptions()` directly instead of routing through
  `AnalyzerOptionsBuilder.BuildBalancedPresetFromCli`.
- Deleted `AsyncStateMachineUncappedRealDumpTests.cs` (§11.4 M2 measurement) — with
  `HistogramTopTypeLimit`/`HistogramInstanceCapPerType`/`TypeCandidateLimit` gone there is no
  capped baseline left to compare against; the measurement it produced is preserved above in the
  Q5 note.
- `StateDistribution`'s top-3-states-only truncation (`sorted.RemoveRange(3, ...)` in the
  analyzer) was **not** part of the audited knob table and was left as-is — it's a per-instance
  display-shape decision, not a scan-completeness cap, and every instance is still counted into
  the underlying histogram before the top-3 slice is taken.
- Test suite: 642 passed, 25 skipped, 0 failed after this change.

### 9.14-9.16 preamble: group 3 shares one root cause

**Update (post-§10): the blocking item shipped.** §10's dominator-tree retention provider
(`IDominatorTreeProvider`) landed via
[dominator-tree-phase1-integration.md](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md),
resolving the preamble's original blocker. §9.14 and §9.15 now read the exact tree instead of
running `BoundedGraphWalk.CollectRetainedObjects`/`FinalizableObjectAnalyzer.BfsEstimateRetained`
(both deleted). §9.16 partially migrated (retained *bytes* already came from the tree; the
forward-path-type-names walk deliberately did not, see its implementation notes) —
`BoundedGraphWalk.CollectForwardTypeNames` and `RetainedSizeCandidateSelector`'s
`ComputeExclusiveRetained` fallback walk both remain, now as an intentional non-blocking
residual rather than the section's headline blocker. The original preamble text below is kept for
the problem framing.

All three analyzers below approximate **retained size / retained set** with a node- and depth-bounded
BFS. There are **four separate implementations** of that approximation:

| Implementation | Consumer |
|---|---|
| `BoundedGraphWalk.CollectForwardTypeNames` | GCRoot ([:107](../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs#L107)) |
| `BoundedGraphWalk.CollectRetainedObjects` | StaticRootLeak ([:154](../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs#L154)) |
| `BoundedGraphWalk.ComputeExclusiveRetained` | `RetainedSizeCandidateSelector` ([:79](../../src/DumpDetective.Analysis/Traversal/RetainedSizeCandidateSelector.cs#L79)) |
| `FinalizableObjectAnalyzer.BfsEstimateRetained` — **private, a fourth copy** | FinalizableObject ([:213, :295](../../src/DumpDetective.Analysis/Analyzers/FinalizableObjectAnalyzer.cs#L295)) |

`DominatorTreeComputer` computes the exact retained size of **every** node in the reachable graph in
one pass — 218 s on the 25.6 GB dump (§4). Every one of the four estimators above is a
pre-dominator-tree workaround.

**So group 3's verdict is AMBER not because exactness is expensive, but because the accessor doesn't
exist yet.** The blocking item is §10's "dominator-tree-backed retained sizes" workstream: a shared
`address → exact retained bytes / retained set` lookup over the dominator tree's rollup arrays. Build
that first, then all three analyzers become deletions rather than rewrites.

**`AbsoluteMaxDepth = 20` clamps everything.**
[BoundedGraphWalk.cs:16](../../src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs#L16) declares
`private const int AbsoluteMaxDepth = 20`, and every entry point does
`maxDepth = Math.Min(maxDepth, AbsoluteMaxDepth)`. Consequences are covered per-analyzer below.

---

### 9.14 StaticRootLeak — **GREEN** ✅ IMPLEMENTED

[StaticRootLeakDetector.cs](../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs) ·
[StaticRootLeakAnalysisOptions.cs](../../src/DumpDetective.Core/Options/StaticRootLeakAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxRetainedObjectsToScan` | 10,000 | 3 — **memory** | replace with dominator lookup |
| `SampleRetainedObjectsToInspect` | 100 | 2 — sampling | delete |
| `MaxRootsToReport` | 15 | 1 — rows | move to render |
| `TopRetainedTypesToReport` | 5 | 1 — rows | move to render |
| `SignificantMemoryThresholdBytes` | 1 MB | 5 | keep |
| `SignificantObjectCountThreshold` | 100 | 5 | keep |

**Q3 — yes, and the code says so.** The comment at
[:137](../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs#L137) states
`CollectRetainedObjects` *"materializes a Dictionary up to `MaxRetainedObjectsToScan` entries."*
Unbounding it means a `Dictionary<ulong,(ulong,ulong)>` over the entire retained set of a static
root — which for a leaking static cache is potentially most of the heap. **This cap cannot simply be
deleted.**

**Q4 — yes.** The exact retained set of any object is a dominator-tree subtree. Reading it from the
rollup arrays costs O(subtree), with no per-object dictionary and no depth limit.

**Q7 — the retained figure is bounded twice over.** A static root retaining 2 million objects reports
the size of at most 10,000 of them, and `TopRetainedTypesToReport` is computed from a further sample
of `SampleRetainedObjectsToInspect` (100). Severity ranking of static roots is therefore ordered by a
number that saturates: every root retaining more than 10,000 objects looks identical.

---

#### Implementation notes (as shipped)

`§10`'s dominator-tree retention provider (`IDominatorTreeProvider`, shipped per
[dominator-tree-phase1-integration.md](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md))
resolved the blocker this section was AMBER on — `EnumerateRetainedSet` got its first real caller here.

- `StaticRootLeakAnalysisOptions` reduced to the two Category-5 fields
  (`SignificantMemoryThresholdBytes`, `SignificantObjectCountThreshold`); `MaxRootsToReport`,
  `TopRetainedTypesToReport`, `SampleRetainedObjectsToInspect`, `MaxRetainedObjectsToScan` all
  deleted. No `Preset`/`Default` remain.
- `StaticRootLeakDetector.AnalyzeStaticRoots` now has three paths per root, in priority order: (1)
  the pre-existing shape pre-check (no reference-typed fields anywhere in the field tree — direct
  object only, exact, `ScanWasCapped = false`); (2) `treeProvider.TryGetRetainedBytes` for the
  exact total, then a streaming `treeProvider.EnumerateRetainedSet` pass building the per-type
  breakdown (`ObjectsKeptAlive`, `TopRetainedTypes`, `ContainsCollections`, `ContainsEventHandlers`)
  over *every* retained object, not a `SampleRetainedObjectsToInspect`-bounded prefix —
  `ScanWasCapped = false`; (3) dominator tree unavailable for this run or this root wasn't
  reachable when it was built — degrades to direct-object-only, `ScanWasCapped = true` (repurposed
  from "hit the numeric cap" to "not exact this run," same field, same section-builder/finding-
  generator consumers, no churn there).
- `BoundedGraphWalk.CollectRetainedObjects` deleted outright — no remaining caller anywhere in the
  codebase after this change (confirmed by search). Its two dedicated tests
  (`BoundedGraphWalkDepthCapTests.cs`, and the first test in what's now
  `StaticRootLeakDetectorDominatorTreeDiscrepancyTests.cs`) deleted with it; the file's still-valid
  `StaticRootLeakDetector` end-to-end real-dump test was kept and the file renamed to match.
- `MaxRootsToReport`/`TopRetainedTypesToReport` moved to render: the analyzer now returns every
  analyzed root (sorted) and every retained type per root (sorted); `StaticRootSectionBuilder`'s
  flat "top roots by retained bytes" table uses `STCompact`'s default pagination (§11.2 D5). The
  per-root "top retained types" *sub-tables* are a different shape — one table per root, not rows
  within one table — so D5's row-pagination argument doesn't apply; kept a small render-layer
  constant (`MaxRootDetailTables = 8`, matching the prior `TopRootsToShow` default) bounding how
  many roots get their own detail sub-table. The collections/event-handler/ALC advisory blocks now
  scan the *full* root list rather than the display-limited slice.
- **Residual, deliberately out of scope:** `EnumerateRetainedSet`'s cost near the dominator tree's
  root is flagged unmeasured in the source doc ("its unbounded subtree-walk cost near the tree's
  root is unmeasured on a real dump"). This section is its first production caller. The risk is
  judged acceptable because (a) it's memory-safe regardless of cost — a streaming walk, not the old
  Dictionary materialization it replaces — and (b) it replaces a heuristic that was already
  silently wrong (truncated at 10,000 objects) with an exact one, which is this whole plan's goal.
  Wall-clock cost for a pathological "root retains most of the heap" case has not been measured on
  a real dump; if it turns out to be a problem in practice, the fallback path (case 3 above) is
  already there to degrade to.

### 9.15 FinalizableObject — **GREEN** ✅ IMPLEMENTED

[FinalizableObjectAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/FinalizableObjectAnalyzer.cs) ·
[FinalizableObjectAnalysisOptions.cs](../../src/DumpDetective.Core/Options/FinalizableObjectAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxBfsNodes` | 200 | 4 | delete with the BFS |
| `MaxBfsDepth` | 10 | 4 | delete with the BFS |
| `QueueScanLimit` | 500 | 3 | delete |
| `TopTypeLimit` | 20 | 1 — rows | move to render |
| `TopQueueEntries` | 10 | 1 — rows | move to render |

Options class deleted outright.

**Q4 — the whole method goes.** `BfsEstimateRetained`
([:295](../../src/DumpDetective.Analysis/Analyzers/FinalizableObjectAnalyzer.cs#L295)) is a private
fourth implementation of bounded retained estimation, duplicating `BoundedGraphWalk`. It is not
unbounded — it is **deleted** and replaced by a dominator-tree lookup. The name carries the verdict:
`…Estimate…`.

**Q7 — `MaxBfsNodes = 200` is the most aggressive cap audited.** A finalizable object whose retained
graph exceeds 200 nodes — trivially common for anything holding a collection or a stream buffer —
reports a retained size derived from the first 200 nodes at depth ≤ 10. On a 50M-object heap this is
not an approximation of the answer; it is unrelated to it.

`QueueScanLimit = 500` separately bounds how much of the finalization queue is examined, so on a
dump with a backed-up finalizer queue — the exact pathology this analyzer exists to detect — the
queue statistics are truncated at 500 entries.

---

#### Implementation notes (as shipped)

`FinalizableObjectAnalysisOptions` deleted outright (all five fields), matching the doc's own
prediction — no fields survived once Category 1/2/3/4 were resolved.

- `BfsEstimateRetained` (the "fourth private copy" of bounded retained-size BFS) deleted entirely,
  per Q4's directive — no internal-constant fallback BFS kept, unlike GCRoot's path-type-name walk
  (§9.16), because this analyzer's retained-size *is* the feature being measured, not a supporting
  narrative detail. Per-entry retained bytes now come from
  `treeProvider.TryGetRetainedBytes(obj.Address, ...)`; when the tree is unavailable (or the lookup
  misses), falls back to `obj.Size` (shallow) — the same "shallow size as honest degrade" pattern
  already shipped for `GCHandleAnalyzer` in §10's own consumer list.
- Added `FinalizerQueueEntry.RetainedBytesIsExact` (mirrors `GCHandleAnalyzer`'s
  `PinnedRetainedBytesIsExact`) so the report can show which entries are exact vs. degraded; new
  "Exact?" column in `FinalizableObjectSectionBuilder`'s queue-entries table.
  `IsRetainedEstimatePartial` on the domain result is now true whenever *any* entry fell back to
  shallow size, repurposed from its old "BFS hit its cap" meaning — same field name, updated
  wording in the finding generator and section builder ("dominator tree unavailable for some
  entries" instead of "BFS capped").
- `QueueScanLimit` deleted: with the O(1) exact-bytes lookup replacing a per-entry BFS, processing
  every finalizer-queue entry (not just the first 500) is cheap — `queueSamples` now collects every
  entry from `heap.EnumerateFinalizableObjects()`.
- `TopTypeLimit`/`TopQueueEntries` moved to render: `topTypesByGen2`, `topQueueTypes`, and
  `topEntries` are now full sorted lists; `FinalizableObjectSectionBuilder` dropped its
  `TopTypeRows`/`TopQueueRows` consts and per-table `Math.Min`/"N additional omitted" text,
  matching D5's default-pagination pattern used everywhere else.
- Test suite regression only (no dedicated pre-existing unit tests for this analyzer, same as
  before this change) — covered by the real-dump discrepancy/integration suite.

### 9.16 GCRoot — **GREEN** ✅ IMPLEMENTED

[GCRootAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs) ·
[GCRootAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCRootAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxBfsNodes` | 500 | 4 | delete with the walk |
| `MaxBfsDepth` | 20 | 4 | delete — **and see below** |
| `PathSearchTopN` | 25 | 4 — candidates walked | delete |
| `TopSeverityLimit` | 20 | 1 — rows | move to render |

Options class deleted outright.

#### Q8 — `MaxBfsDepth = 30` at Full is a value that cannot take effect

`BoundedGraphWalk` clamps every caller: `maxDepth = Math.Min(maxDepth, AbsoluteMaxDepth)` with
`AbsoluteMaxDepth = 20`. The `Full` preset sets `MaxBfsDepth = 30`
([GCRootAnalysisOptions.cs:13](../../src/DumpDetective.Core/Options/GCRootAnalysisOptions.cs#L13)),
which is silently reduced to 20. Anyone reading the preset, or setting 30 in config, gets 20 and no
warning.

This is the third instance of *configured value ≠ applied value*, after String's `SamplingMode`
multiplier (§9.12) and Module's `new()` vs `Default` divergence (§9.3). Worth treating as a pattern
rather than three coincidences: **the options surface is not verifiable by reading it.**

**Q7 — path evidence is capped on two axes.** `PathSearchTopN = 25` bounds how many candidates get a
root path at all; `MaxBfsNodes = 500` bounds each search. Candidate 26 gets no path evidence, and
[Evidence.cs:27](../../src/DumpDetective.Analysis/Models/Evidence.cs#L27) already downgrades
confidence 0.8 → 0.6 whenever a search truncates — so the analyzer is scoring its own output as
low-confidence by construction on any non-trivial heap.

**Q5 — measured (M4, §11.4): affordable even without the dominator-tree rewire.** Removing
`PathSearchTopN` means a root path for *every* candidate rather than 25. On a real dump with 1,404
findings, computing a path for all of them (still via the current BFS — `CollectForwardTypeNames`
hasn't been re-pointed at the dominator tree yet) took 568 ms, actually faster than the capped run's
874 ms for 25. §10's `EnumerateRetainedSet` rewire is still worth doing (an O(1) parent-pointer read
beats a bounded BFS per candidate), but is no longer a blocking prerequisite — `PathSearchTopN` can be
deleted now.

#### Implementation notes (as shipped)

`GCRootAnalysisOptions` deleted outright — all four audited fields resolved, none left as a
tunable.

- `PathSearchTopN` deleted per the M4 measurement above: root-path evidence (BFS path-type names +
  retained-size fallback) now computed for every finding (`pathN = findings.Count`), not the top 25.
- `TopSeverityLimit` deleted from options, but *not* uncapped uniformly — split into two concerns
  that the original single knob conflated. `TopRootsBySeverity` (the actual returned/reported
  finding set) is now the full list, paginated at render (`GCRootIntelligenceSectionBuilder`
  already fed `STCompact` full lists with no row cap, so no section-builder change was needed
  there). The owning-stack-frame-attribution enrichment
  (`cache.TryResolveStackFrameOwner` → `FieldDescription`) stayed bounded, by a new private
  `StackOwnerAttributionLimit = 20` constant — the source code's own comment already flagged this
  per-thread frame walk as "too costly to run for every Stack root in the dump," and it's purely
  cosmetic (an unenriched row loses only the "in Type.Method()" text, not any exactness-relevant
  data: kind/type/bytes/severity are all present and correct either way).
- `MaxBfsNodes`/`MaxBfsDepth` moved off the options surface to private constants
  (`PathWalkMaxNodes = 500`, `PathWalkMaxDepth = 20`) in `GCRootAnalyzer.cs`, matching
  `BoundedGraphWalk.AbsoluteMaxDepth`'s existing precedent for "internal traversal bound, not
  user-configurable." Functionally unchanged from the prior default — this satisfies "delete with
  the walk" in the sense of removing profile/config control over them, not in the sense of
  replacing the walk itself.
- **Residual, deliberately deferred:** `BoundedGraphWalk.CollectForwardTypeNames` (the forward
  path-type-names walk feeding `RootPathFinding.PathTypeNames`) is still BFS-backed, not rewired to
  `IDominatorTreeProvider.EnumerateRetainedSet`, even though retained *bytes* for the same findings
  already come from the exact tree (shipped earlier, §7 of the B2 doc). The plan doc's own Q5 note
  only unblocks `PathSearchTopN` ("no longer a *blocking* prerequisite" for that knob specifically),
  not the walk-to-dominator-tree rewire itself, which stays explicitly "still worth doing." Retained
  as a real BFS with the same fixed bounds as before rather than switched to
  `EnumerateRetainedSet`, since that member's cost near the tree's root is unmeasured (see §9.14's
  notes, where a different consumer took on that risk instead). The old
  `RetainedSizeCandidateSelector` fallback walk (used only when the tree can't answer a target
  exactly) is unaffected — it already only runs for tree misses.
- Deleted the now-obsolete §11.4 M4 measurement test
  (`GCRootAnalyzerUncappedRealDumpTests.cs`) — its result is preserved in the Q5 note above.

### 9.17 Collection — **AMBER** ⚠️ PARTIALLY IMPLEMENTED

[CollectionAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs) ·
[CollectionAnalysisOptions.cs](../../src/DumpDetective.Core/Options/CollectionAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `Profile` | `Balanced` | — | **delete** (§8 item 3) |
| `PathAnalysisTopN` | 5 | 4 | delete with the dominator move |
| `ReferenceChainOptions` (nested) | — | inherits §9.20 | **blocked on ReferenceChain** |
| `TopWastefulCollectionsToShow` | 50 | 1 — rows | move to render |
| `WasteThresholdBytes` | 10 KB | 5 | keep |
| `IncludeQueueAnalysis` | true | toggle | hard-code true |
| `SurfaceProbingExceptions` | false | diagnostics | keep or move to diagnostics options |
| `MaxDegreeOfParallelism` | `ProcessorCount` | concurrency | keep — orthogonal |
| `SerializeHeapAccess` | false | thread-safety | keep — orthogonal |

**The only analyzer that reads `AnalysisProfile` at runtime.**
[:1154](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs#L1154) branches on
`options.Profile == AnalysisProfile.Fast`. Replacement is `options.PathAnalysisTopN <= 0` — provably
equivalent today, and the change is behaviour-neutral (§8 item 3). **Land this alone, before
anything else in this analyzer.**

**Q6 — inherits ReferenceChain's gating.** `CollectionAnalysisOptions` embeds a
`ReferenceChainOptions` and calls `ReferenceChainAnalyzer.IsNoisyType(type, refChainOptions.SkipArrays)`
at [:1191](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs#L1191). Collection cannot
be exact before ReferenceChain is (§9.20).

**Concurrency knobs are not in scope.** `MaxDegreeOfParallelism` and `SerializeHeapAccess` bound
neither work nor rows — they are execution policy. Keep them; just stop varying them by tier.

#### Implementation notes (as shipped)

- **`Profile`/`AnalysisProfile` branch replaced exactly as prescribed.**
  `options.Profile == AnalysisProfile.Fast` → `options.PathAnalysisTopN <= 0` in
  `CollectionAnalyzer.PopulateRootDescriptions`; provably equivalent since the deleted `Fast`
  preset always set `PathAnalysisTopN = 0`. `Profile` field, `Preset`/`Default`, and
  `CollectionAnalysisOptionsModel.Profile` all deleted; `ConfigurationResolver.BuildCollectionFromConfig`
  switched to the by-now-standard `ApplyOverrides(new CollectionAnalysisOptions(), model)` pattern
  (no more `ResolveAnalyzerProfile`/`Preset` call).
- **`TopWastefulCollectionsToShow` and `PathAnalysisTopN` recategorized from Category 1/4 ("move
  to render" / "delete with the dominator move") to Category 5 ("keep, real work-scoping
  thresholds") — a judgment call made in this pass, overriding the original audit.** Confirmed via
  code, not assumption: `TopWastefulCollectionsToShow` sizes `AddToTopWasteful`'s bounded top-K
  accumulator *during* the streaming per-segment scan (`_topCapacity = Math.Max(1,
  Math.Max(TopWastefulCollectionsToShow, PathAnalysisTopN))`), not a post-hoc truncation of an
  already-complete list — the scan cannot retain every wasteful collection found across a 25GB
  heap in memory, so a bounded top-K selection during the single pass is the CLAUDE.md-mandated
  streaming pattern, not a silent-truncation defect. `PathAnalysisTopN` bounds how many of those
  top items get an expensive per-item `RootPathFinder` search — the same "bounds real work, not
  display rows" shape as ReferenceChain's own `TopCount` (§9.20), and StaticRootLeak's now-deleted
  `MaxRootsToReport` before it turned out full-population computation was actually affordable
  there (§9.14) — the difference here is the underlying per-item work (a graph search) is
  expensive enough that doing it unconditionally for the whole wasteful-collection population
  isn't the same easy win. Neither knob was deleted; both stay, no longer tier-varying.
- **`IncludeQueueAnalysis` hard-coded to always-true** (deleted from options): both call sites
  (`OnHeapEntry`'s single-threaded path and `RunParallelCollectionAnalysis`'s parallel path) had
  `if (kind == CollectionKind.Queue && !_options.IncludeQueueAnalysis) return;` — removed outright,
  queue analysis always runs now.
- **`ReferenceChainOptions` (nested) — turned out to be entirely dead, not just "blocked."**
  Caught after this section first shipped: `CollectionAnalyzer.PopulateRootDescriptions`'s
  `RootPathFinder` search is actually configured from `_refChainOptions`, populated in
  `AnalyzeAsync` from the **top-level** `context.AnalysisOptions.ReferenceChain` — the same shared
  `ReferenceChainOptions` instance `ReferenceChainAnalyzer` itself uses — never from
  `CollectionAnalysisOptions.ReferenceChainOptions`. Grepped every read site to confirm: the
  embedded property was only ever *written* (config merge in `ApplyOverrides`/`Validate`/
  `MergeCollectionModel`), never *read* by the analyzer. Deleted outright — property, its config
  model field, and its three merge/override call sites — same "confirmed no consumer anywhere" bar
  as `DependentHandleAnalysisOptions` (§9.6) and this section's own already-deleted `Profile`.
  Collection's root-path descriptions still inherit §9.20's residual AMBER limitation
  (`LargeFanoutThreshold`/`MaxCandidateNodes`/`MaxRootExpansionDepth` kept as real search-layer
  caps on the shared `ReferenceChainOptions`) — that part of the original note still holds, just
  via the correct (top-level) options instance rather than the dead embedded copy.
- `WasteThresholdBytes`/`SurfaceProbingExceptions`/`MaxDegreeOfParallelism`/`SerializeHeapAccess`
  kept exactly as audited (Category 5 / orthogonal execution policy), just no longer tier-varying
  now that `Preset`/`Default` are gone.
- No section-builder/finding-generator/trend-comparer changes needed — none referenced the deleted
  fields or added a redundant display-layer cap.
- Test suite: 642 passed, 22 skipped, 0 failed. Two `ConfigurationResolverTests.cs` tests that
  asserted profile-scaled `Collection.PathAnalysisTopN`/`Collection.Profile` were updated to the
  new single-tier default (5) — expected fallout of deleting profile variance for this analyzer,
  not a regression.
- **Not touched, out of scope:** `BenchmarkSuite1` has pre-existing compile drift from earlier,
  already-shipped sections (e.g. `ModuleAnalysisOptions.Default`, deleted in §9.3) — it has never
  been part of this effort's build/test verification loop across any of the 17 sections implemented
  so far, so fixing its accumulated drift is a separate cleanup, not folded into this section.

---

### 9.18 Dominator — **GREEN** ✅ IMPLEMENTED (mostly nothing to do)

[DominatorAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs) ·
[RetentionOptions.cs](../../src/DumpDetective.Core/Options/RetentionOptions.cs)

**`RetentionOptions` belongs to this analyzer alone** — confirmed by grep, no other analyzer
consumes it. (An earlier pass audited "Retention" as a second, separate analyzer under §9.22; that
row was a duplicate and its findings are folded in here — see the roster correction above.)

Already computes exact retained sizes for the entire reachable graph via the dominator tree. Most of
the options surface is genuinely done. Two groups of knobs still need action:

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxLeakScanObjects` | 2,000,000 | 3 | delete |
| `MaxReferenceAddresses` | 1,000,000 | 3 — **memory** | restructure, don't delete |
| `MaxRootPathCandidateNodes` | 5,000 | 4 | delete with the dominator-lookup move |
| `MaxRootPathCandidateDepth` | 8 | 4 | delete |
| `MaxRootPathExpansionDepth` | 12 | 4 | delete |
| `RootPathLargeFanoutThreshold` | 100 | 4 — **excludes paths** | see below |
| `HighReferenceThreshold` | 50 | 5 | keep |
| `TopFinalizerTypesToShow` | 10 | 1 — rows | **dead (V4), delete** — zero references in any analyzer, including `DominatorAnalyzer.cs` |
| `TopHighlyReferencedObjectsToShow` | 15 | 1 — rows | move to render |
| `EnableExactDominatorTree` | true | capability | **keep**, already correct |
| `ExactDominatorTreeMemoryBudgetBytes` | 20 GB | 3 — **memory** | **keep**, already correct |

#### Q7 — `RootPathLargeFanoutThreshold` is not a budget, it is an exclusion — kept, by decision (D3)

Documented as *"Fanout threshold above which a reference path is considered 'large' and **skipped** to
avoid exploring extremely high-connectivity clusters."* Paths through any object with more than 100
referents are not searched more cheaply — they are **not searched**. Those objects are static caches,
singletons and interned strings: the most likely retainers in a real leak.

**Resolved by D3: keep this one as-is.** Here it only bounds the `PopulateEvidence` path-display
search used for this analyzer's "highly referenced objects" evidence text — the retained-bytes
totals this analyzer reports come from the exact dominator tree, computed independently, so an
un-found display path only costs a confidence downgrade (`searchTruncated`), not a wrong number.
Removing it would risk multi-million-node single-query blowups on exactly the hub objects (static
caches, singletons, interned strings) this evidence search is most likely to hit, for a purely
cosmetic payoff. Contrast with `ReferenceChainAnalyzer` (§9.20), where the equivalent cap **is**
being removed, because there the path is the reported result, not decoration on one.

#### Q7 — `MaxLeakScanObjects = 2,000,000` against an 87 M-object heap

Bounds objects receiving full reference-field enumeration, setting `ObjectScanCapped`. On the
reference dump (§9.19's 87M-object figure) that is ~2.3% of objects. The XML doc is also garbled —
the sentence *"When the limit is reached, ObjectScanCapped is set to true in the retention analyzer
result. is set to `true` and a confidence note is emitted"* has a lost fragment (F5).

**Its two flags were deliberately excluded from the profile system**, and the XML doc at
[RetentionOptions.cs:51-53](../../src/DumpDetective.Core/Options/RetentionOptions.cs#L51-L53) says why:

> Deliberately **not** branched per `AnalysisProfile` in `Preset` below — the profile system is
> expected to be simplified/consolidated later, and this flag is meant to stay independent of
> whatever shape it ends up in.

`EnableExactDominatorTree` (true) and `ExactDominatorTreeMemoryBudgetBytes` (20 GB) are therefore
already in the target state: a standalone capability flag plus a **memory** budget, neither tied to a
tier. This is the pattern §3 prescribes, already implemented once, and it should be the template for
anything Category 4 needs after the migration.

`ExactDominatorTreeMemoryBudgetBytes` is a **Category 3 memory bound and must be kept.** It is the
one cap in the codebase that is doing exactly the right job.

#### Implementation notes (as shipped)

**Audit table was partly stale before this pass even started** — two rows had already been
resolved by earlier, non-§9-sequence work:
- `ExactDominatorTreeMemoryBudgetBytes` — **already deleted**, not "keep." Per
  [dominator-tree-phase1-integration.md §5](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
  "Budget removed, not recalibrated" — the calibrated byte-cost model was fit to two dumps under a
  memory profile that predated Stage A/B sharing a walk, and its abort path risked leaving Stage
  A's reverse-edge index silently incomplete. Replaced by two root-cause fixes (isolated try/catch
  around the walk phase, `ChunkedBuffer<T>.Add` throwing before overflowing `int.MaxValue`) instead
  of a heuristic. Confirmed gone from `RetentionOptions.cs` — no action needed here, just noting the
  audit table was written before this shipped.
- `MaxLeakScanObjects`'s Q7 concern ("2.3% of an 87M-object heap gets scanned") — **the concern
  itself no longer applies to the primary path.** `DominatorAnalyzer.BuildLeakSignalsFromReverseIndex`
  (the reverse-index-backed leak-signal pass, now the default whenever a reverse index exists — i.e.
  effectively always) is exhaustive by construction, per its own code comment: "there's no per-object
  ClrMD work to budget via `MaxLeakScanObjects`... the scan is exhaustive over every recorded child...
  never capped." `MaxLeakScanObjects`/`MaxReferenceAddresses` only still apply to
  `AnalyzeObjectsPass`, the live-heap fallback used solely when no reverse index is available (rare,
  degraded mode) — kept there as a legitimate fallback safety valve, documented as such in the
  options class now. Both fields are *also* separately reused as the BFS-breadth bound
  (`maxBreadth`) for the top-K retained-size walks — a second, legitimate, unrelated use, also kept.

**Confirmed-dead knob deleted:** `TopFinalizerTypesToShow` — grep confirmed zero references in
`DominatorAnalyzer.cs` or anywhere else (matches the original V4 audit finding exactly). Deleted
from `RetentionOptions` and both non-Balanced presets.

**Audit-inconsistency resolved: `MaxRootPathCandidateNodes`/`MaxRootPathCandidateDepth`/
`MaxRootPathExpansionDepth` recategorized from Category 4 ("delete with the dominator-lookup move")
to Category 5, joining `RootPathLargeFanoutThreshold` under the same D3 reasoning the audit had
already applied to that one field.** All four fields populate the *same* `RootPathSearchLimits`
struct at the *same* call site (`PopulateEvidence`) for the *same* purpose — a purely decorative
root-path-evidence-text search, not the reported retained-byte numbers (which come from the exact
dominator tree independently). D3 already concluded removing the fanout threshold "risks
multi-million-node single-query blowups... for a purely cosmetic payoff" and chose to keep it; the
audit table just never noticed the other three fields of the same struct were subject to identical
reasoning and needed the same conclusion. Fixed the inconsistency rather than deleting three of
four fields from one struct and leaving it half-bounded.

**`TopHighlyReferencedObjectsToShow` recategorized from Category 1 ("move to render") to Category
5 ("keep, real work-scoping threshold") — the same audit-blind-spot pattern already found in
Collection (§9.17) and ReferenceChain (§9.20).** Confirmed via code: it sizes the in-scan top-K
`PriorityQueue` in `BuildLeakSignalsFromReverseIndex`, and separately determines how many
candidates get an expensive per-item retained-size BFS walk (`Analyze`'s `topCount`) and root-path
evidence search (`PopulateEvidence`) — not a post-hoc display truncation of an already-complete
list. This is the **third** instance of this exact pattern found this session; worth treating as a
recurring blind spot in the original audit rather than three coincidences, same as the "configured
value ≠ applied value" pattern called out repeatedly earlier in this document.

**Profile variance stopped for the whole class, matching every other migrated section.**
`RetentionOptions.Preset`/`Default` deleted; all remaining fields (`TopHighlyReferencedObjectsToShow`,
`HighReferenceThreshold`, `MaxReferenceAddresses`, `MaxLeakScanObjects`,
`MaxRootPathCandidateNodes`/`CandidateDepth`/`ExpansionDepth`, `RootPathLargeFanoutThreshold`,
`EnableExactDominatorTree`) collapsed to single plain-field defaults at their former Balanced
values — none of them were deleted, but none vary by tier anymore either.
`ConfigurationResolver.BuildMemoryLeakFromConfig` switched to the section/options-override
Preset-bypass pattern used throughout this effort.

**Net result: zero deletions beyond the one confirmed-dead field, because the audit's other seven
"action" items were either already resolved outside this pass, or turned out — on inspection — to
be legitimate kept thresholds under reasoning the audit itself had already established for a
sibling field.** This is the first section this session where "mostly nothing to do" held up
almost exactly as originally assessed.

Test suite: 642 passed, 22 skipped, 0 failed. Three `ConfigurationResolverTests.cs` tests that
asserted profile-scaled `RetentionOptions` values (`TopHighlyReferencedObjectsToShow`,
`MaxLeakScanObjects`, `HighReferenceThreshold`) were updated to the new single-tier defaults —
expected fallout of deleting profile variance, not a regression.

---

### 9.19 EventLeak — **AMBER** ⚠️ PARTIALLY IMPLEMENTED

[EventLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs) ·
[EventLeakOptions.cs](../../src/DumpDetective.Core/Options/EventLeakOptions.cs)

Sixteen knobs; eight are severity-scoring weights (Category 5, all keep).

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxGroupsToEnrich` | 25 | 4 | delete with the dominator move |
| `MaxEvidenceEnrichmentMs` | 2,000 | **wall-clock budget** | **keep — see below** |
| `MinSubscribers` | 0 | 5 | keep |
| `PublisherSubscriberThreshold` | 1 | 5 | keep |
| `LifetimeMismatchProbeLimit` | 50 | 3 | delete |
| `IncludeNonLeakingEvents` | false | toggle | hard-code true under exactness |
| `EnableLowIncomingRefsCheck` | false | toggle | see below |
| `TopSubscriberTypesToShow` | 5 | 1 — rows | **dead (V4), delete** — `EventLeakAnalyzer.cs` uses a hardcoded `TopCorrelationEntries = 20` instead |
| `TopDetailedInstancesPerGroup` | 5 | 1 — rows | move to render |
| `EnableDiagnostics` | true | diagnostics | **dead (V4), delete** — never read by `EventLeakAnalyzer.cs` |
| 6 x `Severity*Bonus` / `SeveritySubscriberLogScale` | — | 5 | keep |

#### This analyzer already has the mechanism the whole plan needs

`MaxEvidenceEnrichmentMs = 2000` is described as a *"wall-clock budget for the entire enrichment loop
across all enriched instances."* **It is the only time-based budget anywhere in the options surface**
— every other bound in the codebase is a node/object/type count (§6 and the earlier
`BidirectionalGraphSearch` finding).

That makes it the working precedent for the global wall-clock budget recommended in §12: bound the
*time*, report what didn't finish, and let the work itself be unbounded. Keep it, and consider
promoting the pattern rather than the specific knob.

**`EnableLowIncomingRefsCheck` is off by default with a documented reason:** *"extremely expensive on
large heaps (25 GB+, 87 M objects)."* That comment also gives us a hard number for the reference dump
— **87 M objects** — worth carrying into the Q5 estimates. Under exactness this check becomes
affordable *only* if served from the reverse index rather than a heap scan; treat it as part of the
§10 workstream, not a flag flip.

#### Implementation notes (as shipped)

**`MaxGroupsToEnrich` deleted — genuinely resolvable, unlike most Category-4 "delete with the
dominator move" items elsewhere, because this analyzer already has the alternative safety
mechanism the rest of the plan wants everywhere: the wall-clock budget.** `PopulateEvidence`'s
`MaxEvidenceEnrichmentMs` guard already bounds total enrichment time regardless of how many groups
are eligible; the group-count pre-filter was therefore a redundant second bound, not a load-bearing
one. Deleted `BuildEnrichmentGroupKeys` and its four dedicated unit tests; every leak instance is
now enrichment-eligible, processed in the caller's existing severity-descending order, with the
time budget as the sole remaining safety valve — a strictly better shape (priority order + time
budget, not an arbitrary group-count cutoff) than what was there before.

**`IncludeNonLeakingEvents` hard-coded to always-true, `MinSubscribers` deleted (not kept) — a
correction to the original audit.** The audit categorized `MinSubscribers` as Category 5 "keep,"
independent of `IncludeNonLeakingEvents`'s "hard-code true." But grep showed `MinSubscribers` had
exactly one behavior: `if (!includeNonLeaking && subs.Count < minSubs) continue;` in both
`EventLeakFastScanner.ProcessInstanceFields` and `EventLeakAnalyzer.SweepRegistryStatics` — a real
completeness filter (silently dropping events below the threshold), not a severity/display concern.
Hard-coding `includeNonLeaking = true` makes that filter permanently unreachable, which makes
`MinSubscribers` dead by construction. Deleted both together rather than leaving a config field with
zero remaining behavior.

**`TopDetailedInstancesPerGroup` recategorized from Category 1 ("move to render") to Category 5
("keep, real work-scoping threshold") — the fourth instance of this exact pattern found this
session** (after Collection §9.17, ReferenceChain §9.20, Dominator §9.18). Confirmed via code:
`AddToAccumulator` uses it to size `GroupAccumulator.TopInstances`, a genuine in-scan top-K
structure with min-replacement, populated during the streaming heap pass — not a post-hoc
truncation. `TopSubscriberTypesToShow` and `EnableDiagnostics` deleted outright — grep confirmed
zero references anywhere outside the options class, matching the original V4 audit finding exactly
for both.

**Real bug found and partially fixed: `LifetimeMismatchProbeLimit` bounded two unrelated
operations with very different cost profiles, and the audit's blanket "Category 3, delete" applied
cleanly to only one of them.**
- `CheckLifetimeMismatch`/`CheckLifetimeMismatchDirect`'s generation check reads
  `SegmentKindMapper.ResolveGeneration`/an equivalent direct segment lookup — an O(1) operation
  regardless of heap scale. **Uncapped**: both now probe every subscriber, not a capped sample —
  a genuine, low-risk exactness win, since this check runs unconditionally (not gated by any
  toggle).
- `HasLowIncomingRefsSignal` (only reachable when `EnableLowIncomingRefsCheck` is explicitly
  enabled) calls `CountIncomingRefs`, which turned out to be **not just slow but wrong**:
  it samples the *first* ~500 objects from `heap.EnumerateObjects()` in arbitrary enumeration
  order and checks each for a reference to the target — on an 87M-object heap this is essentially
  never the real referrer. Fixing this properly means rewiring it through
  `IBackwardReferenceProvider.TryGetParents` for an exact O(1) lookup, which now exists and is used
  throughout this codebase — but `EventLeakFastScanner` (the per-object hot-path scanner this
  check runs from) currently has no cache/provider reference at all, and this check runs once per
  *every* detected leak instance during the main scan, not a bounded top-N like the evidence-search
  cases resolved elsewhere this session. Given the plumbing cost and the lack of a wall-clock-scale
  measurement for "reverse-index lookup × every leak instance across a 25GB heap," this was judged
  out of scope for this pass — **left broken, but the bug is now documented in the options class
  itself** (`EnableLowIncomingRefsCheck`'s XML doc) so it doesn't need rediscovering. `LifetimeMismatchProbeLimit`
  kept on the options class solely because this deferred path still needs it.
- `EnableLowIncomingRefsCheck` itself: kept as an opt-in toggle, default unchanged (`false`) — not
  hard-coded true, unlike `IncludeNonLeakingEvents`, precisely because of the above.

**Profile variance stopped, matching every other migrated section.** `EventLeakOptions.Preset`/
`Default` deleted; `TopDetailedInstancesPerGroup`, the six `Severity*Bonus` fields,
`SeveritySubscriberLogScale`, `SeverityLowIncomingRefsBonus`, `EnableLowIncomingRefsCheck`,
`LifetimeMismatchProbeLimit`, `LifetimeMismatchGen01Threshold`, `PublisherSubscriberThreshold`, and
`MaxEvidenceEnrichmentMs` all collapsed to single plain-field defaults at their former Balanced
values. `ConfigurationResolver.BuildEventLeakFromConfig` switched to the Preset-bypass pattern.
`StartupValidator.ValidateEventLeakOptions` deleted outright — its only check
(`MinSubscribers >= 0`) no longer has a field to validate.

**Two more confirmed-dead CLI-only knobs found and deleted, same bar as §9.20's discoveries:**
`--reference-chain-*`'s siblings this time — `--event-leak-min-subscribers` (`RootCommandBuilder`
option + `AnalysisCommandRequest`/`CliArguments` plumbing) had zero consumers anywhere; it was
threaded from the CLI parser into the request record and then never read again.

Test suite: 638 passed (4 fewer than before — the deleted `BuildEnrichmentGroupKeys` tests), 22
skipped, 0 failed. Several `ConfigurationResolverTests.cs` assertions exercising the old
profile-scaled/toggle-gated behavior were updated to the new single-tier, always-inclusive defaults.

---

### 9.20 ReferenceChain — **AMBER** ⚠️ PARTIALLY IMPLEMENTED (was RED, no longer RED)

[ReferenceChainAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs) ·
[ReferenceChainOptions.cs](../../src/DumpDetective.Core/Options/ReferenceChainOptions.cs)

**RED, for two independent reasons.**

#### 1. It contains a second, parallel profile system

`ReferenceChainSearchMode { Fast, Balanced, Deep }` duplicates `AnalysisProfile`'s tiers, and the
preset maps one onto the other: `AnalysisProfile.Fast → SearchMode.Fast`,
`Full → SearchMode.Deep`. Deleting `AnalysisProfile` does not delete this; it just orphans the
mapping. A decision is required — collapse `SearchMode` to the single exact strategy, or keep it as a
genuine algorithm selector independent of tiers.

**Three-layer resolution makes the applied value unreadable.** `MaxCandidateNodes`,
`MaxCandidateDepth` and `MaxRootExpansionDepth` all default to `0` meaning *"use mode default"*,
resolved through `Resolved*` properties
([:68-84](../../src/DumpDetective.Core/Options/ReferenceChainOptions.cs#L68-L84)). So the effective
value is `preset → explicit-or-zero → mode default`, and a user who sets `0` gets 50,000. Fourth
instance of *configured value ≠ applied value* (§9.16).

#### 2. Q6 — exactness here is definitionally gated

"Exact" for this analyzer means *the true shortest reference chain*, which requires a complete
reverse graph. `MaxParentsPerChild = 10,000` (§6.2) means the graph is not complete for
high-fanout objects — and `LargeFanoutThreshold = 100` prunes those same objects again at the
analyzer layer. **This analyzer cannot be called exact until §6.2 is resolved.** It stays RED
regardless of how many knobs are deleted.

| Knob | Default | Category | Action |
|---|---:|---|---|
| `SearchMode` | `Balanced` | parallel profile | **decision required** |
| `MaxCandidateNodes` / `MaxCandidateDepth` / `MaxRootExpansionDepth` | 0 → mode | 4 | delete with the strategy collapse |
| `MaxPathDepth` | 25 | 4 | delete |
| `LargeFanoutThreshold` | 100 | 4 — **excludes paths** | see §6.2 |
| `SkipArrays` | true | pruning | **confirmed by V3/§11.3: real traversal pruning, not presentation** — force `false`, folded into D1's exact-search work |
| `TopCount` / `FallbackTopCount` | 5 / 10 | 1 — rows | move to render |
| `KnownLeakTypePatterns` | 3 patterns | 5 — heuristic | keep; identical in all three presets (pure duplication) |

#### Implementation notes (as shipped)

**Reason #1 (parallel profile system) — fully resolved.** `ReferenceChainSearchMode` enum and its
`Preset`/`Default`/three `Resolved*` mode-dependent properties deleted outright.
`ReferenceChainOptions` collapsed to plain fields at their former Balanced-preset values — the
codebase always ran through `IndexBackedBidirectionalSearch` when a reverse-edge index is
available anyway (which, per Stage A now always being built, is effectively always), so the mode
enum never actually selected between two live implementations — it only varied numeric budgets by
tier, which the plan's own §1 goal explicitly wants stopped.

**Reason #2 (Q6, graph completeness) — the specific blocker (§6.2's `MaxParentsPerChild`) is
resolved, but a related, undocumented one remains, and that's why this stays AMBER, not GREEN.**
§6.2 shipped: the reverse-edge index has no fan-in cap (confirmed in
[dominator-tree-phase1-integration.md §3](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md)
— `MaxParentsPerChild` deleted outright, real measured worst-case hub fan-in 346K on a 3.3GB dump,
10.76M on a 25.6GB dump). But `LargeFanoutThreshold` is a **separate** cap, at the *search* layer,
not the *index* layer — [IndexBackedBidirectionalSearch.cs](../../src/DumpDetective.Analysis/Traversal/IndexBackedBidirectionalSearch.cs)'s
forward/backward neighbor generators both stop expanding a node past `LargeFanoutThreshold`
matches, even though the underlying index could answer for all of them. The 10.76M-fan-in
measurement above is exactly why this can't simply be deleted: an on-demand, per-query search
hitting a hub that large would need to materialize 10.76 million neighbors in a single BFS step,
for a query that runs a handful of times per analysis (once per `TopCount` type sample) — a
fundamentally different cost profile than the index build (a one-time linear pass). `MaxCandidateNodes`
and `MaxRootExpansionDepth` (the search's own node/depth budget) are kept for the same reason.
**None of these three were deleted; they were recategorized from the original audit's "Category 4,
delete with the strategy collapse" to Category 5 (real, kept, semantic search-budget thresholds)**
— a judgment call made in this pass, not the original audit's assumption. So "the true shortest
reference chain" still isn't guaranteed: a shorter path could exist through a fanout-pruned hub, or
beyond the node/depth budget. This is why the analyzer moves from RED to **AMBER**, not GREEN.
- `SkipArrays` deleted per V3/§11.3's explicit instruction ("force false... real traversal pruning,
  not presentation"). `IsNoisyType` no longer takes a `skipArrays` parameter; arrays are never
  treated as noise now (previously excluded whenever the option was `true`, including at both
  non-Fast presets already, but *not* consistently — deleting it makes the behavior uniform and
  matches the source doc's "fold into D1's exact-search work" verdict).
- `MaxPathDepth` — a **newly-discovered dead knob**, not part of the original audit table (which
  only covered fields actually read by `ReferenceChainAnalyzer`). Confirmed via grep: set in all
  three presets, forwarded into `ExecutionPolicy.ReferenceChainMaxPathDepth` by
  `ConfigurationResolver.BuildExecutionPolicy`, but that `ExecutionPolicy` field (and its two
  siblings, `ReferenceChainFastModeMaxDepth`/`ReferenceChainMaxPathSearchObjects`) were never read
  by any analyzer — the `ExecutionPolicy policy` parameter threaded through
  `ReferenceChainAnalyzer.AnalyzeTopTypes`/`TryFindAnyRootPath`/`TryFindAnyRootPath_Bidirectional`
  was entirely unused. Deleted all three `ExecutionPolicy` fields, the unused `policy` parameter
  threading, and the two CLI-only flags that fed them
  (`--reference-chain-top-count`/`--reference-chain-max-path-search-objects`, and their
  `AnalysisCommandRequest`/`CliArguments`/`RootCommandBuilder` plumbing) — same "confirmed no
  consumer anywhere" bar used for `DependentHandleAnalysisOptions` (§9.6) and
  `TopFinalizerTypesToShow` (§9.18).
- `TopCount` **recategorized from Category 1 ("rows, move to render") to Category 5 ("keep, it's a
  work-scoping choice")** — another judgment call overriding the original audit. Unlike a typical
  display-row cap, `TopCount` bounds how many top-by-size types get an expensive bidirectional
  graph search run at all; removing it would mean running that search for potentially thousands of
  distinct heap types, not just re-displaying an already-cheap, already-complete computation.
  `FallbackTopCount` deleted — it was purely the companion to `TopCount`'s old "0 means use
  fallback" sentinel pattern, dead once `TopCount` became a plain non-zero default.
- `KnownLeakTypePatterns` kept as audited (Category 5).
- Section builder (`ReferenceChainSectionBuilder.cs`): removed `MaxTraces`/`MaxChains`/the
  retained-types-8 local caps — analyzer output is already bounded by the small `TopCount` default,
  so these were a redundant second truncation layer (§11.2 D5). The analyzer's own
  `sampleReferenceChains.Count < 5` cap (a handful of illustrative example chains for narrative
  text, not the core per-type data) was left alone — same category as AsyncStateMachine's top-3-
  states truncation (§9.13), a display-shape decision rather than a completeness cap.
- `CollectionAnalysisOptions`'s three presets (its embedded `ReferenceChainOptions`) updated to
  drop the now-deleted `SearchMode`/`MaxPathDepth` fields — required to compile, not a behavior
  change beyond what this section already did. `CollectionAnalyzer.PopulateRootDescriptions`
  updated for the `Resolved*` → plain-field rename and the `SkipArrays` deletion. Collection's own
  audit (§9.17) is a separate pass.
- Test suite: 642 passed, 22 skipped, 0 failed. Several `ConfigurationResolverTests.cs` assertions
  that exercised the old profile-scaling behavior (`SearchMode`, tier-varying `TopCount`/
  `MaxRootExpansionDepth`) were updated to match the new single-tier defaults — this is expected
  fallout of deleting profile variance, not a regression.

---

### 9.21 TimerLeak — **GREEN** ✅ IMPLEMENTED, no options class

[TimerLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs)

Has **no options class at all** and does not appear in `AnalysisOptions`. It calls
`finder.TryFindAnyRootPath(...)` directly
([:158](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs#L158)) and consumes
`searchTruncated`.

Zero `AnalysisOptions` knobs to delete — it inherits only `RootPathFinder` defaults now that §6.2
(`MaxParentsPerChild`, deleted) and §6.3 (`RootSetCache`'s 256-frame cap, scoped to a cosmetic report
label, not root discovery) are both resolved. It is the cleanest demonstration that this refactor is
not only about the options surface: an analyzer with no configuration at all was still not exact,
purely from shared-traversal bounds below the options layer.

**Was used as the canary.** Because it has no knobs, its output changing after the shared traversal
became exact was attributable purely to that, not to any per-analyzer option change.

#### A dead-sampler defect found outside the original audit scope, and fixed

`TimerLeakAnalyzer` implements `ITypedResourceCandidateSource` (real, used for `IsCandidateType`) and
previously also declared `ITypedResourceInstanceSampler<TimerStateSnapshot>` — `MaxStateSamplesPerType
= 100`, `TopSampleCap = 20` — to satisfy the same "typed-resource quartet" contract as
`DbConnectionAnalyzer`/`WcfChannelAnalyzer`/`HttpObjectAnalyzer` (§9.32-9.34). Unlike those three,
Timer never wired into the real mechanism: it isn't an `IHeapIndexScanParticipant`, so
`TypedResourceScanDriver.CreateSampler`/`TryGetSample` (the reserve-slot + top-N `InstanceStateSampler`
machinery those two properties actually parameterize) were never called for it.
`PopulateEvidence` instead fetched exactly one address per type via `cache.GetSampleInstanceAddress`
and sampled it directly — so `MaxStateSamplesPerType`/`TopSampleCap` were dead, satisfying an interface
contract they never fulfilled. Two further defects surfaced from tracing this:

- The `List<TimerStateSnapshot>(sampler.MaxStateSamplesPerType)` capacity hint pre-sized a list to 100
  for something that only ever held 0 or 1 item — harmless (small type), but another instance of
  §11.6's "configured value ≠ applied value" pattern.
- The evidence sample's `HeapEntry` was fabricated as `new HeapEntry(address, 0, 0)` — the 3-arg ctor
  defaults `Generation = -1` (the "unresolved" sentinel per
  [HeapEntry.cs:9-16](../../src/DumpDetective.Analysis/Indexing/HeapEntry.cs#L9-L16)) — so
  `TimerStateSnapshot.Generation` was always `(uint)(-1)` = `4294967295`, never a real value.
- Worse than either: `TimerStateSnapshot`'s period/callback-owner/generation was computed but **never
  consumed anywhere** — not in `TimerLeakSectionBuilder`, not in `TimerLeakFindingGenerator`, not in
  the trend comparer. Only `Evidence` (the root path) was read; the whole sampling branch was dead
  computation end-to-end.

**Fix shipped:** stopped implementing `ITypedResourceInstanceSampler<TimerStateSnapshot>` on
`TimerLeakAnalyzer` (deleted the two dead properties); the interface's XML doc corrected to list the
three real heap-scan-backed members (`DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
`HttpObjectAnalyzer`) and note Timer's different, direct-sample shape. `TrySample` became a plain
private static `TrySampleTimerState(ClrHeap, ulong, string)`, called directly — no more `HeapEntry`
fabrication. Generation is now resolved for real via the existing
[`GenerationTagResolver.Resolve`](../../src/DumpDetective.Analysis/Traversal/Dominator/GenerationTagResolver.cs)
helper (already used by Stage B persistence), replacing the bogus sentinel with an actual
`GenerationTag` (Gen0/Gen1/Gen2/LOH/POH/Frozen/Unknown). Rather than deleting the now-dead-code
sampling branch outright, **`Samples` was wired into the report**: `TimerLeakSectionBuilder`'s
"Timer-related objects by type" table gained three columns — Sample Period, Sample Callback Owner,
Sample Gen — populated from `TimerObjectTypeSummary.Samples[0]` when present (only
`System.Threading.TimerQueueTimer` yields a sample; other rows show `—`). No `TimerLeakDomainResult`
count/total changed — this is additive report detail, not an exactness fix to the headline numbers.

---

> **9.22 removed** — was a duplicate audit of the same analyzer covered in §9.18 (Dominator). See the
> roster correction near the top of this document.

### 9.23 Thread — **GREEN** ✅ IMPLEMENTED

[ThreadAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ThreadAnalysisOptions.cs)

Ten knobs plus an enum plus **a third tier system**.

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxFramesForThreadScan` | **8** | 3 | delete |
| `MaxStackRootsToCount` | 256 | 3 | delete |
| `MaxThreadsToCaptureSnapshots` | 20 | 3 | delete |
| `MaxSampledStackSnapshots` | 20 | 2 — sampling | delete |
| `SamplingSeed` | 0 | 2 — sampling | delete with the sampling |
| `IncludeStackSamples` | true | toggle | hard-code |
| `AsyncChainDetection` | `Full` | mode enum | collapse to `Full` |
| `PrewarmCacheInBackground` | false | execution policy | keep — orthogonal |
| `DetectWaitPatterns` | true | toggle | hard-code true |
| `MaxTopHotspots` | 10 | 1 — rows | move to render |

#### `AdaptForSize` is a third scaling layer, invisible to the user — resolved by D8: delete it, and it was double-applying

`ThreadAnalysisOptions.AdaptForSize(options, DumpSizeTier)` divides
`MaxThreadsToCaptureSnapshots` and `MaxSampledStackSnapshots` by **4 (Large) / 2 (Medium) / 1**.

So the effective value is `preset value ÷ size divisor` — and the divisor is derived automatically
from dump size, not configured. On a large dump the Balanced default of 20 thread snapshots becomes
**5**. Nothing surfaces this. Fifth instance of *configured value ≠ applied value* (§11.6).

**Worse than described: this divisor is applied twice.** `AdaptForSize` runs in
[AnalyzerExecutionService.cs:52](../../src/DumpDetective.Cli/Execution/AnalyzerExecutionService.cs#L52)
before `ThreadAnalyzer` ever sees the options, but `ThreadAnalyzer.ComputeSamplerCapacity`
([ThreadAnalyzer.cs:22-42](../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L22-L42))
independently looks up the same size tier and re-applies the same divisor to the *already-divided*
value it receives — Large-dump Balanced default 20 → 5 (AdaptForSize) → 1 (ComputeSamplerCapacity), a
16x reduction instead of the intended 4x. **Resolved by D8 (§11.2): delete `AdaptForSize` outright.**
It was never a memory guard — `ThreadWithStackTrace` is small and fixed-size (bounded by
`MaxFramesForThreadScan`, also deleted), and the counts/totals this analyzer reports already come from
enumerating every thread regardless of this cap. `MaxSampledStackSnapshots`/`MaxThreadsToCaptureSnapshots`
become ordinary fixed Category-1 display-example limits, no dump-size scaling. `ComputeSamplerCapacity`'s
`totalThreads / 10` term survives independently as a legitimate "don't oversample a small population"
guard.

**Q7 — `MaxFramesForThreadScan = 8`.** Eight frames per thread, four at Fast. Compare Jit's 200
(§9.10) and `RootSetCache`'s 256 (§6.3): three different frame budgets in three layers, differing by
32x. Any wait-pattern or hotspot conclusion drawn from 8 frames describes the top of the stack only.

**Q7 — thread snapshots are randomly sampled.** `SamplingSeed` exists to make the sample
*deterministic*, not complete — the same "make truncation reproducible" workaround as Boxing's
determinism sort (§9.1) and Module's (§9.3). Third instance.

#### Implementation notes (as shipped)

- **Every knob in the table above is gone except `PrewarmCacheInBackground`.** `ThreadAnalysisOptions`
  shrank to that one field; `Preset`/`Default`/`AdaptForSize` deleted outright (D8's double-applying
  bug — §11.6 F8 — is moot once the method itself is gone). `ConfigurationResolver`'s
  `BuildThreadAnalysisFromConfig` rewritten to the section-overrides-on-`new ThreadAnalysisOptions()`
  one-off pattern established by GCGeneration/SegmentReservation (§9.4/§9.9), not the Boxing-style
  bespoke rewrite — same shape, no CLI-flags-only fallback existed for Thread to replace.
- **`MaxFramesForThreadScan`/`MaxStackRootsToCount` become "walk the whole stack," not
  `int.MaxValue`.** `ThreadAnalyzer.UnboundedFrameCount = 100_000` is a named sentinel, not a true
  unbounded value — chosen because M8 (§11.4) measured that exact figure end-to-end on a real dump at
  2 ms, and because `ThreadStackScanDispatcher.Run` pre-sizes a reused `List<ClrStackFrame>` with it;
  an `int.MaxValue` capacity hint there would repeat F10's (§11.6) `OutOfMemoryException` bug for a
  different collection. `GetRequiredFrameCount` (every `IThreadStackScanParticipant`, not just Thread)
  now requests this sentinel unconditionally — `ComputeEffectiveMaxFramesForSnapshot`'s
  Full-mode-doubles-the-window logic is gone with it, since there's no longer a window to double: every
  alive thread's whole captured stack feeds wait-pattern/hotspot/async-chain detection directly.
- **`AsyncChainDetectionMode` deleted per D9** — `Disabled`/`CountOnly` collapse away;
  `CountMoveNextDepth` runs unconditionally for every alive thread, and the async-chain thread
  count/max depth are always computed from the (now unbounded) captured stack — no more "widen the
  window in place if Full" branch, since the window was never narrowed to begin with.
- **`MaxThreadsToCaptureSnapshots` deletion makes `TopLockedThreads`/`TopBlockedThreads`/
  `ThreadsWithActiveExceptions`/finalizer-frame lists complete**, matching D5: `BuildDomainResult`
  materializes every thread in each category, `ThreadSectionBuilder`'s existing `STCompact` calls
  already had no `.Take()` truncation of their own, so no render-layer change was needed there — only
  the analyzer-side cap came out.
- **`MaxTopHotspots` deletion → complete ranked hotspot lists**, same shape as every other Category-1
  move in this doc — `TopFrameHotspots`/`TopActiveThreadHotspots` are sorted and emitted whole; no
  `Top*ToShow` render-layer constant was added per D5's amendment (`STCompact`'s uniform default page
  size applies).
- **The reservoir-sampled "Sampled threads" feature was redesigned, not just uncapped — flagged as a
  judgment call, not a mechanical deletion.** The audit table above says "delete" for
  `MaxSampledStackSnapshots`/`SamplingSeed` without D3's later per-consumer nuance (§11.2 D3 came from
  auditing ReferenceChain/TimerLeak's evidence sampling, after this row). Two options existed:
  (a) keep it a small illustrative sample of "everything else" like TimerLeak/GCRoot's evidence paths, or
  (b) follow Category 2 literally and make it a complete, deterministic population. Chose **(b)**,
  consistent with this row's own audit verdict and every other Category-2 knob in this doc: every alive
  thread not already captured by locks/blocked/exceptions is now included, unconditionally, no RNG.
  `ThreadCategorization.SampledThreads` renamed to `OtherThreads` to match (no serialization impact —
  `ThreadDomainResult` never reaches JSON, same D2-established pattern). Because this list is no longer
  capped, rendering it as one `NamedStackTrace` block per thread (the old mechanism) would flood the
  report on a busy process with hundreds of "boring" threads — so it was **also migrated onto
  `STCompact`** (D5's mechanism) as a new "Other threads" table, rather than keeping the old
  one-block-per-thread narrative rendering. `SampledSnapshotCount`/`CapturedSnapshotCount`/
  `SamplingCapacity`/`SamplingSeed` deleted from `ThreadDomainResult` outright — none were read anywhere
  outside `ThreadSectionBuilder`'s now-deleted `sampled_snapshots`/`sampling_capacity`/`sampling_seed`
  key-metric block, and none fed a total.
- **`ComputeSamplerCapacity`, `ReservoirSampler<T>`, and `SampleCandidateIndices` deleted outright** —
  `ReservoirSampler<T>` had no other caller in `src` once Thread stopped using it.
  `AnalyzerExecutionService.BuildContext`'s dump-path-hash seed-derivation block (a `SHA256`-based
  "auto-derive `SamplingSeed` when zero" step, its own `TODO: need to evaluate the need for this`) is
  gone with it — there's no seed left to derive.
- **Test fallout, larger than most rows because Thread had the most preset/sampling-specific test
  coverage of any analyzer audited so far:** deleted `ThreadAnalysisOptionsTests.cs` (pure `Preset`
  behavior, per §8 item 8), `AdaptivePresetTests.cs` and `ThreadAnalyzerSamplerCapacityTests.cs` (both
  tested the now-deleted `AdaptForSize`/`ComputeSamplerCapacity`), `PresetBehaviorTests.cs` (§8 item 8,
  Thread was its only remaining subject), `ThreadAnalyzerSamplingTests.cs` (tested
  `SampleCandidateIndices`), `ReservoirSamplerTests.cs`, and `RunAnalyzersPipelineStageTests.cs` (both
  tests exercised the deleted seed-derivation-from-dump-path behavior; no other assertions in that file
  survived it). Trimmed `ThreadAsyncChainTests.cs` to keep only its still-valid
  `CountMoveNextDepthFromSignatures` test. Deleted
  `ThreadUncappedRealDumpTests.cs` (§11.4 M8's own real-dump test) — its capped-vs-uncapped comparison
  has nothing left to compare now that the caps are gone; the measurement it recorded stays in §11.4 as
  a historical record.

---

### 9.24 ThreadStackCluster — **GREEN** ✅ IMPLEMENTED

[ThreadStackClusterAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ThreadStackClusterAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxFramesPerSignature` | **6** | 2/4 — **changes clustering** | delete |
| `MaxClusters` | 500 | 3 | delete |
| `MaxThreadIdsPerCluster` | 8 | 1 — rows | move to render |
| `TopSignaturesToShow` | 5 | 1 — rows | move to render |
| `TopClustersToShow` | 12 | 1 — rows | move to render |
| `ProduceClusterExports` | false | report artifact | move to report options |
| `MinClusterSize` | 1 | 5 | keep |

**Q7 — a 6-frame signature is a lossy hash.** Cluster identity is the top 6 frames (4 at Fast). Two
threads whose stacks agree for 6 frames and then diverge into completely different work are reported
as one cluster. This does not truncate a list — it **merges distinct clusters**, so the cluster
*count* and the per-cluster thread counts are both wrong, in a direction that understates diversity.
For a deadlock or thread-pool-starvation dump, where the question is "how many distinct things are
these threads doing," that is the headline number.

#### Implementation notes (as shipped)

- **`MaxFramesPerSignature` deleted — cluster identity is now the thread's whole captured stack.**
  `GetRequiredFrameCount` returns [`ThreadAnalyzer.UnboundedFrameCount`](../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L27)
  (the same 100K sentinel §9.23 introduced) instead of `MaxFramesPerSignature`, so the shared
  `ThreadStackScanDispatcher` pass was already capturing full stacks for every participant once §9.23
  landed — this row's fix was free at the scan layer, only `BuildSignature`'s internal truncation
  break needed to come out. Two threads that share their first 6 frames and diverge below that are no
  longer merged into one cluster — the Q7 defect is gone, not just bounded differently.
- **`MaxClusters` deleted outright** — the clusters `Dictionary` and every derived array
  (`topClusters`/`filteredClusters`/`topClusterSnapshots`) are unbounded; `MaxClustersCapReached`
  removed from `ThreadStackClusterDomainResult` along with the warning block that read it.
- **`MaxThreadIdsPerCluster`/`TopSignaturesToShow`/`TopClustersToShow` moved to the render layer**,
  matching D5 — but note the destination is **not** `CompactTable`. `ThreadStackClusterSectionBuilder`
  renders one collapsible card per cluster via the pre-existing `StackClusters` typed slot (confirmed
  the sole consumer of that slot in `src`), which is a legitimate specialized display in the same
  family as `NamedStackTrace`/`EventLeakGroupCards`, not the ad-hoc `.Take()`-before-narrative-block
  pattern D5's Mechanism 2 targets — so unlike §9.23's `SampledThreads`→`OtherThreads` conversion
  (which moved genuinely tabular per-thread rows off a one-block-per-thread rendering and onto
  `STCompact`), this one keeps its existing typed slot and gets ordinary section-builder-local
  constants (`TopClustersToShow = 12`, `MaxSampleIdsPerClusterToShow = 8`) instead.
- **Found and fixed in passing: the per-cluster `Truncated` flag was dead code.** It was hardcoded
  `false` at the render layer regardless of whether the sample thread-ID list was actually complete —
  because the analyzer previously capped `SampleThreadAddresses` at `MaxThreadIdsPerCluster` before the
  section builder ever saw it, so there was no way to tell truncated from complete. Now that
  `AccumulateCluster` records every thread's address unconditionally (bounded naturally by cluster
  size, never heap-scale), `ThreadStackClusterSectionBuilder` computes a real
  `Truncated = cluster.SampleOsThreadIds.Count > idLimit` at render time.
- **`SampleOsThreadIds`/`SampleManagedThreadIds` keep their "Sample" name despite now being complete
  lists** — unlike §9.23's `SampledThreads`→`OtherThreads` rename, these fields are written verbatim
  into the on-disk JSON/NDJSON cluster exports (an external artifact contract, not an internal-only
  domain result), so renaming them would be a breaking export-schema change for no behavioral benefit.
  Documented via XML doc comment on `ThreadClusterSnapshot` instead.
- **`ProduceClusterExports`'s D6-decided move to `ReportOptions` was not executed in this pass** — D6
  groups it with `StringAnalysisOptions`/`WeakReferenceAnalysisOptions.ProduceRawExports` as one
  cross-cutting change, and neither of those has landed yet (String is still its own AMBER row,
  §9.12). `ReportOptions` is a CLI/report-layer concept with no existing wiring path into
  `AnalysisContext.AnalysisOptions` (confirmed: `ReportOptions` has zero consumers in
  `DumpDetective.Analysis` today) — moving it would mean relocating the actual JSON/NDJSON export
  generation out of the analyzer into a post-analysis Reporting-layer step, a materially bigger change
  than this pass's scope. Left as a `ThreadStackClusterAnalysisOptions` field with its tier variance
  removed (single Balanced-shaped default, `false`), flagged in-code as deferred.
- **`ConfigurationResolver`/test fallout, same shape as every other `Preset`-deletion row:**
  `BuildThreadStackClusterAnalysisFromConfig` rewritten to the section-overrides-on-`new
  ThreadStackClusterAnalysisOptions()` pattern (§9.4/§9.9/§9.23's shape); deleted the one test
  (`ThreadStackClusterAnalyzerOptionsTests.Preset_Fast_Sets_Coarse_Values`) that asserted on `Preset`
  values, keeping the file's unrelated `DomainResult_Can_Carry_Artifacts` test.

---

### 9.25 Hang — **GREEN** ✅ IMPLEMENTED

[HangAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HangAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxTasksToScan` | 50,000 | 3 | delete |
| `TopWaitingThreadsPerGroup` | 5 | 1 — rows | **dead (V4), delete** — never read by `HangAnalyzer.cs` |
| `TopContinuationTypesToShow` | 5 | 1 — rows | move to render |
| `LongWaitThreshold` | 5 | 5 — **varied by tier** | **dead (V4), delete** — never read by `HangAnalyzer.cs`; the tier variance discussed in §3.1/D4 was inert |
| `HighThreadPoolThreshold` | 100 | 5 — **varied by tier** | keep, stop varying |

One semantics-by-tier threshold remains live for §3.1 after V4: `HighThreadPoolThreshold` (100 vs
150 vs 60). **`LongWaitThreshold`'s apparent tier variance (5 vs 8 vs 3 seconds) turned out to be
inert** — `HangAnalyzer.cs` never reads it (§11.3 V4), so no dump's hang diagnosis ever actually
depended on it.

#### Implementation notes (as shipped)

- **`MaxTasksToScan` deleted, and it was a genuine Q7 defect, not just a display cap.** Both scan
  paths (`AnalyzeHeapObjectByAddress`, the shared-heap-index-scan participant path, and
  `RunParallelAsyncScan`'s `ProcessEntry`, the standalone-invocation fallback) always incremented
  `TotalTasks`/`tasksScanned` unconditionally, but only read each Task's `m_stateFlags` field —
  and therefore only counted it into `PendingTasks`/`FaultedTasks`/`CanceledTasks` — while
  `tasksScanned <= MaxTasksToScan`. So `TotalTasks` was always exact but the three state-bucket
  counts silently undercounted past the cap, the same "cap corrupts a total, not just report width"
  shape as Boxing's `TotalBoxedObjects` (§9.1). Each Task object needs exactly one extra field read
  regardless of heap size — no `Top-N` selection, no per-object list — so removing the cap is a flat
  per-Task-object cost, not a new asymptotic class.
- **The `TaskScanLimited` flag and its `queuedWorkItems > 1000` early-exit died with the cap** —
  deleted from `ThreadPoolAnalysis`, the `MergePartial` OR-merge, `HangDomainResult`, and the
  confidence-reduction branch in `ConfidenceSectionBuilder` (`BuildHangText`/the "Hang / task scan"
  limitation row now only fires on `!RuntimeThreadPoolDataAvailable`). Distinct from
  `AsyncTaskDomainResult.TaskScanLimited` (§9.29, a different analyzer, not touched here).
- **The real `TopWaitingThreadsPerGroup`-shaped cap was a hardcoded `.Take(10)`, not the option
  itself.** V4 already confirmed `TopWaitingThreadsPerGroup` is dead code — this pass found *why* it
  looked plausible: `Analyze()`'s `WaitingThreadSnapshot` list construction had a literal `.Take(10)`
  standing in for it. Deleted the `.Take(10)` (and `TopContinuationTypesToShow`'s `.Take()` in the
  same method) so `HangDomainResult.TopWaitingThreads`/`TopContinuationTypes` carry the complete
  ranked lists; `HangSectionBuilder` gained matching render-layer constants
  (`TopWaitingThreadsToShow = 10`, `TopContinuationTypesToShow = 5`) preserving today's display
  defaults without any exactness cost upstream.
- **`HighThreadPoolThreshold` is the sole surviving option**, matching D4's "shakiest" flag (ignores
  machine core count/workload) — kept at its Balanced value of 100 per D4's decision to defer
  recalibration to field data rather than re-derive it now.
- **Test fallout:** `HangAnalyzerHeapIndexScanTests.cs`'s `MergePartial_OrsTaskScanLimited` deleted
  (tested the now-gone OR-merge); `MergePartial_SumsThreadPoolHeapScanCounters` trimmed to drop its
  `taskScanLimited`/`TaskScanLimited` parameter and assertion, keeping the rest of the merge-counter
  coverage intact. `ConfigurationResolver`'s `BuildHangAnalysisFromConfig` rewritten to the
  section-overrides-on-`new HangAnalysisOptions()` pattern used by every other `Preset`-deletion row.

---

### 9.26 Crash — **GREEN** ✅ IMPLEMENTED (7 of 8 deleted, not "options class deleted outright")

[CrashAnalysisOptions.cs](../../src/DumpDetective.Core/Options/CrashAnalysisOptions.cs)

All eight knobs are Category 1 payload/presentation limits: `MaxExceptionsPerType`,
`TopExceptionTypesToInclude`, `MaxDetailedExceptionsPerType`, `MaxOriginalStackFramesToPrint`,
`MaxCurrentThreadFramesToPrint`, `TopCrashThreadCandidates`, `TopDetailedExceptionInstances`,
`IncludeAllTypesInPayload`. Options class deleted outright.

**`IncludeAllTypesInPayload` already states §10's design, and already defaults to it:**

> When true, analyzer will include full type lists and details in the domain result payload. **The
> report renderer may choose to only display the top-N types.** Default true to prefer sending
> maximal data to the report and let the client filter.

Complete data in the domain result, truncation at the render layer, reversible. That is exactly the
Category 1 move this plan proposes for every other analyzer — already written down, already
implemented, already the default here.

**Use Crash as the reference implementation when building the §10 render-layer mechanism (D5).**
It also settles part of D5 empirically: the split works, and the renderer is the right owner.

#### Correction: `MaxExceptionsPerType` is not Category 1 like its seven neighbors

This row's original one-line verdict ("all eight knobs are Category 1... options class deleted
outright") undersold `MaxExceptionsPerType` specifically. Tracing what it actually gates:
[`ExtractExceptionInfo`](../../src/DumpDetective.Analysis/Analyzers/CrashAnalyzer.cs) walks the
inner-exception chain (up to depth 16) and parses the exception's original stack trace into a
`List<string>` — genuinely expensive per-object work, not a cheap field read — and the resulting
`ExceptionInstance` holds that full stack-trace text. `MaxExceptionsPerType` is what decides, per
exception type, how many instances get this treatment; active-thread exceptions are always processed
regardless of the cap, and every reported total (`TotalExceptions`, per-type/per-generation counts)
is already computed unconditionally elsewhere in the scan, untouched by this cap either way. This is
the same shape as the evidence-decoration caps D3 (§11.2) later decided to *keep* for
TimerLeak/StaticRootLeak/EventLeak/CollectionAnalyzer/Dominator — a cap gating expensive per-item
detail-extraction work whose absence costs nothing in reported-total exactness. **Kept as a fixed
internal constant** (10, unchanged from Balanced), not tier-varied, with the reasoning captured in an
XML doc comment on the surviving `CrashAnalysisOptions` class so a future reader doesn't mistake it
for an oversight.

#### Implementation notes (as shipped)

- **The other seven knobs are gone, confirming the row's core claim.** `IncludeAllTypesInPayload`'s
  `false` branch (`BuildDomainResult`'s `Take(TopExceptionTypesToInclude)`) deleted — the analyzer now
  unconditionally emits complete `ExceptionTypeCounts`/`ActiveExceptionTypeCounts` dictionaries, the
  behavior the option already defaulted to. `TopCrashThreadCandidates` deleted —
  `BuildCrashThreadSnapshotsImpl` emits every distinct crash-thread candidate (bounded by thread count,
  never heap-scale). `TopDetailedExceptionInstances` deleted — `BuildExceptionInstanceSnapshots`'s flat
  list is already bounded by `MaxExceptionsPerType` upstream, so no further slice was needed.
  `MaxDetailedExceptionsPerType` was **dead code** (confirmed zero reads in `CrashAnalyzer.cs`, only
  referenced by the config-plumbing round-trip) — deleted, another §11.3-V4-style dead knob the
  original per-knob table didn't catch.
- **`MaxOriginalStackFramesToPrint`/`MaxCurrentThreadFramesToPrint` were truncating data that was
  already fully captured, not bounding new work.** `ExceptionInstance.OriginalStackTrace`
  (`ExtractExceptionStackTrace`) and `ActiveExceptionContext.CurrentThreadStack` were both already
  materializing every frame at extraction time — the two options only sliced the list afterward, when
  building the domain-result snapshot. One exception: `BuildActiveExceptionLookup`'s
  `thread.EnumerateStackTrace().Take(MaxCurrentThreadFramesToPrint)` *did* bound the live stack walk
  itself — removed too, matching §9.23 Thread's precedent (walk the whole stack; only threads with an
  active exception reach this path, never heap-scale). `TakeNormalized` (truncate-and-normalize)
  replaced with `NormalizeAll` (normalize only) at all four call sites.
- **Schema unaffected**, matching D2: `CrashDomainResult`'s field names/shapes are unchanged — only
  what they now contain (complete instead of capped) changed.
- **`ExceptionAnalysisSectionBuilder` gained matching render-layer constants**
  (`TopExceptionTypesToShow = 15`, `TopCrashThreadCandidatesToShow = 5`,
  `TopExceptionInstancesToShow = 25`) at every table that previously relied on the analyzer's cap for
  display width; the depth-histogram aggregate was left reading the *complete* `TopExceptionInstances`
  list (it already did), so it gets more accurate once the upstream cap is gone, for free.
- **Options-surface cleanup matched every other row's shape**, but with an extra step: since
  `CrashAnalysisOptionsModel` (the config-binding wrapper carrying a legacy per-analyzer `Profile` key,
  §8 item 5) is now unnecessary for a single-field options class, it was deleted outright —
  `CliConfigurationFileModel.Crash` binds directly to `CrashAnalysisOptions`, matching the
  no-Model pattern already used by GCGeneration/SegmentReservation/Thread/ThreadStackCluster/Hang. This
  executes §8 item 5's Crash half early (Collection's Model still carries its own `Profile` key,
  untouched). `ConfigurationResolverTests`' Crash-preset-specific test deleted; two profile-mapping
  tests (`Resolve_ShouldMapDeepToFull_ForGlobalProfile`,
  `Resolve_ShouldFallbackToBalancedProfile_WhenNoProfileProvided`) kept but trimmed of their
  now-invalid Crash-field assertions, since their real subject is the global profile-string mapping via
  `Collection.PathAnalysisTopN`, not Crash specifically.

---

### 9.27 Memory — **GREEN** ✅ IMPLEMENTED (cross-reference's "collapse to one table" executed, with a correction)

> **Cross-reference before executing the `TopTypesCount` move (Category 1) or touching the four
> weight knobs (Category 5):** [analyzer-pipeline-stages-and-leadfinding-dedup.md](./analyzer-pipeline-stages-and-leadfinding-dedup.md#stage-1-purity-audit--analyzer-domain-results-are-not-pure-data-either)
> argues the Category 5 "keep" verdict below should be revisited once the cap is gone — `TopTypes`
> is built from a weighted multi-criteria quota merge that exists to squeeze the "most interesting"
> types into a small display budget; once the analyzer emits the complete type table anyway (Category
> 1's own design), that budget-driven merge logic may no longer need to live in the analyzer at all.

[MemoryAnalysisOptions.cs](../../src/DumpDetective.Core/Options/MemoryAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TopTypesCount` | 20 | 1 — rows | move to render |
| `TopTypesBySizeWeight` | 40 | 5 — **ranking function** | keep, fix one value |
| `TopTypesByCountWeight` | 35 | 5 | keep, fix one value |
| `TopTypesByLohWeight` | 15 | 5 | keep, fix one value |
| `TopTypesByAverageSizeWeight` | 10 | 5 | keep, fix one value |
| `LohThresholdBytes` | 85,000 | 5 | keep |

**Q7 — the tier changes *which* types are selected, not how many.** The four weights are a scoring
function, and the preset re-tunes it: Fast 45/40/10/5, Balanced 40/35/15/10, Full 35/30/20/15. The
class doc says so plainly — *"Fast favors bytes/count, Full gives more room to LOH/avg-size signals."*

So Full is not a superset of Fast. A type surfaced at Fast can be absent at Full and vice versa. This
is a subtler §3.1 case than a threshold: the tier silently substitutes a different ranking function,
which is the least defensible thing for a knob labelled "how thorough" to do.

#### Correction: `TopTypesCount` also gates a real per-type BFS, not just report width

Before executing the cross-reference's "collapse to one raw table" suggestion, tracing
`MemoryAnalyzer.BuildDomainResult` found `TopTypesCount`/`SelectedTypes` feeds a
`RetainedSizeCandidateSelector.SelectAndCompute` call (`maxCandidatesToWalk: walkCandidates.Count` —
already unbounded *relative to* `SelectedTypes`, so `SelectedTypes.Count` **is** the real wall-clock
knob) — a bounded BFS (`BoundedGraphWalk.ComputeExclusiveRetained`, breadth 10,000/depth 20) per
candidate, not a cheap lookup. Naively deleting the cap and reporting "all distinct types" would run
that BFS for every type on a 25GB heap (tens of thousands of distinct types), the same class of defect
already corrected for Crash's `MaxExceptionsPerType` (§9.26) and consistent with D3's kept
evidence-decoration caps. **Resolution: split the two concerns the cross-reference's "one raw table"
idea conflated.** The type *list* is now genuinely complete and exact — every distinct type, no
selection judgment at all. The expensive retained-size *enrichment* stays scoped, to a fixed
`MemoryAnalyzer.TypesToWalkForRetainedSize = 20` internal constant (the largest types by shallow
size) — not exposed via `MemoryAnalysisOptions`, since it has no semantic meaning to a user, purely a
wall-clock-cost knob. This fully executes the cross-reference's recommendation (no weighted
quota-merge selection survives) while keeping the one piece of real per-item cost bounded.

#### Implementation notes (as shipped)

- **The weighted quota-merge selection mechanism is deleted outright, not just de-tiered.** Given the
  type list is now complete (every distinct type reported, sorted by total bytes descending), there is
  no more "which N types get shown" judgment call for the four weights to bias — so
  `TopTypesBySizeWeight`/`TopTypesByCountWeight`/`TopTypesByLohWeight`/`TopTypesByAverageSizeWeight`
  and the `byCompositePressure`-sort/`ComputeQuota`/`AddFromRankedList` machinery in
  `MemoryAnalysisProjection.Build` are gone entirely — a stronger resolution of Q7 than the original
  table's "keep, fix one value" verdict (which would have kept a now-purposeless scoring function
  around). `MemoryPressureScore`'s own composite formula (lohPressure/concentrationPressure/
  smallObjectPressure/densityPressure) is a separate, already-untouched calculation and stays as-is.
- **`MemoryAnalysisProjectionResult.SelectedTypes` renamed to `AllTypesBySize`** to match its new
  semantics (was capped-and-merged, now complete-and-sorted-by-size) — internal-only record, no
  schema/serialization impact per D2's established pattern.
- **`LohThresholdBytes` kept, confirmed cosmetic-but-correct**: traced every use and found it's
  `echoed into `MemoryDomainResult` for display only — the real LOH classification is a hardcoded
  `85_000` constant in `TypeIndexBuilder.cs`, and this option was never tier-varied in the first place
  (absent from every `Preset` branch), so there was no exactness defect here, just a redundant
  always-correct label. `Preset`/`Default` deleted; `MemoryAnalysisOptions` now carries only this one
  field.
- **No render-layer change needed** — `MemoryAnalysisSectionBuilder`'s "Top types" `STCompact` table
  already built its row limit from `d.TopTypes.Count` with no separate cap
  (`ExecutiveSummarySectionBuilder`'s own `TopMemoryItems` slice, and `MemoryAnalyzerTrendComparer`'s
  `.Take(10)`, were already render/trend-layer concerns operating on the full list) — this row's fix
  was entirely upstream, in what the analyzer computes.
- **Test fallout:** `MemoryAnalysisProjectionTests.cs`'s one test rewritten (no `MemoryAnalysisOptions`
  parameter to `Build` anymore; asserts on `AllTypesBySize` containing all three input types sorted by
  size, not a 2-of-3 quota-merged selection). `ConfigurationResolver`'s `BuildMemoryAnalysisFromConfig`
  rewritten to the standard section-overrides-on-`new MemoryAnalysisOptions()` pattern.

---

### 9.28 HeapTopology — **GREEN**, one line, large effect

[HeapTopologyAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HeapTopologyAnalysisOptions.cs)

One knob, `CountSohObjects`, and its own doc describes it as an exactness switch:

> When `false` (**default**), per-object counting is skipped for all SOH segments. Only LOH and POH
> segments are counted exactly. Set `true` when exact SOH object counts are required.

Fast and Balanced set `false`; only Full sets `true`. **The default configuration does not count the
small object heap** — the bulk of objects on nearly every dump.

**Q5 — measured (M6, §11.4): real cost, but a free exact alternative exists.** This enables
per-object counting across all SOH segments via a live `segment.EnumerateObjects()` ClrMD walk
([:284](../../src/DumpDetective.Analysis/Analyzers/HeapTopologyAnalyzer.cs#L284)) — confirmed **not**
served from the disk-backed index. On a real 3.35GB dump this costs 10.2 extra seconds (606 ms → 10.8
s), affordable against the ~10-minute budget but not free. Better than "set to `true` permanently":
**derive the exact SOH count as `TotalObjectCount − LohCount − PohCount − FrozenCount`**, using Phase
1's already-exact total object count and this analyzer's own already-cheap LOH/POH/Frozen walks — zero
additional heap traversal, genuinely free exactness rather than a 10-second one. Delete the knob and
options class either way; prefer the arithmetic over the live walk when implementing.

---

### 9.29 AsyncTask — **GREEN**

> **Cross-reference before executing the four `Top*ToShow` moves (Category 1) below:**
> [analyzer-pipeline-stages-and-leadfinding-dedup.md](./analyzer-pipeline-stages-and-leadfinding-dedup.md#stage-1-purity-audit--analyzer-domain-results-are-not-pure-data-either)
> flags `AsyncTaskDomainResult` as having 8 separately-capped `Top*` lists that likely slice the same
> underlying task population by different states. While removing these four caps, check whether they
> (and the other 4 uncapped `Top*` lists on the same result) should collapse into one raw per-task-type
> table instead of staying as separately-maintained lists — cheaper to do in the same edit than as a
> follow-up pass.

[AsyncTaskAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AsyncTaskAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxTasksToScan` | 50,000 | 3 | delete |
| `MaxTcsToScan` | 20,000 | 3 | delete |
| `MaxVtsToScan` | 20,000 | 3 | delete |
| `MaxContinuationDepth` | 20 | 4 | delete |
| `TopTypesToShow` / `TopOrphanedToShow` / `TopUnresolvedTcsToShow` / `TopPendingVtsToShow` | 10-20 | 1 — rows | move to render |

Options class deleted outright.

**Q7 — three independent scan caps on an 87 M-object heap.** A service under async pressure holds
far more than 50,000 `Task` objects; orphaned-task and unresolved-`TaskCompletionSource` counts are
therefore drawn from a prefix of the population, in index order. `MaxContinuationDepth = 20` further
truncates continuation-chain walks, which is how orphaned tasks are identified in the first place.

**Q2 — confirmed index-backed (M7, §11.4).** The Tasks section (`TaskIndex.bin`) is built
unconditionally during Phase 1, not gated on which analyzers are active — `LoadTaskEntries` reads it
back rather than falling to a live `heap.EnumerateObjects()` scan. Measured on a real dump: uncapped
vs. capped delta was 311 ms against a shared ~4.4s baseline, negligible. Safe to delete.

---

### 9.30 AllocationPattern — **AMBER**, resolved by D7

[AllocationPatternAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AllocationPatternAnalysisOptions.cs)

**AMBER because the tier selects the algorithm, not the effort.** Three enums are varied by preset:

| | Fast | Balanced | Full |
|---|---|---|---|
| `Mode` (`SelectionMode`) | `TopByCount` | `CompositeScore` | `CompositeScore` |
| `Strategy` (`ScanStrategy`) | `TopN` | `TopNByComparator` | **`FullScan`** |
| `Priority` (`SelectionPriority`) | `LongLivedFirst` | `ClassificationFirst` | `ClassificationFirst` |
| `Gen0Weight` / `Gen2Weight` | 1.0 / 1.0 | 1.0 / 1.0 | **0.5 / 1.5** |
| `MaxScanItemsAbsolute` | — | — | 20,000 |

`ScanStrategy.FullScan` exists and is reachable **only at Full**. Exactness means adopting it
permanently, which collapses `ScanStrategy` entirely — but `SelectionMode` and `SelectionPriority`
are genuine algorithm choices that outlive the tier system and need an explicit decision (see D7).

**Resolved by D7 — with one addendum this audit missed:** `FullScan`'s `scanLimit` is still capped by
`MaxScanItemsAbsolute` (10,000 Balanced/default, 20,000 Full) — since `TypeAggregates` runs ~50-100k
entries at 25GB scale (§9.1's Q5), `FullScan` alone is not actually exact; `MaxScanItemsAbsolute` must
be deleted too. `SelectionPriority` turned out not to be a preference at all: `LongLivedFirst`'s
single-pass sequential bucket-fill is scan-order-dependent and can silently drop a bucket's true
top-N member, while `ClassificationFirst` classifies every candidate before ranking each bucket
independently — the only one of the two that's actually correct. Keep `ClassificationFirst`, delete
`LongLivedFirst` and the never-used `Mixed`. `SelectionMode` turned out to be a Category-1
display-ranking choice, not an algorithm choice, once `MaxScanItemsAbsolute` is gone (classification
itself never depended on it) — keep `CompositeScore`, delete the other three.

The four classification thresholds (`LongLivedSelectionThreshold`, `LongLivedClassificationThreshold`,
`TransientClassificationThreshold`, `ShortLivedSelectionThreshold`) are Category 5 and — unusually —
are set to **identical values in all three presets**. Pure duplication; promote to initializers and
delete from the presets. `TopTypeLimit` and `ScanMultiplier` compound (`TopTypeLimit x ScanMultiplier`
at [AllocationPatternAnalyzer.cs:173](../../src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L173)) —
sixth instance of *configured ≠ applied* (§11.6).

---

### 9.31 WeakReference — **GREEN**

[WeakReferenceAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/WeakReferenceAnalyzer.cs) ·
[WeakReferenceAnalysisOptions.cs](../../src/DumpDetective.Core/Options/WeakReferenceAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `HandleScanCap` | 50,000 | 3 | delete |
| `WeakRefProbeSampleLimit` | 50 | 2 — sampling | **delete** |
| `TopTypeLimit` | 15 | 1 — rows | move to render |
| `AbsoluteDeadCountThreshold` | 10,000 | 5 | **dead (V4), delete** — zero references in `WeakReferenceAnalyzer.cs` |
| `ProduceRawExports` | false | report artifact | move (D6) |

**Q7 — `HandleScanCap` truncates the handle table itself**, not a derived list:
[:120, :192](../../src/DumpDetective.Analysis/Analyzers/WeakReferenceAnalyzer.cs) break the handle
enumeration once `totalWeakHandles > options.HandleScanCap` and set `scanCapped`. On a heap with more
than 50,000 weak handles — plausible for a large cache-heavy service — `WeakHandleKinds`,
`TargetTypeHits` and every derived stat are drawn from a prefix, not the population.

**Q7 — `WeakRefProbeSampleLimit` is a genuine Category 2 sample**, distinct from the scan cap. Its own
doc calls it a probe count *"used when approximating stale wrappers,"* and
[:273](../../src/DumpDetective.Analysis/Analyzers/WeakReferenceAnalyzer.cs#L273) treats `<= 0` as "no
cap" — so full exactness is already one config value away, just not the default. Same shape as
Array's `SampleStride` (§9.11): an estimate presented as a count.

**Q3 — no risk.** `TargetTypeHits`/`WeakHandleKinds` are O(distinct types) dictionaries.

---

### 9.32-9.34 preamble: the typed-resource quartet

DbConnection, WcfChannel, HttpObject and (already audited) TimerLeak share infrastructure that never
surfaced from the options-folder walk, because **none of the four have an `AnalysisOptions` class at
all** — every bound is a `private const int` inside the analyzer:

- **[`TypedResourceCandidateScanner.DiscoverCandidates`](../../src/DumpDetective.Analysis/Analyzers/TypedResourceSampler.cs)**
  — candidate-type discovery via `TypeAggregates`, falling back to a full `heap.EnumerateObjects()`
  sweep with no index. O(distinct types), no cap needed, already effectively exact.
- **[`InstanceStateSampler<T>`](../../src/DumpDetective.Analysis/Analyzers/TypedResourceSampler.cs)**
  — the shared per-instance sampler. Two hard-coded numbers per analyzer:
  `MaxStateSamplesPerType` (a **per-type** field-read cap, not global) and `TopSampleCap` (a Category 1
  detail-table limit).

| Analyzer | `MaxStateSamplesPerType` | `TopSampleCap` |
|---|---:|---:|
| DbConnection | 500 | 50 |
| WcfChannel | 500 | 50 |
| HttpObject | 500 | 20 |

**These are not part of `AnalysisOptions` and are not preset-varied** — the three-tier system never
touched this quartet at all. So there is nothing here for the profile-deletion side of this plan to
do; the exactness question stands on its own, and needs a **new mechanism**, not a config change,
since the bound is a compiled constant.

**Q7, shared across all three.** `MaxStateSamplesPerType` caps state-field reads **per type**, so a
service with 600 open `SqlConnection`s reports the state (open/closed/broken) of only 500 of them —
the other 100 fall into neither bucket, silently. This directly undercounts exactly the pathology
these analyzers exist to catch: connection-pool exhaustion, leaked HTTP handlers, faulted WCF
channels. Candidate discovery itself is exact (index-backed); only the state breakdown is capped.

---

### 9.32 DbConnection — **GREEN**

[DbConnectionAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/DbConnectionAnalyzer.cs)

`MaxStateSamples = 500`, `TopOpenCap = 50`, both `private const`. No options class to delete — this
is a straight code change: promote the constants to instance fields (or keep as constants at raised
values if genuinely unbounded reads are unaffordable — see M9) and delete the per-type cap.

**Q3 — check before deleting.** Each state read does one `heap.GetObject` +
`GetFieldByName("_connectionString")` + regex anonymisation
([:75-101](../../src/DumpDetective.Analysis/Analyzers/DbConnectionAnalyzer.cs#L75-L101)). Per-object,
not per-heap-object — bounded by *connection instances*, which are always a small fraction of a
dump's population. Low risk, but confirm the count before assuming.

---

### 9.33 WcfChannel — **GREEN**

[WcfChannelAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/WcfChannelAnalyzer.cs)

Same shape as DbConnection: `MaxStateSamples = 500`, `TopFaultedCap = 50`. Same action — promote
past the per-type cap, keep the Category 1 detail-table limit at the render layer.

**Q7 — faulted-channel detection is exactly what's capped.** `TopFaultedCap` bounds the detail table
callers actually read for root-cause; `MaxStateSamples` bounds the state tally underneath it. A
service with a channel-faulting storm past 500 instances of one channel type reports an incomplete
`StateOpening/Opened/Closing/Closed/Faulted` breakdown for that type.

---

### 9.34 HttpObject — **GREEN**

[HttpObjectAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/HttpObjectAnalyzer.cs)

`MaxStateSamples = 500`, `TopHttpClientSampleCap = 20`. Same action as §9.32/9.33.

**Q7 — this is the analyzer that exists specifically to catch `HttpClient` misuse** (the
"should be a singleton via `IHttpClientFactory`" pattern), and per-type counts
(`HttpClientCount`, `HttpMessageHandlerCount`, etc.) are exact — they come from `TypeAggregates`
pre-seeding, not the capped sampler. Only the *detail table* (`TopHttpClients`, with base
address/timeout) is capped. Lower-stakes than DbConnection/WcfChannel: the headline count is already
right, only the drill-down sample is bounded.

---

### 9.35 LeakCandidate — **GREEN**, no options class, and not gated on §6.2

[LeakCandidateAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LeakCandidateAnalyzer.cs)
(`internal sealed`, `IDeferredAnalyzer`)

No options class, no hard-coded scan cap on its main pass — it iterates every entry in
`TypeAggregates` ([:57-139](../../src/DumpDetective.Analysis/Analyzers/LeakCandidateAnalyzer.cs)),
which is O(distinct types) and already exact. The only limit is a post-hoc slice:

```csharp
int topCount = Math.Min(30, /* candidates.Count */);
```

**Category 1, move to render.** Nothing else to change — this analyzer is close to a template for
"correctly built against the index from the start."

**Worth noting for §10:** it runs *after* other analyzers complete and reads their results directly
— `context.CompletedRunResults?.GetResult<GCHandleDomainResult>()` — rather than re-deriving pinned
/dependent-handle target types itself. That is the composition pattern the retained-size accessor
(B2) should follow: build once, consume by reference, not by re-scanning.

---

## 10. Cross-cutting workstreams

Two pieces of work are shared by many analyzers and should be built once, before the groups that
depend on them.

- **Category 1 render-layer slicing.** Domain results carry complete ranked collections; section
  builders apply display limits. Needed by nearly every analyzer, so build the mechanism during
  group 1 (aggregators) where it is lowest-risk, then reuse.
- **Dominator-tree-backed retained sizes (B2).** A shared accessor for "exact retained set / retained
  bytes for object X" served from `DominatorTreeComputer` output. Retires `BoundedGraphWalk`'s
  capped estimators. Required by group 3 and probably group 4. **Design below.**

---

## 10a. B2 design: the dominator-tree retention provider — done

**Superseded and shipped by [dominator-tree-phase1-integration.md](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md)**
(§6 "retained-bytes consumers", §7 "root-attribution").
The in-memory cache-provider shape below has two real problems (implicit analyzer-ordering coupling,
and ~1.5-3 GB held resident for the rest of the run) — the actual design is a disk-backed index built
during Phase 1's index-build job, extending D7 rather than inventing a new in-memory structure. The
rest of this section is kept for the problem framing (ordering, `IHeapAnalysisCache` precedent) but
its concrete proposal is not what B2 should implement.

### The problem is not "compute the tree," it's "who owns it after it's computed"

The exact tree already exists and is cheap relative to everything else in the pipeline (§4). What
doesn't exist is any way for an analyzer other than `DominatorAnalyzer` to reach it:
[DominatorAnalyzer.cs:128-247](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs#L128-L247)
builds `ReachableGraph` and `DominatorTreeComputeResult` as **method-local variables**, extracts one
narrow `IReadOnlyDictionary<string, ulong>` (per-*type* retained bytes, for report display only, and
only for types already in the heuristic's top-K), and lets everything else — the fold, the idom
array, the retained-bytes array, the child CSR — become eligible for GC the moment `AnalyzeAsync`
returns. There is no per-*object* query surface at all today, exact or otherwise.

### Design: follow the reverse-index provider pattern already in the codebase

`IHeapAnalysisCache` already solves an identical problem for the reverse edge index —
[`TryGetReverseIndexProvider()`](../../src/DumpDetective.Core/Abstractions/IHeapAnalysisCache.cs#L50):
a lazily-built, cached, nullable-on-unavailable provider, built once regardless of which analyzer
asks first. Copy that shape exactly rather than inventing a new one:

```csharp
// New member on IHeapAnalysisCache, same nullability contract as TryGetReverseIndexProvider
IDominatorRetentionProvider? TryGetDominatorRetentionProvider(ClrHeap heap, RetentionOptions options);

internal interface IDominatorRetentionProvider
{
    bool TryGetRetainedBytes(ulong address, out ulong retainedBytes);
    // Streams the address of every node in address's dominator subtree — a parent-pointer / child-CSR
    // walk, not a fresh BFS. Replaces BoundedGraphWalk.CollectRetainedObjects's Dictionary materialization.
    IEnumerable<ulong> EnumerateRetainedSet(ulong address, CancellationToken cancellationToken);
    bool WasBudgetExceeded { get; } // true => caller falls back to its pre-exactness estimator (see below)
}
```

**Why this resolves the ordering problem instead of requiring a reorder.** Module order in
[DefaultAnalyzerFeatureModuleCatalog.cs](../../src/DumpDetective.Reporting/Capabilities/DefaultAnalyzerFeatureModuleCatalog.cs)
runs `gc-root` (140) **before** `dominator` (220) — confirmed via `DefaultAnalyzerFactory.CreateAnalyzers()`
sorting by `m.Order`, consumed sequentially by `AnalysisPipeline`
([:16, :113](../../src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs)). If the tree were only
ever built inside `DominatorAnalyzer`, GCRoot would run first and find nothing. A cache-triggered
provider sidesteps this entirely: **whichever analyzer asks first triggers the build**, `DominatorAnalyzer`
itself becomes a *consumer* of the same accessor rather than the sole owner, and no entry in the
catalog needs reordering. This also means **no analyzer needs to become `IDeferredAnalyzer`** — the
two-phase deferred mechanism (already used by `LeakCandidateAnalyzer` to read other analyzers'
*domain results*) is the wrong tool here; this is a shared *structure*, not a dependency on finished
output.

### The real cost: this changes the memory-lifetime contract, not just the API

Today the tree's structures live for one method call and are collected well before most later
analyzers run. Shared across the pipeline, they must stay resident **concurrently** with every
analyzer that queries them — potentially the entire run, since group 3/4 consumers span module
orders 140 (GCRoot) through 340 (FinalizableObject). `ExactDominatorTreeMemoryBudgetBytes` (20 GB,
§9.18) currently budgets *construction* peak only; once the result is cache-resident, the effective
peak is `tree + whatever else is running concurrently`, which the existing budget was never sized
against. **This needs its own measurement (add to §11.4) before shipping, not an assumption that
20 GB still holds.**

### A second, currently-absent structure is required: a persistent address→id map

`DominatorTreeComputeResult` is indexed by **reduced-graph id**, not address. Within
`DominatorTreeComputer.Compute` the only address→id resolution is a `DenseIdMap` built and discarded
inside `ReachableGraphWalker.Walk`
([:48](../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs#L48)) — nothing
downstream needs it because every consumer today operates by id (LeafFolder, LengauerTarjan, the
rollup). An external accessor taking `ulong address` needs this map to **survive**, which is memory
the current exact path never pays for. Two options, to decide as part of implementation:

1. Retain the `DenseIdMap` the walk already builds (~13 bytes/slot per the measured figure in
   [dominator-tree-memory-profile.md §3.1](../analysis/phase1-redesigns/dominator-tree-memory-profile.md#31-allocation-accounting-before-fixes-318-gb-measured)) instead of discarding it — cheapest,
   but ties the provider's lifetime to internals that were designed to be transient.
2. Build a fresh `Dictionary<ulong, int>` on first provider use — simpler, but heavier per-slot than
   `DenseIdMap` and a second full pass over `ReachableGraph.Addresses`.

### Fallback semantics must be explicit, not implicit

`WasBudgetExceeded` (or `TryGet...` returning `null`/`false`) must be defined **before** any consumer
is migrated: when the exact tree isn't available (budget exceeded, `EnableExactDominatorTree = false`,
or the fold/LT computation throws — `DominatorAnalyzer.cs:236-246` already catches and logs this
case), does the calling analyzer (a) fall back to its pre-migration `BoundedGraphWalk` estimator, or
(b) report the metric as unavailable rather than approximate? §9 audited every consumer assuming (a)
implicitly; confirm that's actually wanted per-analyzer rather than assumed uniformly — StaticRootLeak
and FinalizableObject may prefer different answers here given how aggressively their current caps
already truncate (§9.14, §9.15).

### Consumers ready to migrate once this lands

| Consumer | Current implementation | Retired by B2 |
|---|---|---|
| GCRoot | `BoundedGraphWalk.CollectForwardTypeNames` | §9.16 |
| StaticRootLeak | `BoundedGraphWalk.CollectRetainedObjects` | §9.14 |
| `RetainedSizeCandidateSelector` | `BoundedGraphWalk.ComputeExclusiveRetained` | §9.14-16 preamble |
| FinalizableObject | `FinalizableObjectAnalyzer.BfsEstimateRetained` (private 4th copy) | §9.15 |

Each becomes a call through `IDominatorRetentionProvider` plus the existing fallback path, not a
rewrite of the analyzer's surrounding logic.

### Open items before implementation starts (fold into §11)

- Confirm §11.4/M-series real-dump measurement includes concurrent resident cost, not just
  construction peak (per the memory-lifetime point above).
- Pick the address→id strategy (retain `DenseIdMap` vs. fresh dictionary) — affects the memory answer
  above, so resolve before measuring rather than after.
- Decide fallback semantics (explicit "unavailable" vs. silent approximation) per consumer, not
  globally.
- `EnumerateRetainedSet` must itself be bounded by *something* observable (progress reporting /
  cancellation) for objects retaining millions of nodes — not a new cap, but a streaming contract so a
  caller iterating a huge subtree doesn't look hung. `CancellationToken` in the signature above covers
  cancellation; consider whether progress reporting is also needed for very large subtrees.

---

## 11. Pre-implementation checklist

Everything the audit surfaced that must be resolved, decided, verified or measured **before** the
first exactness commit. Nothing here is optional; items marked **BLOCKER** stop work entirely.

### 11.1 Blockers

| # | Item | Source |
|---|---|---|
| B1 | **DONE.** `performance-checklist.md` rewritten to separate bounded-memory (non-negotiable) from bounded-work (case-by-case; illegitimate only when it caps a reported total). | §6.1 |
| B2 | **DONE.** Dominator-tree retained-size accessor built — shipped as a disk-backed Phase 1 index (`IDominatorTreeProvider.TryGetRetainedBytes`), not the in-memory cache-provider originally designed in §10a. | §10a, [phase1-integration.md](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md) |
| B3 | **DONE.** `MaxParentsPerChild` deleted outright (not just raised) — reverse index is uncapped. Real worst-case fan-in measured (346K at 3.3GB, 10.76M at 25.6GB) is far under the sort phase's ceiling. `TotalTruncatedChildren` is moot; every Q6-gated analyzer, including ReferenceChain, is no longer capped at the graph layer. | §6.2, [phase1-integration.md §3](../analysis/phase1-redesigns/dominator-tree-phase1-integration.md#3-stage-a--reachability-walk-shipped) |
| B4 | **DONE.** `RootSetCache.MaxFramesPerThread = 256` only bounds `BuildStackFrameOwnerMap`/`TryResolveStackFrameOwner`'s owner-attribution label, not root discovery — `RootIndexWriter.Write` and `RootSetCache.BuildFromLiveHeap` both use uncapped `heap.EnumerateRoots()`. Root-path consumers and dominator root seeding were never affected; no real-dump measurement needed. | §6.3 |

### 11.2 Decisions required (no correct default)

| # | Decision | Source |
|---|---|---|
| D1 | **DECIDED: delete `ReferenceChainSearchMode` entirely, collapse to one exact bidirectional strategy.** `Balanced`/`Deep` only ever differed by budget (`MaxCandidateNodes`/`MaxCandidateDepth`/`MaxRootExpansionDepth`) around the same algorithm — building a *scoped* candidate set from a *partial* reverse index, a workaround for §6.2's old 10K-fanout cap. With the reverse-edge index now full, disk-backed, and uncapped, there's no more "partial reverse index" to scope around, so exact bidirectional search against the real index replaces all three tiers with one path. `Fast` (unidirectional forward BFS only, explicitly "may miss long paths") is the one truly different algorithm here, but keeping an approximate mode around contradicts the §1 exactness goal unless explicitly opt-in and labeled non-exact — no evidence yet that exact bidirectional search is too slow to be the only mode. **Follow-up before implementation:** measure exact bidirectional search wall-clock on the 25.6GB real dump; if meaningfully slower than today's `Fast`, revisit keeping a documented, explicitly-non-exact fast path rather than assuming it away. D3's `LargeFanoutThreshold` is a separate, still-open decision — not resolved by this collapse. | §9.20 |
| D2 | **RESOLVED: no schema version bump needed.** Domain results never reach the JSON serialization surface — [ReportJsonContext.cs](../../src/DumpDetective.Reporting/Serialization/ReportJsonContext.cs)'s `[JsonSerializable]` roster is exclusively report/section-layer types (`ReportDomainSection`, `SectionKeyMetric`, `NumericMetricValue`, `CompactTable`, …); `BoxingDomainResult`/`ModuleDomainResult`/`ObjectShapeAnalyzerDomainResult` (and their `TypeScanCapped`/`TypeScanCapUsed`/`ExcludedModuleCount`/`InstanceCountCap` fields) are internal-only, confirmed via grep for `JsonSerializer.Serialize` against any `*DomainResult` type (zero hits) — no raw-export path touches them either (`ProduceRawExports` writes unrelated sample records). Every current surfacing of these values in the report contract is already either a conditionally-emitted `Dictionary<string, MetricValue>` entry (e.g. `ModuleSectionBuilder`'s `excluded_modules`, gated on `> 0`) or free-text narrative baked into a `SectionBlock` (e.g. `ObjectShapeSectionBuilder`'s "computed over at most N types" sentence) — both are inherently additive/mutable, not a fixed schema shape. Deleting the caps just means the condition permanently evaluates false or the sentence gets reworded; nothing named in [schema-versioning.md](../schema-versioning.md)'s "do not remove or rename persisted fields" sense is being removed. No trend comparer references any of these fields either (verified by grep). | §9.1, §9.2, §9.3, §9.6 |
| D3 | **DECIDED: split by consumer, not a blanket removal.** Traced every call site of `LargeFanoutThreshold`/`RootPathLargeFanoutThreshold` (`RootPathFinder.cs`, `IndexBackedBidirectionalSearch.cs`) — they fall into two groups with opposite answers. **Keep the cap** (no change) for the five evidence-decoration consumers, all of which call it from a method literally named `PopulateEvidence`, *after* the reported total/count/boolean is already computed independently: `StaticRootLeakDetector`, `TimerLeakAnalyzer`, `EventLeakAnalyzer`, `CollectionAnalyzer`, and `DominatorAnalyzer`'s "highly referenced objects" list (§9.18). For these, the search only builds a "why is this alive" representative-path string for display; `searchTruncated` already flows into `Evidence` and drops confidence 0.8→0.6 when it bites ([Evidence.cs:27](../../src/DumpDetective.Analysis/Models/Evidence.cs#L27)) — removing the cap buys zero exactness benefit (the reported numbers don't depend on whether a path is found) while risking real wall-clock blowup: `BidirectionalGraphSearch`'s outer node budget (`MaxCandidateNodes`, e.g. 5,000) is separate from this per-node fanout cap ([BidirectionalGraphSearch.cs:64-67](../../src/DumpDetective.Analysis/Traversal/BidirectionalGraphSearch.cs#L64-L67)) — hitting the real measured worst-case hub (10.76M parents at 25.6GB, §3 of phase1-integration.md) without a per-node cap would dump millions of nodes into a single level-expansion (each needing a `RootPathSearchSupport.ResolveType` lookup), for a feature that runs once per top-N finding across five-plus analyzers, on exactly the objects (singletons, static caches) most likely to sit near a finding. **Remove the cap** only for `ReferenceChainAnalyzer` (§9.20) — there the path *is* the deliverable, so the cap directly determines whether the true shortest chain is found, unlike the five consumers above. Folded into D1's implementation scope: measure exact bidirectional search wall-clock on the 25.6GB real dump before committing to unconditional removal there; if a hub-adjacent worst case proves too costly, document an explicit non-exact fallback for that analyzer specifically rather than silently capping. | §9.20, §9.22 |
| D4 | **DECIDED: keep every Balanced value as the sole constant, non-blocking.** Recalibrating "what counts as long-waiting/oversized/heavy" is a product decision best driven by real user reports, not re-derivable from first principles — not worth gating implementation on. When each constant lands as a standalone value (no more tiers), add a one-line rationale comment next to it rather than leaving it as an unexplained survivor of three arbitrary picks: **reasonably anchored, comment can state the anchor** — `ThirtyTwoBitPressureThresholdBytes` (1.5 GB; 32-bit VA space caps around 2-4 GB, this is an "approaching the wall" line), `DensityAnomalyMinBytes`/`MaxTypes` (50 MB / 5 types; describes a real anomaly shape — huge footprint from very few distinct types — not just a size cutoff). **Shaky, comment should say so and flag for revisit once field data exists:** `RatioHighPressureThreshold` (10.0x reserved:committed, [SegmentReservationAnalyzer.cs:142](../../src/DumpDetective.Analysis/Analyzers/SegmentReservationAnalyzer.cs#L142) — no external standard), `LargeMethodThresholdBytes` (Jit, 64 KB — arbitrary round number), `OversizedThresholdBytes` (Boxing, 64 bytes — arbitrary, "oversized" scales continuously), `HighThreadPoolThreshold` (Hang, 100 — ignores machine core count/workload, likely the shakiest of the remaining ones). `HeavyModuleWarningThresholdBytes` (Module, 200 MB) is loosely anchored (most modules are single-digit MB) but not flagged urgent. **Correction from V4 (§11.3): `LongWaitThreshold` (Hang) is not a threshold to keep at all — it's dead code.** `HangAnalyzer.cs` never reads it; nothing was ever gated on "5s = long-waiting." Delete it outright, no rationale comment needed, nothing to revisit. Memory's per-tier `TopTypesBy*Weight` re-tuning (§9.27) and AllocationPattern's mode/weight switching (§9.30, D7) are a separate, sharper defect (an algorithm silently changing, not just a threshold) — not resolved by this decision. | §3.1 |
| D5 | **DECIDED: one shared mechanism (already mostly built), converge Mechanism 2 onto it, and upgrade it to real pagination.** Two render mechanisms currently coexist. **Mechanism 1 — `CompactTable`** ([SectionBuilderBase.cs:50-51](../../src/DumpDetective.Reporting/SectionBuilders/SectionBuilderBase.cs#L50-L51), `STCompact`/`RowLimit`, rendered via [report.renderers.sections.js:322-369](../../src/DumpDetective.Reporting/Templates/report.renderers.sections.js#L322-L369) + [report.ui.tables.js](../../src/DumpDetective.Reporting/Templates/report.ui.tables.js)) already sends every row to the client, and sort/filter already operate over the complete dataset, not just the visible slice — confirmed in code: `hydrateRows()` builds a `<tr>` for every row up front (or on first expand past 180 rows), `doSort` reorders the full `tbody`, and `applyManagedTableState` filters over every row's text before applying the display limit to the matched set. The only real gap is the browsing model — today it's a binary "first `limit` rows, or reveal everything" toggle, not true pagination. **Action:** replace the binary `limit`/`showAll` state in `applyManagedTableState` with real pagination state (current page + page size) and a page-size selector (20/50/100/all), default 20 — a self-contained front-end change to `report.ui.tables.js`/`report.renderers.sections.js`'s button/DOM wiring; no domain-model or backend change, since full data is already delivered and `CompactTable.RowLimit` just becomes the default page size instead of a hard initial-reveal cutoff. GCHandle's actual defect isn't the render mechanism (already correct) — it's that `GCHandleAnalyzer` still truncates to `TopTypeCount = 15` **before** the domain result is built (§9.5, `ToTopEntries`/`ToTopByteEntries` at :226-232); deleting that upstream cap and letting each of its seven existing `STCompact` calls carry the full list resolves the "one knob governs six lists" problem without inventing a new abstraction — each call already has its own independently-settable limit. **Mechanism 2 — ad-hoc `.Take(N)` before narrative blocks** (`Li`/`M`/`TextBlock`, a structurally different, non-tabular, non-sortable, non-filterable, irreversible-truncation path — confirmed distinct from `CompactTable` via [AnalyzerDetailSection.cs:203,222-237](../../src/DumpDetective.Analysis/Models/AnalyzerDetailSection.cs#L203)) exists in `BoxingSectionBuilder`, `JitSectionBuilder`, `WeakReferenceSectionBuilder`, `LeakAnalysisSectionBuilder`, `TypeSystemSectionBuilder`, `ExceptionAnalysisSectionBuilder`, `GCRootIntelligenceSectionBuilder`, each with its own inconsistent locally-named constant (`TopTypesToShow`, `TopFrameTypesToShow`, `TopCandidateCount`, `TopRows`, or bare `15`/`10`/`3`/`6`). **Action:** migrate these onto `CompactTable`/`STCompact` wherever the content is genuinely per-item tabular data (most of them — type name, count, bytes triples), which also gets them sorting/filtering/pagination for free. Leave alone: true inline prose lists embedded in a sentence (`ModuleSectionBuilder`'s "Conflict groups include: {csv of 6 names}", `LeakAnalysisSectionBuilder`'s "{csv of top 3 types}") — capping a name list inside a sentence is a prose-length choice, not a hidden-data concern, so these can stay small analyzer-local constants rather than being forced into a table. **Do not force something that isn't a table into a table just to get pagination.** When implementing, leave a comment at each retained inline-prose truncation noting that if the full list ever needs to be browsable, the right fix is a small dedicated list-pagination affordance for prose (not `CompactTable`, and not silently raising the inline cap) — flagged as a possibility to look for, not a decision to build one now. **Amendment (post-§9.7): drop the per-call custom `rowLimit` entirely, use `STCompact`'s uniform default (20) everywhere.** Early implementation (§9.1, §9.4, §9.5, §9.7) carried the old per-table hardcoded constants (`TopTypesToShow`, `TopPaddingToShow`, `TopLohTypesToShow`, `TopGenProfilesToShow`, `TopContestedTypesToShow`, values 8/10/15/30 depending on the table) forward as `rowLimit` arguments instead of collapsing them. On review this was unnecessary ceremony: since every one of these tables now receives the complete, unbounded dataset and the client-side table component already provides its own page-size selector (20/50/100/all) and full sort/filter over that complete dataset, the specific initial page size is not a decision worth a bespoke constant per table — it is exactly the "options surface nobody uses" pattern this whole document warns against, just moved from `AnalysisOptions` to section-builder locals instead of eliminated. **Action, applied to §9.1/§9.4/§9.5/§9.7 and to be followed for every subsequent section:** remove the local `private const int Top*ToShow` constants and the explicit `rowLimit` argument from every `STCompact` call; let it fall through to the default (`rowLimit = 20`). Only keep a bespoke constant where a table's natural row count is small and stable enough that pagination is irrelevant (none identified so far) or where D5's own "leave alone" carve-out for inline prose lists applies (`ModuleSectionBuilder`'s conflict-groups sentence, etc. — those were never `STCompact` calls and are unaffected by this amendment). | §9.1, §9.4, §9.5, §9.7, §10 |
| D6 | **DECIDED: split by what each flag actually does; both destination classes already exist.** `ProduceRawExports` (String, also on `WeakReferenceAnalysisOptions`) and `ProduceClusterExports` (ThreadStackCluster, §9.24) control whether an extra output *artifact* gets written alongside the report (`.ndjson.gz` raw samples, cluster export files) — a report/output-format concern, same category as [ReportOptions.cs](../../src/DumpDetective.Core/Options/ReportOptions.cs)'s existing `SeparateJson`/`PreRender`. **Move both to `ReportOptions`.** `SurfaceProbingExceptions` (Collection, §9.17 — confirmed at [CollectionAnalyzer.cs:1289,1395,1508,1582,1687](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs#L1289) gating whether internal reflection/probing exceptions get surfaced as diagnostic output vs. silently swallowed) controls visibility into analyzer internals, not analysis results — same category as [DiagnosticsOptions.cs](../../src/DumpDetective.Core/Options/DiagnosticsOptions.cs)'s existing `EnableMemoryDiagnostics`/`EnablePerformanceDiagnostics`. **Move it to `DiagnosticsOptions`.** No new options class needed. **Correction from V4 (§11.3): `EnableDiagnostics` (EventLeak, §9.19) is not live — there's nothing to move.** `EventLeakAnalyzer.cs` never reads it; delete it outright instead of moving it. | §9.12, §9.17, §9.19, §9.24 |
| D7 | **DECIDED, with a code-grounded justification for each, not just "keep Balanced."** `ScanStrategy` → `FullScan`, but this alone is not exact: [AllocationPatternAnalyzer.cs:170-171](../../src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L170-L171) shows `FullScan`'s `scanLimit` is still `Min(metrics.Count, MaxScanItemsAbsolute)` (10,000 Balanced/default, 20,000 Full) — since `TypeAggregates` was measured at ~50-100k distinct types on a 25GB dump (§9.1's Q5), `FullScan` would still silently truncate classification on exactly the large dumps this tool targets. **`MaxScanItemsAbsolute` must be deleted alongside `ScanStrategy`**, not kept as a safety cap — the classification loop is O(distinct types) with cheap cached lookups, the same negligible-cost shape already established for Boxing (§9.1); this wasn't called out in the original §9.30 audit and is an addendum to it. **`SelectionPriority`: keep `ClassificationFirst` only, delete `LongLivedFirst` and `Mixed` — a correctness fix, not a preference.** `LongLivedFirst` ([:304-349](../../src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L304-L349)) walks one globally-sorted list once and fills buckets incrementally, stopping each at `TopTypeLimit` — scan-order-dependent, so a type that's genuinely the #1 most-transient type in the heap can be silently excluded from the "top transient" table if other buckets filled first. `ClassificationFirst` ([:192-302](../../src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L192-L302)) classifies every candidate into its bucket first, then sorts and takes top-N independently per bucket — the only way to guarantee each table's top-N is actually the top-N for that category. `Mixed` is unused by every preset and documented as "reserved for future tweaks" — dead code, delete outright. **`SelectionMode`: keep `CompositeScore` only — demotes to a Category-1 display-ranking choice, not a correctness question, once the above lands.** Classification (Transient/Retained/Mixed) is decided purely by fixed threshold comparisons on `Gen0Pct`/`longLivedRatio` ([:215-219](../../src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L215-L219)) — `Mode` never affects whether a type is correctly classified, only which sort key orders the top-N shown within each bucket (its effect on which types survive the `MaxScanItemsAbsolute` prefix disappears once that cap is deleted). `CompositeScore` is what Balanced and Full already independently converged on — it blends Gen0%/Gen2/LOH into a signal aligned with "interesting allocation pattern," unlike raw count/size which just surfaces "the biggest collection" regardless of whether it's pathological; delete `TopByCount`/`TopByGen0Pct`/`TopBySize`. **`Gen0Weight`/`Gen2Weight`/`LohSizeWeight`** (feeding `CompositeScore`) are the same shape as D4's semantics-vary-by-tier defect — folded into D4's resolution: keep Balanced's 1.0/1.0/1.0 (equal weighting is the least arbitrary default), flagged shaky/revisit-with-field-data like D4's other flagged thresholds. | §9.30 |
| D8 | **DECIDED: delete `AdaptForSize` entirely — it isn't a memory guard, and it's actively double-applying a bug today.** [AnalyzerExecutionService.cs:52](../../src/DumpDetective.Cli/Execution/AnalyzerExecutionService.cs#L52) calls `AdaptForSize` (divides `MaxSampledStackSnapshots`/`MaxThreadsToCaptureSnapshots` by 4/2/1 before the tier's) before the analyzer runs, then `ThreadAnalyzer.ComputeSamplerCapacity` ([ThreadAnalyzer.cs:22-42](../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L22-L42)) independently looks up the *same* size tier and re-applies the *same* divisor to the already-divided value — on a Large dump, Balanced's `MaxSampledStackSnapshots = 20` becomes 5 via `AdaptForSize`, then 1 via `ComputeSamplerCapacity`, a 16x reduction instead of the intended 4x. This is a real bug independent of the exactness work, found while investigating D8. Root cause is a category error: `MaxSampledStackSnapshots`/`MaxThreadsToCaptureSnapshots` were treated as memory guards needing dump-size scaling, but `ThreadWithStackTrace` ([ThreadAnalyzer.cs:784-793](../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L784-L793)) — a `ClrThread` ref plus a `List<ClrStackFrame>` already bounded by `MaxFramesForThreadScan` (itself deleted, §9.23) — is small and fixed-size, not something whose per-item cost scales with dump size; and per [performance-checklist.md](../performance-checklist.md)'s Thread Analysis section, full thread enumeration/categorization already runs over every thread regardless of this cap — these knobs only govern how many illustrative example stack traces get attached to the report. **They're ordinary Category-1 display-example limits, not memory guards**, so there's nothing left for a dump-size-based auto-scaling layer to protect. Delete `AdaptForSize`; keep `MaxSampledStackSnapshots`/`MaxThreadsToCaptureSnapshots` as fixed (non-scaling) Category-1 defaults per D5's mechanism. `ComputeSamplerCapacity`'s own `Math.Min(capacity, totalThreads / 10)` term is a different, defensible thing — a "don't oversample a small thread population" statistical sanity guard, not a bytes cap — keep it on its own merits, independent of `AdaptForSize`. | §9.23 |
| D9 | **DECIDED: delete both enums, same as D1's reasoning — `AsyncChainDetectionMode` turned out to be an even easier case.** `ReferenceChainSearchMode` is already resolved (D1): collapse to the single exact bidirectional strategy. `AsyncChainDetectionMode` (Thread) has three values, but only one changes what's counted: `Disabled` skips `AsyncChainThreadCount`/`MaxAsyncChainDepth` entirely — a real correctness effect, but one that contradicts the exactness goal outright (a user could silently get an under-reported/zero count with no indication) rather than being a legitimate mode to preserve. `CountOnly` vs `Full` compute the *identical* counts/totals ([ThreadAnalyzer.cs:500-520](../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L500-L520)) — the only difference is whether the illustrative example stack trace gets a wider frame window for display, and that window is built from frames the walk already captured in memory (`availableFrames`), so `Full` costs nothing extra over `CountOnly`. There's no genuine "more thorough but slower" tradeoff here to preserve as a knob at all. **Delete `AsyncChainDetectionMode` entirely, hard-code the `Full` behavior** (matches §9.23's own audit table, already scoped as "collapse to Full" before D9 was raised) — free correctness (chain counts always computed) at zero cost, unlike `ReferenceChainSearchMode` where dropping `Fast` has a real, to-be-measured wall-clock cost (D1's follow-up). | §9.20, §9.23 |
| D10 | **DECIDED: no new shared options surface — the premise dissolves once connected to M9 and D5.** The sampler mechanism is already shared and parameterized, not duplicated: [TypedResourceSampler.cs:67-80](../../src/DumpDetective.Analysis/Analyzers/TypedResourceSampler.cs#L67-L80)'s `InstanceStateSampler<TSnapshot>` already takes `maxSamplesPerType`/`topNCap` as constructor parameters — only the three call sites' literal values (500/50, 500/50, 500/20) differ. `MaxStateSamplesPerType` (500 everywhere) is expected to be **deleted outright, not promoted** — M9 (§11.4) already frames it as bounded by resource-instance count (open connections/channels/HTTP clients — always a small fraction of a heap's population), expected cheap once measured, matching the "vestigial cap" pattern established throughout this doc (§4); if there's no cap left, there's nothing to build a config surface for. `TopSampleCap`/`TopOpenCap`/`TopFaultedCap`/`TopHttpClientSampleCap` (50/50/20) are ordinary Category-1 detail-table limits, already covered by D5's `CompactTable`/`STCompact` + pagination mechanism — no new options class needed there either. **Action:** delete `MaxStateSamplesPerType` once M9 confirms it's cheap; if M9 surprises us and shows it's genuinely needed on some dump shape, fall back to one shared `private const` across the three analyzers (not a new `AnalysisOptions` class) — none of the three ever had one, and introducing one now just to hold a single safety-net int would be more machinery than the need justifies. | §9.32-9.34 |

### 11.3 Verify before deleting

| # | Verification | Source |
|---|---|---|
| V1 | **CONFIRMED orphaned — delete.** No reflection- or serialization-based consumer exists: grepped every `System.Reflection.GetProperty`/`GetProperties` call site in `DumpDetective.Analysis` (`GCHandleAnalyzer.cs`, `HangAnalyzer.cs`) and both operate on ClrMD-resolved dump types, not on `AnalysisOptions`/config binding. No `DependentHandleAnalyzer.cs` exists anywhere in `src` (only `Options/DependentHandleAnalysisOptions.cs`) — no planned-but-unbuilt analyzer file or stub either. `DependentHandleAnalysis` on `AnalysisOptions`/`ResolvedExecutionOptions` is set but never read by any analyzer; only CLI config plumbing (`CliConfigurationModels.cs`, `ConfigurationResolver.cs`) and the JSON source-gen attribute reference it. Safe to delete the class and all four plumbing references per §9.6's original work item. | §9.6 |
| V2 | **CONFIRMED live, not dead — the original grep was incomplete.** `AsyncStateMachineAnalysisOptions.TopTypeLimit` (declared at [AsyncStateMachineAnalysisOptions.cs:5](../../src/DumpDetective.Core/Options/AsyncStateMachineAnalysisOptions.cs#L5)) is genuinely read at [AsyncStateMachineAnalyzer.cs:109](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs#L109), and is distinct from `AllocationPatternAnalysisOptions.TopTypeLimit` (a same-named but unrelated property on a different class — confirmed by checking the `options` variable's type at each call site). It's used only as an initial-capacity hint at :109/:114, not a loop bound — the field-metadata analysis loop at :116 iterates every candidate regardless (bounded separately by `TypeCandidateLimit`, already flagged for deletion). Its real effect is truncating `TopStateMachineTypes` in the domain result, confirmed via `AsyncStateMachineDomainResult.cs:40` and the trend comparer. **Correction: this is an ordinary Category-1 "rows" knob, same shape as its table neighbors `SuspendedMethodMapLimit`/`TopCapturedSizeEntries`** — §9.13's "verify: no use found" action should read "move to render," not "delete." | §9.13 |
| V3 | **CONFIRMED: `SkipArrays` prunes traversal, not just presentation — a real correctness gap, not a display concern.** Traced `IsNoisyType(type, skipArrays)` into `RootPathFinder`/`IndexBackedBidirectionalSearch`, where the `isNoise` predicate is a hard traversal-pruning gate (`if (_isNoise(type)) { ...; yield break; }` in both `ForwardNeighbors` and `BackwardNeighbors`) — when `SkipArrays = true`, the BFS refuses to expand through *any* array object, treating it as a dead end. Since collections are almost universally array-backed (`List<T>`'s internal array, `Dictionary<TKey,TValue>`'s bucket/entry arrays, etc.), a real shortest reference chain routed through an array is not an edge case, it's the common case. **This defaults to `true` in Balanced** ([ReferenceChainOptions.cs:58](../../src/DumpDetective.Core/Options/ReferenceChainOptions.cs#L58)) — meaning ReferenceChainAnalyzer's default configuration today silently drops real paths through arrays, undiscovered by any prior audit pass. `System.String`/`System.Object` pruning (also inside `IsNoisyType`, always-on, not gated by `SkipArrays`) is comparatively safe — both are typically leaf nodes with no outgoing reference fields relevant to retention, so pruning through them doesn't lose real paths. **Action, folded into D1's exact-bidirectional-search work:** force `SkipArrays = false` for the exact search; it cannot be `true` and still support D1's "true shortest chain" claim. | §9.20 |
| V4 | **DONE.** Ran the §5 dead-knob grep (`grep -rn "KnobName" --include=*.cs src \| grep -v "/Options/"`) against all 232 unique option property names project-wide, not just the previously-audited analyzers. Found **6 more dead knobs** beyond the original three (`ModuleSelectionMode`, `IncludeExcludedModuleSummary`, `DeduplicationStringCountThreshold`), each individually verified by reading the analyzer, not just trusting a zero grep-hit count: `LongWaitThreshold` and `TopWaitingThreadsPerGroup` (`HangAnalysisOptions` — `HangAnalyzer.cs` never reads either), `PohThresholdPercent` (`GCGenerationAnalysisOptions` — `GCGenerationAnalyzer.cs` computes `PohBytes`/`PohObjects` but never thresholds against it), `AbsoluteDeadCountThreshold` (`WeakReferenceAnalysisOptions` — zero references in `WeakReferenceAnalyzer.cs`), `TopFinalizerTypesToShow` (`RetentionOptions` — zero references in any analyzer including `DominatorAnalyzer.cs`), and `EnableDiagnostics` (`EventLeakOptions` — zero references in `EventLeakAnalyzer.cs`). **`TopSubscriberTypesToShow` (`EventLeakOptions`) is worse than merely unused** — [EventLeakAnalyzer.cs:315-316,337](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs#L315-L337) hardcodes `private const int TopCorrelationEntries = 20` instead of reading the configurable option at all. This corrects two decisions already made this session: **D4** treated `LongWaitThreshold` as a real, meaningful semantic threshold worth a rationale comment — it's dead code, delete it, no comment needed. **D6** planned to move `EnableDiagnostics` to `DiagnosticsOptions` — there's no behavior to move, delete it instead. Also corrects §9.19 and §9.25, which listed `TopSubscriberTypesToShow` and `TopWaitingThreadsPerGroup` as live "Category 1 — move to render" knobs; neither is live, so there's nothing to move — delete the options, the render layer keeps whatever hardcoded/default behavior already exists independently of them. | §5, §9.3, §9.12, §9.19, §9.23, §9.25 |

### 11.4 Measurements deferred from Q5

Fold these into the same real-dump run as B3/B4 — **one dump at a time, foreground**.

| # | Measure | Source |
|---|---|---|
| M1 | **MEASURED, negligible — safe to delete.** Real 3.35GB dump (`Crash_IIS_BALTSTPRD`, 266 total modules across all AppDomains — already past Balanced's `ModuleEnumerationLimit=50`, so the cap genuinely bites on this dump). `ModuleAnalyzer.AnalyzeAsync`, Balanced default (capped at 50, `TypeEnumerationMode` unaffected since `Full` is already the default): 506 ms. Fully uncapped (`ModuleEnumerationLimit=int.MaxValue`, all 266 modules, `TypeEnumerationMode.Full`, `PreferIndexOnly=false`): 178 ms — faster, not slower, almost certainly a JIT/ClrMD-metadata-cache warm-up artifact from running capped first rather than a real negative cost, but either way both numbers are sub-second against the ~10-minute nominal budget (§1). Confirms §9.3 Q5's "expect seconds, not minutes" — it's actually milliseconds. Test: [ModuleAnalyzerUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/ModuleAnalyzerUncappedRealDumpTests.cs), run via `DD_RUN_DISCREPANCY_TESTS=1`, foreground, one dump at a time. | §9.3 |
| M2 | **PARTIALLY MEASURED — index-scan cost confirmed cheap, per-instance cost still an estimate.** Same 3.35GB dump: capped (Balanced: `HistogramTopTypeLimit=10`, `HistogramInstanceCapPerType=1000`, `TypeCandidateLimit=200`) took 2,132 ms; fully uncapped (all `int.MaxValue`) took 1,728 ms — no real difference, within warm-cache noise. **Caveat: this dump only has 11 total async state machines across 7 types, so candidate selection was identical in both runs and the per-type instance cap never actually bound in either — the doc's own worst-case example (500,000 pending state machines across 60 types, §9.13 Q7) was not exercised.** What *is* confirmed: the second full-index pass itself (`cache.EnumerateIndexedEntriesAsTuples()` over 14.6M objects, dictionary-lookup-and-skip per entry) costs ~2s and is cap-independent — both runs did the full scan since the early-exit (`typesStillOpen == 0`) never fired given how few state machines exist. The additional cost of uncapping `HistogramInstanceCapPerType` specifically — one extra `ClrInstanceField.Read<int>` call per additional instance — remains an estimate, not a measurement: bounded to roughly hundreds of ms to low seconds even at the cited 500K-instance worst case (field reads are sub-microsecond to low-microsecond), nowhere near the ~10-minute budget, but a dump with a genuinely large state-machine population would be needed to measure it directly. Test: [AsyncStateMachineUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/AsyncStateMachineUncappedRealDumpTests.cs). | §9.13 |
| M3 | **MEASURED, negligible — safe to delete.** Same 3.35GB dump. Balanced default (`SampleStride=100`, `SparseSampleLimit=500`, `TopSparseLimit=10`; 1,734,392 array objects, `ScanLimited: True` — the 500 cap genuinely bites): 39 ms. Fully uncapped (walk every element of every qualifying array, no candidate-set or discovery cap, `ScanLimited: False`): 42 ms — a 3ms delta, noise-level. `TopSparseArrays` count happened to stay at 3 either way on this dump, but that's this dump's data, not a guarantee: the capped run's `ScanLimited: True` means it only ever considered the top-500-by-size candidates, so a real sparse array outside that window could be missed on a different dump even though this one didn't hit that case. **Found in passing: `TopSparseLimit=int.MaxValue` throws `OutOfMemoryException`** — [ArrayAnalyzer.cs:241](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L241) passes it directly as a `List<SparseArrayEntry>` capacity (`new List<SparseArrayEntry>(options.TopSparseLimit)`), which tries to allocate a 2-billion-element backing array. Folded into F10 (§11.6) — uncapping this knob needs a capacity-safe rewrite (e.g. don't pre-size, or clamp the hint), not a literal `int.MaxValue`. Test: [ArrayUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/ArrayUncappedRealDumpTests.cs) (uses `TopSparseLimit=1_000_000` to work around the bug for measurement purposes). | §9.11 |
| M4 | **MEASURED, cheap even without the dominator-tree rewire — simplifies the decision.** Same 3.35GB dump, Stage B (exact dominator tree) built. 1,411 total roots, 1,404 severity-scored findings. Balanced default (`PathSearchTopN=25`, only 25 of 1,404 findings get a root path): 874 ms. Fully uncapped (every one of the 1,404 findings gets a root path): 568 ms — faster, not slower (56x more candidates walked). **Important nuance: this is not yet the dominator-tree-backed path the doc originally envisioned.** [GCRootAnalyzer.cs:97-101](../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs#L97-L101) already skips the retained-*bytes* BFS when the exact tree can answer it (§7.1 of phase1-integration.md), but the separate forward-type-names-along-the-path walk (`BoundedGraphWalk.CollectForwardTypeNames`, [:122](../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs#L122)) is still called unconditionally per candidate and has *not* been re-pointed at `IDominatorTreeProvider.EnumerateRetainedSet` (which per phase1-integration.md has no caller yet). This measurement shows that **even the still-BFS-backed path walk, bounded by `MaxBfsNodes`/`MaxBfsDepth`, is already affordable at 56x scale** — roughly 0.4ms/candidate average. Extrapolated (not measured) to a dump with tens of thousands of roots, this would still land in the tens-of-seconds range, well inside the ~10-minute budget. **Conclusion: `PathSearchTopN` can be deleted without waiting on the `EnumerateRetainedSet` rewire** — that rewire remains worth doing for its own sake (an O(1) parent-pointer read beats a bounded BFS), but is no longer a hard prerequisite. Test: [GCRootAnalyzerUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/GCRootAnalyzerUncappedRealDumpTests.cs). | §9.16 |
| M5 | **PARTIALLY MEASURED — surfaced that the real cap lives in Phase 1, not the audited options class; decided to delete it too.** Same 3.35GB dump, 2,421,447 total strings. Running `StringAnalyzer` with `StringAnalysisOptions.MaxStringsToDedup`/`MaxUniqueStringTracking` capped (Balanced: 50,000/200,000) vs. fully uncapped (`int.MaxValue`) produced **byte-for-byte identical results** — `StringsSampled: 321,266`, `SampledUniquePatterns: 147,400`, both runs, no difference at all. Traced why: `StringAnalyzer.Analyze`'s fast path ([StringAnalyzer.cs:628-669](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs#L628-L669)) reads `heapIndex.StringDedupIndex`, a Phase 1 satellite index built once during `PrebuildHeapIndex` and governed by its own **hardcoded** `const int MaxDedupUnique = 500_000` ([DiskBackedObjectIndexWriter.cs:167](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L167)) — completely independent of the `StringAnalysisOptions` values under audit in §9.12's table, which only matter in fallback branches (no prebuilt index, or a live participant scan) that don't execute once Phase 1's index exists, i.e. essentially never in production. **Decision: delete `MaxDedupUnique`'s cap entirely as well, not just the Phase 2 options** — same reasoning as every other vestigial cap in this doc, and the same "genuine resident-bytes cap" analysis in §9.12 applies to it more than to the options that were actually audited. **Not stress-tested by this measurement**: this dump's real unique-string population (321,266) is already under the 500,000 cap, so removing it changes nothing observable here — there is no different number to measure on this dump. Validating the "~10M unique strings → hundreds of MB" estimate needs either a dump with a much larger unique-string population, or accepting the estimate (both `StringFingerprint` and `StringDedupEntry` are small fixed-size records, so linear extrapolation from the ~34MB allocated for ~147K unique patterns here to 10M would land around 2-2.5GB — worth treating as an estimate, not a confirmed number, same caveat shape as M2). Test: [StringAnalyzerUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/StringAnalyzerUncappedRealDumpTests.cs) (measures the Phase 2 options only; does not yet exercise `MaxDedupUnique`). | §9.12 |
| M6 | **MEASURED, real cost, and a free fix exists.** Confirmed by reading [HeapTopologyAnalyzer.cs:284](../../src/DumpDetective.Analysis/Analyzers/HeapTopologyAnalyzer.cs#L284) (`CountObjects` calls `segment.EnumerateObjects()`) — this is a live ClrMD walk, **not** served from the disk-backed index; `HeapTopologyAnalyzer.Analyze` doesn't even take `IHeapAnalysisCache` as a parameter. On the 3.35GB dump: `CountSohObjects=false` (default): 606 ms. `CountSohObjects=true` (full live SOH walk): 10,780 ms — a real **10.2-second cost**, not noise like M1/M3/M4. Still affordable against the ~10-minute nominal budget (~1.7%), so the doc's original recommendation ("set to `true` permanently") is still viable as-is. **But there's a free alternative worth taking instead of paying this cost:** Phase 1's index already has the exact total object count across the *entire* heap (`HeapIndexBuildResult.ObjectCount`, already used by every other measurement in this section — e.g. 14,620,162 for this dump). Since `HeapTopologyAnalyzer` already counts LOH/POH/Frozen objects exactly via their own (cheap, small-segment-count) live walks, exact SOH count is just `TotalObjectCount - LohCount - PohCount - FrozenCount` — arithmetic, zero additional heap traversal. Recommend rewriting to this instead of enabling the live SOH walk; it gets exactness for free rather than for 10 seconds. Test: [HeapTopologyUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HeapTopologyUncappedRealDumpTests.cs). | §9.28 |
| M7 | **CONFIRMED index-backed, measured negligible delta.** [DiskBackedObjectIndexWriter.cs:1107-1119](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L1107-L1119) shows the Tasks section (`TaskIndex.bin`) is built unconditionally during Phase 1 (task candidates collected during the main heap scan, not gated on which analyzers are active) — so `AsyncTaskAnalyzer.LoadTaskEntries` hits the fast index-backed path (`TaskIndexReader.ReadTaskIndexFile`/`InMemoryTaskCandidates`), not the live `heap.EnumerateObjects()` fallback (`ScanRawHeapForTasks`), which only runs if no index exists at all. On the 3.35GB dump (12,071 total tasks — under all three caps, so they didn't genuinely bind here either): Balanced default (`MaxTasksToScan=50000`, `MaxTcsToScan=20000`, `MaxVtsToScan=20000`): 4,447 ms. Fully uncapped: 4,758 ms — a 311ms delta, negligible relative to the ~4.4s baseline both runs share. That shared ~4.4s baseline is unrelated to these three caps (likely per-task state-flag re-reads and `MaxContinuationDepth`-bounded continuation walks, a separate Category 4 knob already flagged for deletion) — same caveat as M2/M5: this dump's task population is under all three caps, so the "population exceeds the cap" case isn't stress-tested, but the qualitative Q2 concern (index-backed vs. live walk) is conclusively resolved by code, not just this dump's numbers. Test: [AsyncTaskUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/AsyncTaskUncappedRealDumpTests.cs). | §9.29 |
| M8 | **MEASURED, negligible — safe to delete.** Same 3.35GB dump, 135 total threads (38 alive) — a modest population. Using `ThreadAnalyzer`'s documented direct-invocation fallback path: Balanced default (`MaxFramesForThreadScan=8`, `MaxStackRootsToCount=256`): 25 ms. Fully uncapped (`MaxFramesForThreadScan=100,000`, `MaxStackRootsToCount=100,000`): 2 ms — both effectively instant, no meaningful cost either way. Caveat: this dump's thread count (135) and presumably stack depths are modest; a dump with thousands of threads or pathologically deep recursive/async stacks isn't stress-tested here, but stack frame counts don't scale with heap size the way object counts do, so there's no structural reason to expect this to behave differently at larger scale. Test: [ThreadUncappedRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/ThreadUncappedRealDumpTests.cs). | §9.23 |
| M9 | **MEASURED, negligible — after fixing a real bug in the test setup itself.** First attempt read back zero instances for all three resource types, which turned out to be a test-harness bug, not the dump: passing these analyzers as `activeAnalyzers` to `PrebuildHeapIndex` only affects Stage B gating ([DiskBackedObjectIndexWriter.cs:216](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L216)) — it does not register them as heap-index-scan participants. The real wiring lives in `AnalysisPipeline.ExecuteAsync` ([AnalysisPipeline.cs:41-44](../../src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs#L41-L44)), which collects `IHeapIndexScanParticipant` analyzers and runs `HeapIndexScanDispatcher` *before* calling `AnalyzeAsync`; calling `AnalyzeAsync` directly (as M1/M3/M4/M6/M8 safely do for index-backed Phase-2-only analyzers) skips that step, so `OnHeapEntry` never populates candidate state — DbConnection/WcfChannel/HttpObject have no unconditional Phase-1 satellite index to fall back on the way String (`StringDedupIndex`) and AsyncTask (`TaskIndex.bin`) do, so this gap produced silent zeros instead of a visible error. **After running `HeapIndexScanDispatcher` first:** 503 DB connections, 1,210 WCF channels, 102 HTTP objects — all real, all `StateScanCapped: False` (the 500 cap is per-*type*, not a global total, so even 1,210 total WCF channels didn't trip it if spread across multiple channel types). Elapsed: 109 ms / 1 ms / 1 ms — confirms the "expect cheap" reasoning (D10, §11.2) with real nonzero data, consistent with every other index-backed per-item measurement in this section. Per-type cap still didn't bind on this dump, so the exact cost of exceeding it per-type remains an extrapolation, not a direct measurement — same shape of caveat as M2/M5/M7. Test: [TypedResourceQuartetRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/TypedResourceQuartetRealDumpTests.cs) (now runs the dispatcher correctly). | §9.32-9.34 |
| M10 | **MEASURED, comfortably within budget.** Ran the real production analyzer set (`DefaultAnalyzerFactory.CreateAnalyzers()`, all 33 registered analyzers, real module order — not a hand-copied subset) through `AnalysisPipeline.ExecuteAsync` against the 3.35GB dump with Stage B enabled, sampling `Environment.WorkingSet` on a 200ms background timer throughout the whole run to catch a transient mid-run peak a before/after snapshot could miss. All 33 analyzers reported `Success` (0 failures) — a useful end-to-end validation on its own. Working set immediately after Phase 1 (Stage B construction done): ~3.95 GB. Peak working set across the *entire* 33-analyzer run (36.6s, Stage B resident throughout): ~4.75 GB — roughly 0.8 GB above the Stage-B-only figure, attributable to the other analyzers' own working memory rather than the tree itself growing. Comfortably inside `ExactDominatorTreeMemoryBudgetBytes` (20 GB) — only ~24% of budget used on this dump. **Caveat:** this dump is smaller than the 25.6GB reference dump used for phase1-integration.md §5's 6.42GB Stage-B-construction-peak figure, so this doesn't directly confirm the budget at that scale — but the measured overhead ratio here (peak ÷ Stage-B-only ≈ 1.2x) extrapolated to the 25.6GB figure would land around 7.7 GB, still comfortably under 20 GB. A direct run against the 25.6GB dump would close this out fully. Test: [DominatorConcurrentResidentMemoryRealDumpTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DominatorConcurrentResidentMemoryRealDumpTests.cs). | §10a |

### 11.5 Ordering constraints (violating these blows the budget or the bisect)

1. **`CollectionAnalyzer.cs:1154` lands alone, first.** Only runtime read of `AnalysisProfile`; only predicate rewrite in the plan. | §8.3, §9.17
2. ~~**Do not delete GCRoot's caps against the existing BFS.**~~ **Superseded by M4 (§11.4): measured cheap without B2/`EnumerateRetainedSet` landing first.** `PathSearchTopN` removal was assumed O(candidates x graph) and unacceptable against the existing BFS, but a real-dump measurement found 1,404 candidates (56x the capped default) walked in 568 ms — faster than the capped run. `PathSearchTopN` can be deleted now; the `EnumerateRetainedSet` rewire remains worth doing for its own sake but is no longer an ordering prerequisite. | §9.16, §11.4 M4
3. **Collection is blocked on ReferenceChain** — it embeds `ReferenceChainOptions` and calls `IsNoisyType`. | §9.17
4. **String needs a restructure, not cap removal.** Two genuine memory guards; exact dedup needs a streaming hash-count pass. | §9.12
5. **B1 before the first exactness commit**, so reviewers have a coherent standard.

### 11.6 Defects found in passing (fix while editing, each behaviour-neutral)

| # | Defect | Source |
|---|---|---|
| F1 | `new ModuleAnalysisOptions()` ≠ `ModuleAnalysisOptions.Default` on two booleans. | §9.3 |
| F2 | `GCRootAnalysisOptions.MaxBfsDepth = 30` (Full) is silently clamped to 20 by `AbsoluteMaxDepth`. | §9.16 |
| F3 | String's `SamplingMode` multiplies the other caps; the configured value is never the applied value. | §9.12 |
| F4 | `ReferenceChainOptions` 0-sentinel resolution — setting `0` yields 50,000. | §9.20 |
| F5 | `RetentionOptions.MaxLeakScanObjects` XML doc has a lost sentence fragment. | §9.22 |
| F6 | LohFragmentation comment says "≤ 100 objects" where the effective default is 20. | §9.8 |
| F7 | Boxing/Module determinism sorts, and Thread's `SamplingSeed`, exist only to make truncation reproducible — remove with the caps. | §9.1, §9.3, §9.23 |
| F8 | `ThreadAnalysisOptions.AdaptForSize` divides two caps by 4/2/1 on dump size — invisible third scaling layer. **Worse than described (D8, §11.2): the same divisor is applied a second time** by `ThreadAnalyzer.ComputeSamplerCapacity`, independently re-deriving the size tier — a Large-dump 20-snapshot default silently becomes 1, not the intended 5 (16x reduction instead of 4x). Resolved by deleting `AdaptForSize` outright, not fixing the double-application. | §9.23, D8 |
| F9 | AllocationPattern's `TopTypeLimit x ScanMultiplier` compound into the real scan limit. | §9.30 |
| F10 | Array's `TopSparseLimit` is used directly as a `List<SparseArrayEntry>` constructor capacity ([ArrayAnalyzer.cs:241](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L241)) — raising it to remove the loop bound (e.g. `int.MaxValue`) throws `OutOfMemoryException` instead of uncapping the search. Found while measuring M3 (§11.4). Needs a capacity-safe rewrite when the cap is removed. | §9.11, §11.4 M3 |

> **Pattern worth naming:** F1-F4, F8 and F9 are **six** separate instances of *configured value ≠
> applied value*.
> The options surface is not verifiable by reading it, which is why §5's grep step is procedural
> rather than advisory. Any residual knob that survives this migration should be checked against this
> failure mode before it is kept.

---

## 12. Test strategy

- Commit (1) per analyzer must leave output **identical** for a config that sets no profile. Verify
  with the existing snapshot mechanism —
  [BASELINE_BEHAVIOR_SNAPSHOTS.md](../improvements/BASELINE_BEHAVIOR_SNAPSHOTS.md) and
  [PHASE0_BASELINE_RUNBOOK.md](../improvements/PHASE0_BASELINE_RUNBOOK.md) — rather than a new one.
- Commit (2) changes output by design. The assertion is *direction*: counts go up or stay equal,
  never down; `*Capped` flags go false; runtime stays inside budget.
- **Compare allocations, not seconds.** Per the memory-profile doc, allocated bytes was stable to
  0.001% between identical runs while wall-clock varied 2.7x on page-cache state alone.
- Real-dump validation: **one dump at a time, foreground**, per the CLAUDE.md rule. Never
  `run_in_background`, never in parallel.

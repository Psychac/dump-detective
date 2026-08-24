# Exact analysis: removing scan caps, sampling, and the AnalysisProfile system

**Status (2026-08-24): this plan is complete.** All 33 registered analyzers have been through the
per-analyzer exactness pass (§9) — 29 fully GREEN, 4 deliberately-deferred AMBER (§9.12 String,
§9.17 Collection, §9.19 EventLeak, §9.20 ReferenceChain, each with a real restructuring intentionally
deferred rather than shipped half-done — see each section for what's left and why). §11's
pre-implementation checklist (blockers B1-B4, decisions D1-D10, verifications V1-V4, measurements
M1-M10) is fully closed out, and §8's residual profile-only cleanup (the `AnalysisProfile` enum,
resolver plumbing, dead parsers, config keys, tests, and docs) is done. The `AnalysisProfile` system no
longer exists anywhere in `src`. See §9 for the per-analyzer implementation notes and §7 for the
verdict table.

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
| 28 | HeapTopology | non-heap | **GREEN** ✅ DONE | 1 of 1 | §9.28 — a literal exact/not-exact switch, defaulting to **not**; shipped as free arithmetic derivation instead of the live walk |
| 29 | AsyncTask | non-heap | **GREEN** ✅ DONE | 8 of 8 | §9.29 — options class deleted outright |
| 30 | AllocationPattern | — | **GREEN** ✅ DONE (was AMBER) | 13 of 13 | §9.30 — three enums collapsed to one algorithm via D7; no more tier-varies-the-algorithm defect, so AMBER resolves to GREEN once implemented |
| 31 | WeakReference | — | **GREEN** ✅ DONE | 4 of 5 (1 kept, `ProduceRawExports`, deferred to D6's cross-cutting move) | §9.31 — `HandleScanCap` truncates the handle table, not a derived list |
| 32 | DbConnection | typed-resource | **GREEN** ✅ DONE | 2 of 2 | §9.32-9.34 — **no options class at all**; bounds are `private const`, never preset-varied |
| 33 | WcfChannel | typed-resource | **GREEN** ✅ DONE | 2 of 2 | §9.33 — same shape; caps faulted-channel detection specifically |
| 34 | HttpObject | typed-resource | **GREEN** ✅ DONE | 2 of 2 | §9.34 — headline counts already exact; only the drill-down sample is capped |
| 35 | LeakCandidate | typed-resource | **GREEN** ✅ DONE | 1 of 1 | §9.35 — already index-backed and exact; a template for "built right from the start" |

> Rows 4 and 22 are cross-references, not distinct analyzers (roster correction, top of document) —
> **33 real rows for 33 registered analyzers.**

---

## 8. Residual profile-only cleanup (not emergent from the audit) — ✅ DONE

These do not fall out of any analyzer's exactness work and must be done explicitly, after the audit
retires the per-analyzer `Preset()` methods.

**Implementation notes (as shipped):**

- **Items 2, 3, 5, 7 were already resolved incidentally** by earlier per-analyzer work (§9.17
  Collection deleted `CollectionAnalysisOptions.Profile`/`CollectionAnalysisOptionsModel.Profile`
  and the `options.Profile == AnalysisProfile.Fast` runtime check outright rather than rewriting the
  predicate as originally planned; `RetentionOptions.cs`'s stale `<see cref="AnalysisProfile"/>` was
  already gone). Verified each by grep before assuming, not by trusting the plan's stale line numbers.
- **Item 1's "dead duplicate" (`ConfigurationResolver.ParseAnalysisProfile`) plus the entire cluster
  it sat next to (`ResolveAnalyzerProfile`, `GetAnalyzerProfile`, the generic
  `BuildAnalyzerOptionsFromConfig<T>`, and `AnalyzerOptionsBuilder.BuildBalancedPresetFromCli`/
  `BuildValidatedBalancedPresetFromCli`) had all become fully dead** by the time every analyzer's
  `Preset()` was retired in §9 — zero remaining callers, confirmed by grep before deleting each.
  Deleted as one cluster rather than item-by-item.
- **Item 4 (collapse the resolver plumbing) was subsumed by the above** — nothing separate to do
  once the dead cluster was gone.
- **Item 6 (delete the enum + `CliConfigurationFileModel.Profile`), done, with the deprecation
  warning implemented literally rather than skipped:** deleting the `Profile` property means System.Text.Json
  silently drops an unrecognized `"Profile"` key with no signal to the user — added
  `ConfigurationResolver.WarnIfLegacyProfileKeyPresent`, which parses the raw config JSON with
  `JsonDocument` (independent of the strongly-typed model, specifically so the property could be
  fully deleted rather than kept around just to detect it) and emits a `ConsoleUx.Warning` naming
  the replacement (the `Analyzers` section) when a legacy `"Profile"` key is found at the root,
  before falling through to normal resolution.
- **Item 8, rewritten rather than deleted where real coverage existed.** `PresetBehaviorTests.cs`,
  `StringAnalyzerOptionsTests.cs`, and `ThreadAnalysisOptionsTests.cs` no longer existed (removed
  during §9.12/§9.23's own implementation passes). `ThreadStackClusterAnalyzerOptionsTests.cs` had
  already been repurposed to test something unrelated (artifact carrying) — nothing to change.
  `WeakReferenceOptionsTests.cs` was deleted outright (§9.31 — every test in it asserted deleted
  preset values). `ConfigurationResolverTests.cs`'s six profile-tier tests were replaced with tests
  matching present reality: a legacy `"Profile"` key is ignored and doesn't throw (including on
  invalid values, which used to `throw ArgumentException`), and field overrides still work
  regardless of legacy `"Profile"` keys anywhere in the config. No `Collection.Profile` assertions
  remained to delete (already gone).
- **Item 9 — all five docs annotated with a dated "Superseded note"** pointing at the relevant §9
  subsection, rather than rewritten in place: `allocation-pattern-analyzer-audit.md` (the
  cross-profile-comparison confusion this predates is moot once there's only one algorithm),
  `crash-analyzer-audit.md` (also confirmed Bug 1, adjacent to the profile mention, is independently
  already fixed), `dominator-tree-implementation-plan.md` and `dominator-tree-lengauer-tarjan.md`
  (D9's prediction that exact-mode gating would stay independent of the tier system held exactly as
  designed), and `root-path-search-blast-radius.md` (confirmed the `AnalysisProfile.Fast`-triggered
  exposure it describes — `ReferenceChainSearchMode`/`TryFindAnyRootPath_Fast` — no longer exists,
  while flagging that the doc's other four `SampleRootPathFinder` call sites are a separate,
  still-open concern this pass didn't touch). Each claim was verified against current source before
  writing the note, not assumed from the original audit text.
- **Done-check re-run clean**: `grep -rn "AnalysisProfile" src tests` (excluding this plan doc and
  the new explanatory comments/doc notes above, which are expected) returns nothing live;
  `Preset(AnalysisProfile`, `ParseAnalysisProfile`, `ResolveAnalyzerProfile`/`GetAnalyzerProfile`,
  and `options.Profile` all return zero matches in `src`.

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

[BoxingAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs)

**Shipped:** Deleted `TypeScanCap` (a 10,000-distinct-type cap that was silently under-reporting
`TotalBoxedObjects` on exactly the large dumps this tool targets) and the 17-line determinism-sort
workaround that existed only to make its truncation reproducible. `TopBoxedTypeLimit`/
`TopPaddingLimit`/`TopOversizedTypeLimit` moved to render — `BoxingDomainResult` now carries complete
ranked lists, `STCompact` paginates. `OversizedThresholdBytes` kept as a fixed constant; `Preset`/
`Default` deleted. Found and fixed a second, independent truncation in `BoxingSectionBuilder` (a
`.Take(TopTypesToShow)` stacked on top of the already-capped list) — this pattern (render layer
re-truncating an already-capped or already-complete list) recurs throughout §9 and is fixed the same
way everywhere: feed the complete list into `STCompact`, no custom `rowLimit`. `BoxingDomainResult`
never reaches the JSON report surface, so no schema version bump was needed here or anywhere else in
§9 (checked once, applies uniformly — not re-verified per analyzer below).

### 9.2 ObjectShape — **GREEN** ✅ IMPLEMENTED

[ObjectShapeAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs)

**Shipped:** `ObjectShapeAnalysisOptions` deleted outright — `InstanceCountCap` (a misleadingly-named
"top 200 types by instance count" cap on ClrMD metadata lookups) had been silently redefining two
headline totals, not just truncating a list: `TotalGcScanWork`/`AvgRefFieldsPerType` were computed
*inside* the capped loop, so they read as whole-heap metrics while actually covering only 200 types.
Now exact over every type. `TopListLimit` moved to render (`TopReferenceHeavyTypes`/
`TopValueHeavyTypes`/`TopBalancedTypes` already flowed into `STCompact` uncapped, so no
section-builder change was needed there). First "delete the whole options class" case — established
the wiring surface every later "options class deleted outright" row follows: remove from
`AnalysisOptions`, `CliConfigurationModels` (+ `[JsonSerializable]` entry), `ConfigurationResolver`,
`AnalyzerExecutionService`, `ResolvedExecutionOptions`, and any positional-constructor test call
sites.

---

### 9.3 Module — **GREEN** ✅ IMPLEMENTED (with the largest cascade so far)

Source: [ModuleAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs) ·
[ModuleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ModuleAnalysisOptions.cs)

**Shipped:** Deleted `ModuleEnumerationLimit` (a 50-module cap that truncated per-domain
`EstimatedManagedBytes`, not just the report) and, by cascade, four dependent knobs/enums it made
necessary: `EmitTruncationNotice`, `TypeEnumerationMode` (its `Sampled` mode was a first-1024-entries
prefix biased by metadata token order, not a real sample, plus the dead `Skip` mode), and two
already-dead knobs found in passing — `ModuleSelectionMode` and `IncludeExcludedModuleSummary`,
neither read anywhere in `src`. `PreferIndexOnly` (skip type enumeration when no `TypeAggregates`
index exists) was a correct degraded-path policy, not a tier — hard-coded as a plain `hasIndex` check,
option deleted. `TopLoadedAssembliesCount`/`TopModulesByHeapCount`/`TopModuleTypeCountLimit` moved to
render (`STCompact`, no `.Take()`). `HeavyModuleWarningThresholdBytes`/`DensityAnomalyMinBytes`/
`DensityAnomalyMaxTypes` kept as real semantic thresholds. Fixed a latent trap along the way: `new
ModuleAnalysisOptions()` and `.Default` disagreed on two boolean defaults — resolved by promoting the
Balanced values into the initializers before deleting both. Measured (M1): fully uncapped on a
3.35GB/266-module real dump ran in 178ms, actually faster than the capped 506ms run (warm-cache noise,
not a real cost) — confirmed metadata-table iteration, not heap work. Kept the per-domain
`Array.Sort` by size — it turned out to double as the sort behind `AppDomainSnapshot.TopModules`'
hard-coded top-8 narrative list, unrelated to the deleted cap. Deleted
`ModuleAnalyzerUncappedRealDumpTests.cs` outright (it existed only to A/B-measure the now-gone cap;
its result is preserved as M1 above) and cleaned a stale commented-out example from
`src/DumpDetective.Cli/config.json`.

### 9.4 GCGeneration — **GREEN** ✅ IMPLEMENTED

[GCGenerationAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs) ·
[GCGenerationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCGenerationAnalysisOptions.cs)

**Shipped:** `TopLohTypeLimit`/`TopGenProfileLimit` were pure post-aggregation output slicing (Q7:
"nothing corrupted" — first analyzer where that was true) — moved to render across both analyzer paths
(`BuildFromIndex` and the no-index `BuildFromTypeStatistics` fallback) and across the three
`GCPressureSectionBuilder` tables that had the same double-truncation shape as Boxing's §9.1 (analyzer
cap plus independent render-side re-slicing). `LohThresholdPercent`/`Gen0PressureThresholdPercent` kept
as real semantic thresholds. **`PohThresholdPercent` — V4 flagged it dead (nothing gated on it), but
that was a wiring gap, not vestigiality: wired it up instead of deleting it**, adding a new POH-share
`InsightFinding` to `GCGenerationFindingGenerator` mirroring the existing LOH-share finding. General
lesson carried forward: a knob V4 finds dead needs a "should this be wired up" check before defaulting
to deletion. Known pre-existing (untouched) accuracy gap: "Top LOH types" sources from two different
rankings depending on which list is non-empty (instance-count-filtered vs. byte-size-ranked) — noted
for a future follow-up, not a truncation defect.

---

### 9.5 GCHandle — **GREEN** ✅ IMPLEMENTED

[GCHandleAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCHandleAnalyzer.cs) ·
[GCHandleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCHandleAnalysisOptions.cs)

**Shipped:** `TopTypeCount` was pure post-aggregation slicing feeding six independent ranked lists —
moved to render (renamed `ToTopEntries`/`ToTopByteEntries` to `ToRankedEntries`/`ToRankedByteEntries`,
dropped the `take` parameter, replaced `.OrderByDescending().Take()` with explicit `List<T>.Sort` per
the no-LINQ-in-hot-paths rule). `TotalHandlesWarningThreshold`/`PinnedHandleTargetsWarningThreshold`/
`PinnedRetainedBytesWarningThreshold`/`DependentUnresolvedPercentWarningThreshold` kept as semantic
thresholds. **Found and fixed a second, deeper instance of §9.4's wiring-gap pattern:**
`GCHandleFindingGenerator` declared its own same-named properties with independently hardcoded
defaults and never actually read the resolved `GCHandleAnalysisOptions` values — the four "keep"
thresholds were fully plumbed through config/CLI and then silently went nowhere. Fixed by threading
the four thresholds through `GCHandleDomainResult` (populated by the analyzer from `options`) and
changing the finding generator to read `r.<Threshold>` instead of its own disconnected copies.
**Lesson confirmed twice now: a "keep, semantic threshold" verdict isn't safe until the finding
generator — not just the analyzer — is checked for actually reading it.**

---


### 9.6 DependentHandle options — **GREEN** ✅ IMPLEMENTED, and the class looked orphaned

[DependentHandleAnalysisOptions.cs](../../src/DumpDetective.Core/Options/DependentHandleAnalysisOptions.cs)

**Not a registered analyzer** — dependent-handle analysis is done by `GCHandleAnalyzer` (§9.5); this
class was pure configuration plumbing (`AnalysisOptions`, `CliConfigurationModels`,
`ConfigurationResolver`, `ResolvedExecutionOptions`) for a `TopCount` knob no analyzer read — the most
complete instance found of the "fully plumbed, config-exposed, but consumed by nothing" failure mode.

**Shipped:** Confirmed orphaned (no reflection/serialization consumer, no planned-but-missing
analyzer). Deleted the class outright and its five plumbing references plus two test call sites. No
schema-bump concern — same D2 reasoning as §9.1/§9.2 (never reaches the JSON report surface).

---

### 9.7 LockGraph — **GREEN** ✅ IMPLEMENTED

[LockGraphAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs) ·
[LockGraphAnalysisOptions.cs](../../src/DumpDetective.Core/Options/LockGraphAnalysisOptions.cs)

**Shipped:** Single knob `MaxContestedLocksToShow` was pure post-build output slicing, but capped two
independent lists (`topContestedTypes`, `contestedLockDetails`), not one — both moved to render.
`topContestedTypes` replaced `.OrderByDescending().Take()` with an explicit `List<T>.Sort`. Also found
and fixed `LockGraphSectionBuilder`'s own separate, unrelated local cap (`Math.Min(topTypes.Count, 8)`)
stacked on top of the analyzer cap — this section was actually the trigger for the §11.2 D5 amendment
(drop custom `rowLimit`s, let `STCompact` use its uniform default). Options class deleted outright,
same wiring pattern as §9.2 ObjectShape.

---

### 9.8 LohFragmentation — **GREEN** ✅ IMPLEMENTED

[LohFragmentationAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs) ·
[LohFragmentationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/LohFragmentationAnalysisOptions.cs)

**Shipped:** `TopSegments` was clean output slicing; `TopLargeObjectsCount` was worse — an early-return
inside the `LargeObjectTracker.ReadRecords` callback that truncated the `typeAggregation` dictionary,
not just the top-list, to whichever records happened to satisfy it first (index/on-disk order, no
size-relevance). **Found a second, more serious defect while fixing this: the index fast path's
surviving `topLargeObjects` list was never sorted by size at all** (unlike the heap-scan fallback,
which used a proper bounded top-K accumulator) — a "Top large objects" table in arbitrary file order
that looked plausible only because it was pre-truncated to 20 items. Fixed by adding an explicit sort
after `ReadRecords` and removing the early-return cap entirely (per Q8, `ReadRecords` already read
every record regardless — removing the cap only adds one cached `GetTypeByMethodTable` lookup per
object). Deleted the now-pointless `TrimLargeObjectCandidates` helper and the heap-scan path's
min-eviction top-K logic (LOH-sized objects are a naturally small population — no bound needed).
Options class deleted outright; collapsed a dead 4-arg `Analyze` overload found in passing.

---

### 9.9 SegmentReservation — **GREEN** ✅ IMPLEMENTED, but for an unusual reason

[SegmentReservationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/SegmentReservationAnalysisOptions.cs)

**Shipped:** This analyzer had no caps, limits, or sampling to begin with — already exact, zero knobs
deleted. The preset still had to die: `ThirtyTwoBitPressureThresholdBytes`/`RatioHighPressureThreshold`
are semantic thresholds that varied by tier (1.0-2.0 GB, 8.0-12.0x), meaning the same dump could be
"under pressure" at Fast and "over pressure" at Full — the tier was changing what the answer *means*,
not how hard the analyzer looked. `Preset`/`Default` deleted, Balanced's values promoted to field
initializers with D4 rationale comments (one anchored to 32-bit VA-space, one flagged as an unanchored
round number pending field data).

---

### 9.10 Jit — **GREEN** ✅ IMPLEMENTED, worst Q7 finding so far

[JitAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/JitAnalyzer.cs) ·
[JitAnalysisOptions.cs](../../src/DumpDetective.Core/Options/JitAnalysisOptions.cs)

**Shipped:** `MaxFramesPerThread` (200-frame stack-walk cap) truncated six independent accumulators at
once — managed/unmanaged frame counts, active-method set, frame-type histogram, tiered-compilation
detection, and large-method discovery — for any thread deeper than 200 frames, which is precisely the
case (deep async chains, runaway recursion) that hang/stack-overflow dumps produce and this analyzer
exists to diagnose. Deleted; the stack-walk loop now runs to completion for every live thread.
`TopMethodsLimit`/`TopFrameTypesLimit` deleted, moved to render (`STCompact`, no `rowLimit`).
`LargeMethodThresholdBytes` was a semantics-by-tier defect in the §9.9 mould (32-96 KB across tiers) —
promoted to a plain initializer with a D4 rationale comment. Found and fixed an unrelated bug in
passing: the report's "large method" flag column hardcoded `total > 64_000` instead of comparing
against the actual configured threshold. Confirmed independent of §6.3's separate
`RootSetCache.MaxFramesPerThread = 256` (different class, different purpose, already resolved
out-of-scope).

### 9.11 Array — **GREEN** ✅ IMPLEMENTED

[ArrayAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs) ·
[ArrayAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ArrayAnalysisOptions.cs)

**Shipped:** The sparse-array probe stacked three approximations and reported the result as exact
integers: one sample object per type, every 100th element of that one array (`SampleStride`), then
extrapolated back up to `NullOrZeroCount`/`WastedBytes` totals. All three deleted —
`SparseSampleLimit`/`SampleStride` gone, every qualifying array is now walked fully, and the
extrapolation math itself is gone (not just uncapped): `NullOrZeroCount` is the real count,
`WastedBytes` is `nullCount * elemSize` directly. `TopSparseLimit` was also a loop bound, not just a
row limit — probing stopped once 10 sparse arrays were found, so anything ranked 11th+ was never even
evaluated; deleted, moved to render along with `TopTypeLimit`/`TopLargeLimit`. Measured (M3): full
element walks on a 3.35GB/1.73M-array dump cost 42ms uncapped vs 39ms capped — negligible. Surfaced and
fixed F10 in passing: `TopSparseLimit` was used directly as a `List<T>` capacity, so naively uncapping
it by setting `int.MaxValue` threw `OutOfMemoryException` — moot now since the loop bound itself is
gone (lists seeded with a small fixed capacity instead). Deleted dead code found while touching the
file (`ReadLargeArraysFromIndex`, never called) and the now-permanently-false `ArrayDomainResult.ScanLimited` field (removed from the domain result, section builder, and `ConfidenceSectionBuilder`).
Deleted `ArrayUncappedRealDumpTests.cs` (its M3 measurement is preserved above, same reasoning as
§9.3's `ModuleAnalyzerUncappedRealDumpTests.cs` deletion).

---

### 9.12 String — **AMBER** ⚠️ PARTIALLY IMPLEMENTED, the first analyzer where cap removal is not sufficient

[StringAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs) ·
[StringAnalysisOptions.cs](../../src/DumpDetective.Core/Options/StringAnalysisOptions.cs)

**Shipped:** Sixteen knobs, the largest options surface in the codebase — deleted `MaxStringsToDedup`
(bounded how many strings get read at all, e.g. 50,000 out of a heap holding tens of millions),
`SamplingMode` (compounded with the above via a multiplier the user never saw — `Fast` composed to an
effective 2,500), `EnableDeduplication`, `DeduplicationMode`, `DeduplicationStringCountThreshold`
(inert by default), `DetectInterning` (hard-coded true), `TopDuplicatesToShow`/`PreviewMaxLength`
(moved to render). Also deleted `MaxDedupUnique`, a hardcoded Phase-1 satellite-index cap
(`const int = 500_000`, not even part of `StringAnalysisOptions`) that M5 found is the cap that
actually binds in production — the audited options only govern fallback branches that essentially
never execute once the Phase 1 index exists. Replaced the two-`PriorityQueue`-plus-merge top-K
selection with a single full sort now that nothing truncates. Found and deleted a second dead
parallel code path while touching the file (`else if (typeAggregates is null)` stats-only branch,
unreachable once `runDedup` is unconditionally true). Deleted now-permanently-false result fields
(`DeduplicationSkipped`/`DedupSkipReason`) and their report metrics, same "vestigial signal" pattern
as Boxing (§9.1) and Module (§9.3).

**Remaining (why still AMBER, not GREEN):** two genuine memory/allocation guards were kept, not
deleted — `MaxUniqueStringTracking` (bounds the fingerprint dictionary; ~10M unique strings would be
hundreds of MB, estimated 2-2.5GB by extrapolation but never directly measured — needs a larger dump
than the 3.35GB/321K-unique-string one available) and `MaxDuplicateStringLength` (a materialization
guard — without it, a single 100MB string becomes a 200MB managed allocation on read). Exact duplicate
detection over every string needs a genuinely different shape, not a bigger cap: a disk-backed,
hash-partitioned streaming fingerprint-and-count pass (imitating `ReverseEdgeExtractor`'s existing
pattern) instead of the current in-memory dictionary. That's a new subsystem, out of scope for this
pass. `ProduceRawExports` ("move to report options") was also deliberately deferred — `ReportOptions`
isn't wired into `AnalysisContext` today, and `WeakReferenceAnalyzer` has the identical unmigrated
pattern (§9.31), so fixing String alone would have created a cross-analyzer inconsistency at the time;
flagged as a cross-cutting follow-up rather than solved in this pass.

---

### 9.13 AsyncStateMachine — **GREEN** ✅ IMPLEMENTED

[AsyncStateMachineAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs) ·
[AsyncStateMachineAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AsyncStateMachineAnalysisOptions.cs)

**Shipped:** Deleted six of seven knobs. `TypeCandidateLimit` (200) capped `candidates` outright — the
domain result's own comment admitted a field named as a sum over "ALL candidate types" was actually
bounded. `TopTypeLimit` (20) gated which candidates got a full profile built at all — moved to render.
`HistogramTopTypeLimit`/`HistogramInstanceCapPerType` made `StateDistribution` a sample, not a
distribution (top 10 types x 1,000 instances each) — deleted, but the early-exit optimization behind
`HistogramInstanceCapPerType` was preserved rather than dropped: `histogramRemaining[mt]` is now seeded
from the type's *exact* `TypeAggregates` instance count instead of an arbitrary cap, so the second heap
pass still exits early once every type is exhausted, but the histogram itself is complete.
`SuspendedMethodMapLimit`/`TopCapturedSizeEntries` were pure post-build output slicing, moved to
render. `LargeCaptureThresholdBytes` kept as a semantic threshold. Deleted the now-permanently-false
`ScanLimited`/`SkippedTypeCount`/`SkippedBytesFraction` result fields and their `ConfidenceSectionBuilder`
limitation row, same vestigial-signal pattern as prior sections; `AsyncTaskDomainResult`'s separate
`TaskScanLimited` left untouched (unmigrated at the time). `StateDistribution`'s top-3-states display
truncation was left as-is — a display-shape decision, not a scan-completeness cap; every instance is
still counted into the histogram before the top-3 slice. Measured (M2): the second full-index scan
costs ~2s and is cap-independent (1,728ms vs 2,132ms on the reference dump, though that dump's small
state-machine population — 11 total — never actually exercised the per-instance cap; the interesting
high-volume case is estimated, not measured). Deleted `AsyncStateMachineUncappedRealDumpTests.cs` (its
M2 result is preserved above).

### 9.14-9.16 preamble: group 3 shares one root cause

StaticRootLeak, FinalizableObject, and GCRoot each approximated **retained size / retained set** with
their own node/depth-bounded BFS (`BoundedGraphWalk.CollectRetainedObjects`,
`FinalizableObjectAnalyzer.BfsEstimateRetained`, `BoundedGraphWalk.CollectForwardTypeNames`/
`ComputeExclusiveRetained`) — four separate implementations of the same approximation, all clamped by
a shared `AbsoluteMaxDepth = 20`. `DominatorTreeComputer` computes the exact retained size of every
node in the reachable graph in one pass (218s on a 25.6GB dump, §4), making all four estimators
pre-dominator-tree workarounds. **Group 3's AMBER verdict was blocked on the accessor not existing
yet, not on cost.** §10's `IDominatorTreeProvider`/`EnumerateRetainedSet` shipped and resolved the
blocker — §9.14 and §9.15 below now read the exact tree, deleting their BFS estimators entirely. §9.16
partially migrated: retained *bytes* now come from the tree, but the forward-path-type-names walk
deliberately stayed BFS-backed (see its notes) as a non-blocking residual.

---

### 9.14 StaticRootLeak — **GREEN** ✅ IMPLEMENTED

[StaticRootLeakDetector.cs](../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs) ·
[StaticRootLeakAnalysisOptions.cs](../../src/DumpDetective.Core/Options/StaticRootLeakAnalysisOptions.cs)

**Shipped:** `MaxRetainedObjectsToScan` (10,000) capped the `Dictionary` a static root's retained set
was materialized into — for a leaking static cache, potentially most of the heap; `SampleRetainedObjectsToInspect` (100) further sub-sampled that for the type breakdown — so severity ranking
saturated identically for any root retaining more than 10,000 objects. Both deleted, replaced by
`IDominatorTreeProvider` — the first production caller of `EnumerateRetainedSet`.
`AnalyzeStaticRoots` now has three paths per root: (1) shape pre-check (no reference fields — exact,
trivial), (2) tree-backed exact bytes + a streaming `EnumerateRetainedSet` pass over every retained
object (not a 100-object sample), (3) tree unavailable/root unreachable — degrades to direct-object-only,
with `ScanWasCapped` repurposed from "hit the numeric cap" to "not exact this run." `BoundedGraphWalk.CollectRetainedObjects` deleted outright (no remaining callers). `MaxRootsToReport`/
`TopRetainedTypesToReport` moved to render — the flat "top roots" table uses `STCompact`'s default
pagination; the per-root "top retained types" sub-tables (one table per root, not rows in one table)
kept a small render-layer constant (`MaxRootDetailTables = 8`) since D5's row-pagination argument
doesn't apply to that shape. **Residual, deliberately accepted:** `EnumerateRetainedSet`'s cost near
the dominator tree's root is unmeasured on a real dump — judged acceptable since it's a streaming walk
(memory-safe regardless of cost) replacing a heuristic that was already silently wrong, with a
same-run fallback path available if it proves too slow in practice.

### 9.15 FinalizableObject — **GREEN** ✅ IMPLEMENTED

[FinalizableObjectAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/FinalizableObjectAnalyzer.cs) ·
[FinalizableObjectAnalysisOptions.cs](../../src/DumpDetective.Core/Options/FinalizableObjectAnalysisOptions.cs)

**Shipped:** `MaxBfsNodes = 200`/`MaxBfsDepth = 10` were the most aggressive cap audited in this whole
pass — any finalizable object with a retained graph exceeding 200 nodes (trivially common for anything
holding a collection or stream buffer) reported a retained size unrelated to the real answer, not an
approximation of it. `BfsEstimateRetained` (a private fourth copy of the bounded-BFS pattern) deleted
entirely — per-entry retained bytes now come from `treeProvider.TryGetRetainedBytes`, falling back to
shallow `obj.Size` when the tree is unavailable (same degrade pattern as `GCHandleAnalyzer`). Added
`FinalizerQueueEntry.RetainedBytesIsExact` and a report "Exact?" column so degraded entries are
visible; `IsRetainedEstimatePartial` repurposed from "BFS hit its cap" to "some entries fell back to
shallow size." `QueueScanLimit` (500) deleted — the O(1) exact-bytes lookup makes scanning every
finalizer-queue entry cheap. `TopTypeLimit`/`TopQueueEntries` moved to render. Options class deleted
outright — no fields survived.

### 9.16 GCRoot — **GREEN** ✅ IMPLEMENTED

[GCRootAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs) ·
[GCRootAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCRootAnalysisOptions.cs)

**Shipped:** `PathSearchTopN` (25) bounded how many findings got root-path evidence at all;
`MaxBfsNodes`/`MaxBfsDepth` bounded each search — and the confidence-scoring code already downgraded
0.8→0.6 whenever a search truncated, so the analyzer was scoring its own output low-confidence by
construction on any non-trivial heap. Found a third instance of *configured value ≠ applied value*
(after String's §9.12 `SamplingMode` multiplier and Module's §9.3 `new()`/`Default` divergence): the
`Full` preset set `MaxBfsDepth = 30`, silently clamped to 20 by `BoundedGraphWalk.AbsoluteMaxDepth` —
readable nowhere, applied nowhere the config said. Measured (M4): computing a root path for every one
of 1,404 findings (still BFS-backed, not yet dominator-tree-rewired) took 568ms — faster than the
capped 25-candidate run's 874ms — so `PathSearchTopN` was deleted without waiting for the
dominator-tree rewire. `TopSeverityLimit` deleted but split into two concerns it had conflated: the
reported finding set (`TopRootsBySeverity`) is now the full list, paginated at render; the
owning-stack-frame-attribution enrichment (a genuinely costly per-thread frame walk, already flagged
in a source comment as "too costly to run for every Stack root") stayed bounded by a new private
`StackOwnerAttributionLimit = 20` constant — purely cosmetic, an unenriched row just loses descriptive
text, not any exactness-relevant data. `MaxBfsNodes`/`MaxBfsDepth` moved off the options surface to
private constants, matching `BoundedGraphWalk.AbsoluteMaxDepth`'s own precedent. Options class deleted
outright. **Residual, deliberately deferred:** the forward-path-type-names walk
(`BoundedGraphWalk.CollectForwardTypeNames`) stays BFS-backed rather than rewired to
`EnumerateRetainedSet`, even though retained bytes for the same findings already come from the exact
tree — that member's cost near the tree's root is unmeasured (§9.14 took on that risk for a different
consumer), so this walk keeps its fixed bounds rather than inheriting the open question.

### 9.17 Collection — **AMBER** ⚠️ PARTIALLY IMPLEMENTED

[CollectionAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs) ·
[CollectionAnalysisOptions.cs](../../src/DumpDetective.Core/Options/CollectionAnalysisOptions.cs)

**Shipped:** This was the only analyzer that read `AnalysisProfile` at runtime
(`options.Profile == AnalysisProfile.Fast`) — replaced with the provably equivalent
`options.PathAnalysisTopN <= 0`, since the deleted `Fast` preset always set that to 0. `Profile`
field, `Preset`/`Default` all deleted. `IncludeQueueAnalysis` hard-coded to always-true (both call
sites' `if (... && !IncludeQueueAnalysis) return;` guards removed). The nested `ReferenceChainOptions`
turned out to be entirely dead — grepped every read site and found the analyzer's actual
`RootPathFinder` search is configured from the **top-level** `context.AnalysisOptions.ReferenceChain`
(shared with `ReferenceChainAnalyzer` itself), never from the embedded copy, which was only ever
written by config merge and never read — deleted outright, same "confirmed no consumer" bar as §9.6.
`WasteThresholdBytes`/`SurfaceProbingExceptions`/`MaxDegreeOfParallelism`/`SerializeHeapAccess` kept as
audited (semantic thresholds / orthogonal execution policy), just no longer tier-varying.

**Remaining (why still AMBER):** `TopWastefulCollectionsToShow` and `PathAnalysisTopN` were
recategorized from "move to render"/"delete" to "keep, real work-scoping thresholds" — a judgment call
overriding the original audit, confirmed via code: `TopWastefulCollectionsToShow` sizes a bounded
top-K accumulator *during* the streaming per-segment scan (the CLAUDE.md-mandated streaming pattern —
the scan cannot retain every wasteful collection found across a 25GB heap in memory), and
`PathAnalysisTopN` bounds how many of those top items get an expensive per-item `RootPathFinder`
graph search — the same "bounds real work, not display rows" shape as ReferenceChain's own `TopCount`
(§9.20). Collection's root-path descriptions also still inherit §9.20's residual AMBER limitation
(`LargeFanoutThreshold`/`MaxCandidateNodes`/`MaxRootExpansionDepth` kept as real search-layer caps),
now via the correct top-level options instance.

---

### 9.18 Dominator — **GREEN** ✅ IMPLEMENTED (mostly nothing to do)

[DominatorAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs) ·
[RetentionOptions.cs](../../src/DumpDetective.Core/Options/RetentionOptions.cs)

**Shipped:** Already computed exact retained sizes for the whole reachable graph via the dominator
tree — most of the options surface was genuinely already done, and net deletions ended up being one
confirmed-dead field, not the seven the original audit table implied. Two rows had already been
resolved by earlier work before this pass started: `ExactDominatorTreeMemoryBudgetBytes` was already
deleted (replaced by two root-cause fixes — isolated try/catch, overflow-safe `ChunkedBuffer<T>.Add` —
instead of a heuristic byte-cost model), and `MaxLeakScanObjects`'s "2.3% of an 87M-object heap"
concern no longer applies to the primary path since `BuildLeakSignalsFromReverseIndex` (the default
whenever a reverse index exists — effectively always) is exhaustive by construction; the field now
only bounds the rare no-index fallback and a separate BFS-breadth use, both legitimate. Deleted the
one confirmed-dead knob, `TopFinalizerTypesToShow` (zero references anywhere). Recategorized two
groups from "delete" to "keep, real threshold" after finding the original audit's reasoning was
inconsistently applied within a single struct: `MaxRootPathCandidateNodes`/`CandidateDepth`/
`ExpansionDepth` join `RootPathLargeFanoutThreshold` (all four populate the same `RootPathSearchLimits`
struct for the same purely-decorative evidence-text search — the reported retained-byte numbers come
from the exact tree independently, so an un-found display path only costs a confidence downgrade, not
a wrong number) — deleting three of four fields and leaving the struct half-bounded would have been
the actual defect. `TopHighlyReferencedObjectsToShow` similarly recategorized — it sizes an in-scan
top-K `PriorityQueue` and gates expensive per-item BFS/path-search work, not a display truncation —
the third instance of this exact audit blind spot found this session (after Collection §9.17,
ReferenceChain §9.20). `Preset`/`Default` deleted; all surviving fields collapsed to single plain-field
Balanced-tier defaults.

---

### 9.19 EventLeak — **AMBER** ⚠️ PARTIALLY IMPLEMENTED

[EventLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs) ·
[EventLeakOptions.cs](../../src/DumpDetective.Core/Options/EventLeakOptions.cs)

**Shipped:** Sixteen knobs, eight of them severity-scoring weights kept as Category 5. `MaxGroupsToEnrich`
(25) deleted — genuinely resolvable here, unlike most "delete with the dominator move" items, because
this analyzer already had the alternative safety mechanism the whole plan wants everywhere:
`MaxEvidenceEnrichmentMs`, a 2000ms **wall-clock budget** for the entire enrichment loop — the only
time-based budget anywhere in the codebase's options surface, kept as the precedent/pattern to
promote elsewhere. With the group-count pre-filter gone, every leak instance is enrichment-eligible in
severity-descending order, with the time budget as sole safety valve. `IncludeNonLeakingEvents`
hard-coded true; `MinSubscribers` deleted alongside it rather than kept as originally audited — grep
showed it had exactly one behavior, gating on `!includeNonLeaking`, which the hard-code makes
permanently unreachable. `TopSubscriberTypesToShow`/`EnableDiagnostics` deleted as confirmed-dead (V4).
`TopDetailedInstancesPerGroup` recategorized kept (sizes a genuine in-scan top-K accumulator) — the
fourth instance of the "row-limit that's actually work-scoping" audit blind spot this session.
`LifetimeMismatchProbeLimit` turned out to bound two operations with very different cost profiles: the
generation-check half is O(1) regardless of scale and was uncapped cleanly; the
`EnableLowIncomingRefsCheck`-gated half (`CountIncomingRefs`) turned out **not just slow but wrong** —
it samples the first ~500 objects in arbitrary enumeration order and checks each for a reference to
the target, essentially never finding the real referrer on an 87M-object heap. Fixing it properly
needs `IBackwardReferenceProvider.TryGetParents` wired into `EventLeakFastScanner`, which currently has
no cache/provider reference — judged out of scope for this pass; **left broken but documented in the
options class XML doc** rather than silently left for rediscovery. Found and deleted a second
confirmed-dead CLI-only knob, `--event-leak-min-subscribers` (zero consumers past the CLI parser),
same bar as §9.20's discoveries.

**Remaining (why still AMBER):** `EnableLowIncomingRefsCheck`'s underlying `CountIncomingRefs`
correctness bug above — kept as an opt-in toggle, default unchanged, not hard-coded true, specifically
because fixing it properly is deferred, not because the toggle itself is fine as-is.

---

### 9.20 ReferenceChain — **AMBER** ⚠️ PARTIALLY IMPLEMENTED (was RED, no longer RED)

[ReferenceChainAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs) ·
[ReferenceChainOptions.cs](../../src/DumpDetective.Core/Options/ReferenceChainOptions.cs)

**Was RED for two independent reasons:** (1) `ReferenceChainSearchMode { Fast, Balanced, Deep }` was a
second, parallel profile system duplicating `AnalysisProfile`'s tiers, with a three-layer
value-resolution chain (`preset → explicit-or-zero → mode default`) that made the applied value
unreadable — a fourth instance of *configured value ≠ applied value*. (2) "Exact" here means the true
shortest reference chain, which requires a complete reverse graph — `MaxParentsPerChild` (§6.2) meant
the graph itself was incomplete for high-fanout objects, on top of `LargeFanoutThreshold` pruning the
same objects again at the analyzer layer.

**Shipped:** Reason #1 fully resolved — `ReferenceChainSearchMode` enum, `Preset`/`Default`, and the
three `Resolved*` mode-dependent properties deleted outright; the codebase always ran through
`IndexBackedBidirectionalSearch` when a reverse-edge index exists anyway (effectively always since
Stage A), so the mode enum never actually selected between two live implementations — it only varied
numeric budgets by tier. `SkipArrays` deleted per V3 (confirmed real traversal pruning, not
presentation) — arrays are never treated as noise now, made uniform across what was previously
inconsistent preset behavior. Found and deleted a newly-discovered, entirely dead knob not in the
original audit table: `MaxPathDepth` and two `ExecutionPolicy` sibling fields were threaded from CLI
through to an `AnalyzeTopTypes`/`TryFindAnyRootPath` parameter that was never actually read anywhere —
deleted all three fields, the unused parameter threading, and their two CLI-only flags.

**Remaining (why still AMBER, not GREEN):** Reason #2's specific blocker (§6.2's `MaxParentsPerChild`)
is resolved — the reverse-edge index itself has no fan-in cap (measured worst-case hub fan-in 10.76M on
a 25.6GB dump) — but a related, separate cap survives at the *search* layer, not the index layer:
`LargeFanoutThreshold`/`MaxCandidateNodes`/`MaxRootExpansionDepth` still stop
`IndexBackedBidirectionalSearch`'s neighbor generators from expanding past a hub, even though the
index could answer for all of them. Recategorized from "delete with the strategy collapse" to "keep,
real search-budget thresholds" — a 10.76M-neighbor single BFS step is a fundamentally different cost
profile than the index's one-time linear build, for a query that runs a handful of times per analysis.
So "the true shortest reference chain" still isn't guaranteed — a shorter path could exist through a
fanout-pruned hub or beyond the node/depth budget. `TopCount` was also recategorized kept rather than
moved to render — it bounds how many top-by-size types get an expensive bidirectional search run at
all, not a display truncation of an already-complete computation; `FallbackTopCount` (its "0 means
fallback" sentinel companion) deleted as dead once `TopCount` became a plain non-zero default.
`KnownLeakTypePatterns` kept as audited.

---

### 9.21 TimerLeak — **GREEN** ✅ IMPLEMENTED, no options class

[TimerLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs)

**Shipped:** Has no options class and no `AnalysisOptions` entry — the cleanest demonstration that
this refactor isn't only about the options surface: an analyzer with zero configuration was still not
exact, purely from shared-traversal bounds below the options layer (`RootPathFinder`'s now-resolved
§6.2/§6.3 defaults). Used as the canary for that. Found a genuine, unrelated defect while auditing:
`TimerLeakAnalyzer` declared `ITypedResourceInstanceSampler<TimerStateSnapshot>`
(`MaxStateSamplesPerType`/`TopSampleCap`) to satisfy the same interface contract as the DbConnection/
WcfChannel/HttpObject quartet (§9.32-9.34), but never actually wired into the real sampling mechanism
— `PopulateEvidence` fetched one address per type directly instead, so both properties were dead.
Tracing it further found the evidence sample's `HeapEntry` was fabricated with a bogus `Generation =
-1` sentinel, and that `TimerStateSnapshot`'s period/callback-owner/generation data was computed but
**never consumed anywhere** in the report — dead computation end-to-end. Fixed all three: deleted the
unused interface properties, resolved generation for real via `GenerationTagResolver.Resolve`, and
wired `Samples` into `TimerLeakSectionBuilder`'s report table (three new columns) as additive detail
rather than just deleting the dead branch.

---

> **9.22 removed** — was a duplicate audit of the same analyzer covered in §9.18 (Dominator). See the
> roster correction near the top of this document.

### 9.23 Thread — **GREEN** ✅ IMPLEMENTED

[ThreadAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ThreadAnalysisOptions.cs)

**Shipped:** Ten knobs plus an enum plus a third tier system — `AdaptForSize(options, DumpSizeTier)`
divided `MaxThreadsToCaptureSnapshots`/`MaxSampledStackSnapshots` by 4/2/1 based on dump size,
invisibly (fifth instance of *configured value ≠ applied value*), and turned out to be double-applied:
`ThreadAnalyzer.ComputeSamplerCapacity` independently re-applied the same divisor to the
already-divided value, so a large-dump Balanced default of 20 became 1 (16x reduction, not the
intended 4x). Resolved by deleting `AdaptForSize` outright — it was never a memory guard, and every
count/total this analyzer reports already enumerated every thread regardless. `MaxFramesForThreadScan`
(8 frames, four at Fast — compare Jit's 200 and `RootSetCache`'s 256, three different frame budgets in
three layers) and `MaxStackRootsToCount` deleted; every alive thread's whole captured stack now feeds
wait-pattern/hotspot/async-chain detection, via a named `UnboundedFrameCount = 100_000` sentinel
(measured at 2ms end-to-end, M8) rather than `int.MaxValue` (which would repeat F10's `List<T>`
capacity `OutOfMemoryException` bug). `AsyncChainDetectionMode` deleted per D9 — runs unconditionally
now. `MaxThreadsToCaptureSnapshots`/`MaxTopHotspots` deletion made the locked/blocked/exception/hotspot
lists complete. The reservoir-sampled "Sampled threads" feature was redesigned, not just uncapped: per
D3's later per-consumer nuance, chose to make it a complete deterministic population (every alive
thread not already captured elsewhere, no RNG) rather than a small illustrative sample — renamed
`SampledThreads`→`OtherThreads` and migrated it onto `STCompact` (was one `NamedStackTrace` block per
thread, which would have flooded the report once uncapped). `ComputeSamplerCapacity`/
`ReservoirSampler<T>`/`SampleCandidateIndices` deleted outright, along with the dump-path-hash seed
derivation step that fed `SamplingSeed`. Largest test fallout of any row so far — deleted six
preset/sampling-specific test files (`ThreadAnalysisOptionsTests`, `AdaptivePresetTests`,
`ThreadAnalyzerSamplerCapacityTests`, `PresetBehaviorTests`, `ThreadAnalyzerSamplingTests`,
`ReservoirSamplerTests`) plus `ThreadUncappedRealDumpTests.cs` (M8's measurement preserved above).

---

### 9.24 ThreadStackCluster — **GREEN** ✅ IMPLEMENTED

[ThreadStackClusterAnalysisOptions.cs](../../src/DumpDetective.Core/Options/ThreadStackClusterAnalysisOptions.cs)

**Shipped:** `MaxFramesPerSignature` (6 frames, 4 at Fast) was a lossy clustering hash, not a list
truncation — two threads sharing their first 6 frames and then diverging into completely different
work were merged into one cluster, understating diversity on exactly the deadlock/thread-pool-starvation
dumps where "how many distinct things are these threads doing" is the headline question. Deleted;
cluster identity is now the thread's whole captured stack (free once §9.23's unbounded frame capture
landed — only `BuildSignature`'s internal truncation break needed to come out).
`MaxClusters` deleted outright (unbounded dictionary/derived arrays). `MaxThreadIdsPerCluster`/
`TopSignaturesToShow`/`TopClustersToShow` moved to render, but to the pre-existing `StackClusters`
per-cluster-card typed slot rather than `STCompact` — a legitimate specialized display, not the
ad-hoc `.Take()` pattern D5 targets — so these kept ordinary section-builder-local constants instead
of `STCompact`'s uniform pagination. Found and fixed a dead-code `Truncated` flag in passing — it was
hardcoded `false` because the analyzer used to cap the sample list before the section builder ever saw
it; now computed for real at render time. `SampleOsThreadIds`/`SampleManagedThreadIds` deliberately
kept their "Sample" name despite now being complete lists, since they're written verbatim into
external JSON/NDJSON export artifacts (a schema, not internal-only). `ProduceClusterExports`'s
"move to report options" deliberately not executed — grouped with String/WeakReference's identical
unmigrated `ProduceRawExports` pattern (§9.12) as one deferred cross-cutting change.

---

### 9.25 Hang — **GREEN** ✅ IMPLEMENTED

[HangAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HangAnalysisOptions.cs)

**Shipped:** `MaxTasksToScan` (50,000) was a genuine Q7 defect, not a display cap — both scan paths
always counted `TotalTasks` exactly but only read each Task's `m_stateFlags` field (feeding
`PendingTasks`/`FaultedTasks`/`CanceledTasks`) while under the cap, so those three state-bucket counts
silently undercounted past 50,000 — same "cap corrupts a total" shape as Boxing's `TotalBoxedObjects`.
Deleted; costs one extra field read per Task object, not a new asymptotic class. `TopWaitingThreadsPerGroup`
was already confirmed dead by V4, but this pass found *why it looked plausible*: a hardcoded `.Take(10)`
in `Analyze()` was doing the real truncation the option only pretended to control — deleted both, moved
`TopContinuationTypesToShow` to render alongside it. `LongWaitThreshold`'s apparent tier variance (5 vs
8 vs 3 seconds) turned out to be inert — never read by `HangAnalyzer.cs` — deleted. `HighThreadPoolThreshold`
is the sole surviving option, kept at its Balanced value (100) per D4's "shakiest flag, defer
recalibration to field data" reasoning. The `TaskScanLimited` flag and its `queuedWorkItems > 1000`
early-exit died with the cap (distinct from `AsyncTaskDomainResult.TaskScanLimited`, §9.29, untouched).

---

### 9.26 Crash — **GREEN** ✅ IMPLEMENTED (7 of 8 deleted, not "options class deleted outright")

[CrashAnalysisOptions.cs](../../src/DumpDetective.Core/Options/CrashAnalysisOptions.cs)

**Shipped:** All eight knobs were Category 1 payload/presentation limits. `IncludeAllTypesInPayload`
already documented and defaulted to exactly the design this whole plan proposes ("include full data in
the domain result, let the renderer display top-N") — used as the reference implementation for the
§10 render-layer mechanism (D5), settling empirically that the split works and the renderer is the
right owner. `TopCrashThreadCandidates`/`TopDetailedExceptionInstances` deleted, moved to render.
`MaxDetailedExceptionsPerType` turned out to be dead code (zero reads, only config round-trip) —
deleted. `MaxOriginalStackFramesToPrint`/`MaxCurrentThreadFramesToPrint` were truncating data already
fully captured at extraction time — removed the post-hoc slice; one exception,
`BuildActiveExceptionLookup`'s live stack walk, did bound real work and was uncapped too, matching
§9.23 Thread's precedent. **Correction to the original one-line "all Category 1" verdict:**
`MaxExceptionsPerType` specifically gates genuinely expensive per-instance work (inner-exception-chain
walk + stack-trace parsing), while every reported total is computed unconditionally elsewhere in the
scan, untouched by the cap — the same evidence-decoration-cap shape D3 later decided to keep for
TimerLeak/StaticRootLeak/EventLeak/Collection/Dominator. Kept as a fixed internal constant (10), not
tier-varied, documented in an XML comment. Deleted the now-unnecessary `CrashAnalysisOptionsModel`
config-binding wrapper, executing §8 item 5's Crash half early.

---

### 9.27 Memory — **GREEN** ✅ IMPLEMENTED (cross-reference's "collapse to one table" executed, with a correction)

[MemoryAnalysisOptions.cs](../../src/DumpDetective.Core/Options/MemoryAnalysisOptions.cs)

**Shipped:** `TopTypesBySizeWeight`/`CountWeight`/`LohWeight`/`AverageSizeWeight` drove a weighted
quota-merge selection that the tier silently re-tuned (Fast 45/40/10/5 vs Full 35/30/20/15) — Full was
not a superset of Fast, a type surfaced at one tier could be entirely absent at the other. **Stronger
resolution than the original "keep, fix one value" verdict**: since the type list is now complete
(every distinct type, sorted by bytes), there's no more "which N types get shown" judgment call for
the weights to bias, so the entire quota-merge mechanism was deleted, not just de-tiered.
**Correction found before executing the cross-reference's "one raw table" idea:** `TopTypesCount` also
gated a real per-type bounded BFS (`RetainedSizeCandidateSelector.SelectAndCompute`), not just report
width — naively deleting it would have run that BFS for every distinct type on a 25GB heap. Split the
two concerns: the type *list* is now genuinely complete and exact, while the expensive retained-size
*enrichment* stays scoped to a fixed internal `TypesToWalkForRetainedSize = 20` constant (not
user-configurable — a pure wall-clock-cost knob with no semantic meaning). `LohThresholdBytes` kept —
confirmed purely a display echo of a hardcoded `85_000` constant elsewhere, never tier-varied in the
first place, no exactness defect.

---

### 9.28 HeapTopology — **GREEN** ✅ IMPLEMENTED, one line, large effect

[HeapTopologyAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HeapTopologyAnalysisOptions.cs)

**Shipped:** One knob, `CountSohObjects` — the default configuration (Fast/Balanced) didn't count the
small object heap at all, the bulk of objects on nearly every dump; only Full did, via a live
`segment.EnumerateObjects()` walk measured at 10.2 extra seconds on a 3.35GB dump (M6). Rather than
"set to true permanently," shipped a genuinely free exact alternative: derive
`SohObjects = TotalObjectCount − LohCount − PohCount − FrozenCount` from Phase 1's already-exact total
and this analyzer's own already-cheap LOH/POH/Frozen walks — zero additional heap traversal. Options
class deleted outright, same wiring shape as ObjectShape (§9.2). Per-segment/per-logical-heap SOH
breakdowns still show `N/A` (arithmetic can't recover a per-segment split) — only the headline total
became exact.

### 9.29 AsyncTask — **GREEN**

[AsyncTaskAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AsyncTaskAnalysisOptions.cs)

**Shipped:** `MaxTasksToScan`/`MaxTcsToScan`/`MaxVtsToScan` (50K/20K/20K) were three independent scan
caps on an 87M-object heap — deleted; the Tasks index (`TaskIndex.bin`) is Phase-1-built unconditionally
regardless (M7: uncapped vs capped delta was 311ms against a 4.4s baseline). `MaxContinuationDepth`
(20) deleted, leaving the pre-existing `MaxContinuationNodesToVisitPerTask = 2_000` node budget as the
sole traversal bound — no dominator tree exists for a task's own continuation graph to re-point at, so
Category 4 guidance reduced to "delete the redundant depth cap, trust the existing node budget." Four
`Top*ToShow` knobs moved to render — **first pass got this wrong**, reintroducing exactly the
render-layer-cap pattern D5's amendment (post-§9.7) already corrected (added local
`TopTypesToShow`/`TopSnapshotsToShow` consts with `.Take()` plus a manual "(showing N of M)" suffix);
corrected to emit the complete population for all eight `Top*` lists with no `rowLimit` anywhere,
falling through to `STCompact`'s uniform default. Options class deleted outright.
`TaskScanLimited`/`TcsScanLimited`/`VtsScanLimited` became permanently-false and were deleted. Did not
pursue the cross-referenced "collapse 8 Top* lists into one raw table" restructuring — a genuine design
question requiring the lead-finding-dedup doc's fuller context, flagged as a deliberate follow-up.

---

### 9.30 AllocationPattern — **GREEN** ✅ IMPLEMENTED (was AMBER, resolved by D7)

[AllocationPatternAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AllocationPatternAnalysisOptions.cs)

**Was AMBER because the tier selected the algorithm, not the effort:** three enums
(`SelectionMode`/`ScanStrategy`/`SelectionPriority`) varied by preset, and `ScanStrategy.FullScan`
(exact) was reachable only at the Full tier.

**Shipped:** All three enums deleted entirely, resolved individually per D7 rather than mechanically —
`SelectionPriority.LongLivedFirst`'s single-pass sequential bucket-fill turned out to be
scan-order-dependent and could silently drop a bucket's true top-N member, so `ClassificationFirst`
(the only correct one of the two) was kept and the other, plus the never-used `Mixed`, deleted.
`SelectionMode` turned out to be a display-ranking choice, not an algorithm choice, once the scan cap
was gone — kept `CompositeScore` only. **Addendum the original audit missed:** `FullScan`'s `scanLimit`
was still bounded by `MaxScanItemsAbsolute` (10-20K), so `FullScan` alone wasn't actually exact at 25GB
scale — deleted that too, along with the `TopTypeLimit x ScanMultiplier` compounding (sixth instance of
*configured ≠ applied*) and a full O(N log N) pre-sort that existed only to establish the now-gone
scan-limit prefix — net effect cheaper than the capped version, not just more correct. Found and
deleted two more dead items while implementing: `EmitTransient`/`EmitShortish`/`EmitLongLived` (pure
display-suppression flags that ran after the list was already built, never saving work) and
`LohThresholdBytes` (never actually read by its own analyzer, unlike the same-named properties on
Memory/String). The four classification thresholds survive as a 7-constant POCO, kept because they were
identical across all three presets (pure duplication, not tier variance).

---

### 9.31 WeakReference — **GREEN** ✅ IMPLEMENTED

[WeakReferenceAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/WeakReferenceAnalyzer.cs) ·
[WeakReferenceAnalysisOptions.cs](../../src/DumpDetective.Core/Options/WeakReferenceAnalysisOptions.cs)

**Shipped:** `HandleScanCap` (50,000) truncated the handle table itself, not a derived list — deleted;
the live-mode fallback reader now gets `int.MaxValue`, matching the pattern `GCHandleAnalyzer` (§9.5)
already used. `WeakRefProbeSampleLimit` was a genuine Category-2 sample (probes distinct
`WeakReference<T>`-shaped MethodTables, an O(distinct-types) operation the cap was never meaningfully
buying) — deleted. `AbsoluteDeadCountThreshold` deleted per V4 (confirmed dead; the finding generator
has its own independent hardcoded copy, never connected to this one). `TopTypeLimit` deleted, moved to
render — but `WeakReferenceSectionBuilder` needed a real fix, not a pass-through: it had its own local
`TopTypesToShow = 15` const with `.Take()` before every `STCompact` call, the same D5-violating pattern
§9.29's first pass introduced fresh — deleted here too. `ScanCapped`/`ScanCapUsed` deleted as
permanently-false vestiges. `ProduceRawExports` left in place, unmoved — matches §9.24 ThreadStackCluster's
identical deferred-cross-cutting precedent for the same D6 decision.

---

### 9.32-9.34 preamble: the typed-resource quartet — ✅ IMPLEMENTED

DbConnection, WcfChannel, HttpObject (and already-audited TimerLeak, §9.21) share infrastructure that
never surfaced from the options-folder walk, because none of the four have an `AnalysisOptions` class
at all — every bound is a `private const int` inside the analyzer, never preset-varied, so the
three-tier system never touched this quartet. `InstanceStateSampler<T>`'s `MaxStateSamplesPerType`
(500, a **per-type** cap — a service with 600 open `SqlConnection`s reported only 500's state) and
`TopSampleCap` (Category 1 detail-table limit) were the caps to resolve here, needing a new mechanism
rather than a config change since the bound was compiled in.

**Shipped:** `MaxStateSamplesPerType` deleted outright per D10 — M9's real-dump measurement (503 DB
connections, 1,210 WCF channels, 102 HTTP objects, all well under 500-per-type, 1-109ms elapsed)
confirmed deleting it costs nothing measurable. `InstanceStateSampler<TSnapshot>` collapsed to a plain
unbounded accumulator (`TryReserveSample`/`_capped`/the two-arg constructor all deleted); the interface
properties themselves were removed, not left unused. `Top*` detail lists (open connections, faulted
channels, HttpClients) now hold the complete matching population. Found and fixed a second,
independent cap while implementing: `DbConnectionAnalyzer.BuildTopPools` had its own hardcoded
`.Take(10)` that only became visible once its input stopped being pre-truncated. `ScanCapped`/
`StateScanCapped`/`InstanceScanCapped` deleted from all three domain results as permanently-false
vestiges. Left inline-prose truncations alone per D5's carve-out (comma-separated name lists embedded
in a sentence, not a hidden-data concern). Found and flagged (not fixed, out of scope) two fully
orphaned models — `SqlTransactionDomainResult`/`SqlCommandDomainResult` — produced by no registered
analyzer anywhere in `src`.

### 9.32 DbConnection — **GREEN** ✅ IMPLEMENTED — [DbConnectionAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/DbConnectionAnalyzer.cs)

Per the quartet preamble: `MaxStateSamples`/`TopOpenCap` constants deleted, no options class existed.

### 9.33 WcfChannel — **GREEN** ✅ IMPLEMENTED — [WcfChannelAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/WcfChannelAnalyzer.cs)

Same shape as DbConnection. `MaxStateSamples`/`TopFaultedCap` deleted — this is the quartet member
where the cap mattered most: a channel-faulting storm past 500 instances of one type previously
reported an incomplete `Opening/Opened/Closing/Closed/Faulted` breakdown for exactly that type.

### 9.34 HttpObject — **GREEN** ✅ IMPLEMENTED — [HttpObjectAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/HttpObjectAnalyzer.cs)

Same shape as DbConnection/WcfChannel. Lower-stakes than the other two: per-type counts
(`HttpClientCount`, etc.) were already exact via `TypeAggregates` pre-seeding — only the drill-down
detail table (`TopHttpClients`) was capped and is now complete.

---

### 9.35 LeakCandidate — **GREEN** ✅ IMPLEMENTED, no options class, and not gated on §6.2

[LeakCandidateAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LeakCandidateAnalyzer.cs)

**Shipped:** No options class, main pass already O(distinct types) and exact — the only limit was a
post-hoc `Math.Min(30, candidates.Count)` slice, deleted; `TopCandidates` is now the complete ranked
population. Found a second, redundant cap while implementing: `LeakAnalysisSectionBuilder` had its own
local `TopCandidateCount = 30` const with a `.Take()` stacked on the already-capped domain result — the
same double-truncation shape as Boxing (§9.1); deleted. **`LeakCandidateCards` is the one deliberate
exception, not a D5 violation:** unlike `STCompact`, the card-rendering path has no client-side
pagination — every card renders into the DOM directly — so feeding it the full uncapped list would
have silently overwhelmed a UI widget never designed for hundreds of rich cards. Added a
section-builder-local `MaxLeakCandidateCards = 30` specifically for the card loop; the `STCompact` table
above it still shows the complete, exact population.

**This closes out §9 — all 33 registered analyzers have now been through this pass** (29 GREEN, 4
deliberately-deferred AMBER: §9.12 String, §9.17 Collection, §9.19 EventLeak, §9.20 ReferenceChain).
§8's residual profile-only cleanup is the only work item this plan described that hadn't been executed
by the time §9 closed out.

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
(§6 "retained-bytes consumers", §7 "root-attribution"). The problem this section originally scoped:
`DominatorAnalyzer` built the exact tree as method-local state, extracted one narrow per-type
dictionary for its own report display, and let everything else (idom array, retained-bytes array,
child CSR) become GC-eligible the moment it returned — there was no per-*object* query surface for any
other analyzer to reach the tree, exact or otherwise.

The original design proposed here was an **in-memory** `IHeapAnalysisCache`-cached provider
(`IDominatorRetentionProvider`), copying the existing reverse-index-provider pattern
(`TryGetReverseIndexProvider`) so whichever analyzer asked first would trigger the build — sidestepping
the module-ordering problem (GCRoot, order 140, runs before Dominator, order 220) without requiring a
reorder or `IDeferredAnalyzer`. **That in-memory shape was never shipped** — it had two real problems
(implicit analyzer-ordering coupling, and ~1.5-3GB held resident for the rest of the run). What
actually shipped instead is a **disk-backed index built during Phase 1's index-build job**, extending
D7 rather than inventing a new in-memory structure — resolving the same ordering and lifetime concerns
this section raised (concurrent residency, the address→id map, explicit fallback semantics) at the
Phase 1 layer instead. See the phase1-integration doc for the actual implementation.

**Consumers that migrated once it landed:** GCRoot (§9.16, retained bytes only — the forward-path-type-names
walk deliberately stayed BFS-backed), StaticRootLeak (§9.14), `RetainedSizeCandidateSelector` (§9.14-16
preamble), FinalizableObject (§9.15, its private 4th BFS copy deleted entirely).

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

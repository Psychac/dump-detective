# Exact analysis: removing scan caps, sampling, and the AnalysisProfile system

Status: **audit complete** — 33 of 33 registered analyzers (**26 GREEN, 6 AMBER, 1 RED**).
**Start at §11, the pre-implementation checklist.** Nothing should be implemented before B1-B4 are
resolved and D1-D9 are decided.

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

**Action:** every Category 5 threshold keeps its Balanced value as its single constant. Reviewers
should sanity-check each one on its merits rather than inheriting "Balanced was the middle option."

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
| 1 | Boxing | aggregator | **GREEN** | 4 of 5 | §9.1 — cap bites today; deleting it also deletes a determinism workaround |
| 2 | GCGeneration | aggregator | **GREEN** | 2 of 5 | §9.4 — pure output slicing; 3 thresholds survive |
| 3 | GCHandle | aggregator | **GREEN** | 1 of 5 | §9.5 — pure output slicing; 4 thresholds survive |
| 4 | *(GCHandle, cont'd)* | — | — | — | `DependentHandleAnalysisOptions` is **not a registered analyzer** — orphaned options class, folded into §9.6 below rather than counted as its own row |
| 5 | LockGraph | aggregator | **GREEN** | 1 of 1 | §9.7 — options class deleted outright |
| 6 | LohFragmentation | aggregator | **GREEN** | 2 of 2 | §9.8 — one cap applies during collection, truncating a type aggregation |
| 7 | ObjectShape | aggregator | **GREEN** | 2 of 2 | §9.2 — cap of 200 types corrupts three whole-heap aggregates |
| 8 | SegmentReservation | aggregator | **GREEN** | 0 of 2 | §9.9 — already exact; preset varies *semantics*, so it must still die |
| 9 | Module | aggregator | **GREEN** | 8 of 11 | §9.3 — 2 knobs are dead code; one deletion cascades into 4 more |
| 10 | Jit | aggregator | **GREEN** | 3 of 4 | §9.10 — one cap corrupts **six** accumulators; worst Q7 so far |
| 11 | Array | sampling | **GREEN** | 5 of 6 | §9.11 — `WastedBytes` is an extrapolation of an extrapolation of one sample object |
| 12 | String | sampling | **AMBER** | ~8 of 16 | §9.12 — exact dedup needs a restructured hash-count pass, not just cap removal |
| 13 | AsyncStateMachine | sampling | **GREEN** | 5-6 of 7 | §9.13 — a domain-result comment already documents its own corrupted sum |
| 14 | StaticRootLeak | retained-size | **AMBER** | 4 of 6 | §9.14 — `MaxRetainedObjectsToScan` materializes a Dictionary; needs dominator tree |
| 15 | FinalizableObject | retained-size | **AMBER** | 5 of 5 | §9.15 — a **fourth** private copy of bounded-BFS retained estimation |
| 16 | GCRoot | retained-size | **AMBER** | 4 of 4 | §9.16 — `MaxBfsDepth = 30` at Full is silently clamped to 20 |
| 17 | Collection | retained-size | **AMBER** | 4 of 9 | §9.17 — the only analyzer reading `AnalysisProfile` at runtime |
| 18 | Dominator | retained-size | **GREEN** | 0 | §9.18 — already exact; owns `RetentionOptions` exclusively (see row 22 note); its own flags were *deliberately* kept off the profile system |
| 19 | EventLeak | root-path | **AMBER** | 5 of 16 | §9.19 — holds the codebase's **only wall-clock budget**; useful precedent |
| 20 | ReferenceChain | root-path | **RED** | 6 of 10 | §9.20 — a **second, parallel profile enum**; Q6-gated on `MaxParentsPerChild` |
| 21 | TimerLeak | root-path | **AMBER** | 0 (no options class) | §9.21 — no knobs of its own; inherits every shared traversal bound |
| 22 | *(= Dominator, row 18)* | — | — | — | "Retention" is **not a separate analyzer** — `RetentionOptions` belongs to `DominatorAnalyzer`. Originally audited as a second, duplicate row; findings (`RootPathLargeFanoutThreshold` exclusion, `MaxLeakScanObjects` vs. 87M-object heap, etc.) merged into §9.18. |
| 23 | Thread | non-heap | **AMBER** | 8 of 10 | §9.23 — a **third** tier system (`AdaptForSize`); scans 8 frames per thread |
| 24 | ThreadStackCluster | non-heap | **GREEN** | 6 of 7 | §9.24 — 6-frame signatures merge genuinely different stacks |
| 25 | Hang | non-heap | **GREEN** | 3 of 5 | §9.25 — 2 more semantics-by-tier thresholds |
| 26 | Crash | non-heap | **GREEN** | 8 of 8 | §9.26 — **already implements the §10 render-layer pattern**; use as reference |
| 27 | Memory | non-heap | **GREEN** | 1 of 6 | §9.27 — tier changes the *ranking function*, not just the row count |
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

### 9.1 Boxing — **GREEN**

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

### 9.2 ObjectShape — **GREEN**

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

---

### 9.3 Module — **GREEN** (with the largest cascade so far)

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

**Q5 — cost.** Full `EnumerateTypeDefToMethodTableMap()` across all modules in all domains. This is
the one aggregator where unbounding does real extra work — the Full preset's comment
([:92](../../src/DumpDetective.Core/Options/ModuleAnalysisOptions.cs#L92)) warns *"the analysis time
grows quickly with the number of modules."* Still metadata-table iteration, not heap work; expect
seconds to low tens of seconds. **Measure this one rather than assuming.**

**Q6 — reverse index.** Not used.

### 9.4 GCGeneration — **GREEN**

[GCGenerationAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs) ·
[GCGenerationAnalysisOptions.cs](../../src/DumpDetective.Core/Options/GCGenerationAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TopLohTypeLimit` | 15 | 1 — rows | move to render |
| `TopGenProfileLimit` | 20 | 1 — rows | move to render |
| `LohThresholdPercent` | 20.0 | 5 | keep |
| `Gen0PressureThresholdPercent` | 40.0 | 5 | keep |
| `PohThresholdPercent` | 5.0 | 5 | keep |

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

---

### 9.5 GCHandle — **GREEN**

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

---

### 9.6 DependentHandle options — **GREEN**, and the class looks orphaned

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

---

### 9.7 LockGraph — **GREEN**

[LockGraphAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs) ·
[LockGraphAnalysisOptions.cs](../../src/DumpDetective.Core/Options/LockGraphAnalysisOptions.cs)

Single knob `MaxContestedLocksToShow = 15`, Category 1. The options class is deleted outright.

**Q7 — nothing corrupted.** Applied at
[:61, :68, :71-72](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs#L68) after the
lock graph is built. The name is honest for once: `…ToShow`.

**Q8 — minor.** [:68](../../src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs#L68) uses
`.OrderByDescending(…).Take(…)`; moving the limit to the render layer is the moment to replace the
LINQ per the project's no-LINQ-in-hot-paths rule.

---

### 9.8 LohFragmentation — **GREEN**

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

---

### 9.9 SegmentReservation — **GREEN**, but for an unusual reason

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

---

### 9.10 Jit — **GREEN**, worst Q7 finding so far

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

### 9.11 Array — **GREEN**

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

**Q5.** Full element walks over qualifying arrays (length ≥ 10,000). Bounded by total array elements
on the heap, read through already-mapped pages. Estimate: seconds. Worth measuring if a dump has
unusually many large arrays.

---

### 9.12 String — **AMBER**, the first analyzer where cap removal is not sufficient

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

---

### 9.13 AsyncStateMachine — **GREEN**

[AsyncStateMachineAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs) ·
[AsyncStateMachineAnalysisOptions.cs](../../src/DumpDetective.Core/Options/AsyncStateMachineAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `TypeCandidateLimit` | 200 | 3 | delete |
| `HistogramInstanceCapPerType` | 1,000 | 2 — sampling | **delete** |
| `HistogramTopTypeLimit` | 10 | 3 | delete |
| `SuspendedMethodMapLimit` | 20 | 1 — rows | move to render |
| `TopCapturedSizeEntries` | 10 | 1 — rows | move to render |
| `TopTypeLimit` | 20 | 1 — rows | **verify**: no use found in `DumpDetective.Analysis` |
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

**Q5 — measure.** Removing the histogram caps means the second heap pass tracks every
state-machine method table and every instance rather than 10 x 1,000. It is still a single bounded
heap pass with O(types) accumulator state, so the expected cost is one extra scan, but this is the
group-2 analyzer most worth timing rather than estimating.

**Q3 — no risk.** Histogram state is a per-type state-value counter, O(types x distinct states).

### 9.14-9.16 preamble: group 3 shares one root cause

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

### 9.14 StaticRootLeak — **AMBER**

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

### 9.15 FinalizableObject — **AMBER**

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

### 9.16 GCRoot — **AMBER**

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

**Q5 — this is the group-3 analyzer whose cost most needs measuring.** Removing `PathSearchTopN`
means a root path for *every* candidate rather than 25. Served from the dominator tree this is a
parent-pointer walk per candidate (cheap); served from the current BFS it is O(candidates x graph)
and would be unacceptable. **The verdict depends entirely on the §10 workstream landing first** —
do not delete these caps against the existing BFS.

### 9.17 Collection — **AMBER**

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

---

### 9.18 Dominator — **GREEN** (mostly nothing to do)

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
| `TopFinalizerTypesToShow` | 10 | 1 — rows | move to render |
| `TopHighlyReferencedObjectsToShow` | 15 | 1 — rows | move to render |
| `EnableExactDominatorTree` | true | capability | **keep**, already correct |
| `ExactDominatorTreeMemoryBudgetBytes` | 20 GB | 3 — **memory** | **keep**, already correct |

#### Q7 — `RootPathLargeFanoutThreshold` is not a budget, it is an exclusion

Documented as *"Fanout threshold above which a reference path is considered 'large' and **skipped** to
avoid exploring extremely high-connectivity clusters."* Paths through any object with more than 100
referents are not searched more cheaply — they are **not searched**. Those objects are static caches,
singletons and interned strings: the most likely retainers in a real leak.

Combined with `MaxParentsPerChild` (§6.2), the same class of object is excluded twice, at two
different layers, for the same reason. **Resolving §6.2 without also resolving this leaves the
exclusion in place** — folded into D3.

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

---

### 9.19 EventLeak — **AMBER**

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
| `TopSubscriberTypesToShow` | 5 | 1 — rows | move to render |
| `TopDetailedInstancesPerGroup` | 5 | 1 — rows | move to render |
| `EnableDiagnostics` | true | diagnostics | move to diagnostics options |
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

---

### 9.20 ReferenceChain — **RED**

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
| `SkipArrays` | true | pruning | **verify semantics** — feeds `IsNoisyType`; confirm whether it prunes traversal or only presentation before changing |
| `TopCount` / `FallbackTopCount` | 5 / 10 | 1 — rows | move to render |
| `KnownLeakTypePatterns` | 3 patterns | 5 — heuristic | keep; identical in all three presets (pure duplication) |

---

### 9.21 TimerLeak — **AMBER**, no options class

[TimerLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs)

Has **no options class at all** and does not appear in `AnalysisOptions`. It calls
`finder.TryFindAnyRootPath(...)` directly
([:158](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs#L158)) and consumes
`searchTruncated`.

Zero knobs to delete — it inherits only `RootPathFinder` defaults now that §6.2 (`MaxParentsPerChild`,
deleted) and §6.3 (`RootSetCache`'s 256-frame cap, scoped to a cosmetic report label, not root
discovery) are both resolved. It is the cleanest demonstration that this refactor is not only about
the options surface: an analyzer with no configuration at all was still not exact, purely from
shared-traversal bounds below the options layer.

**Was used as the canary.** Because it has no knobs, its output changing after the shared traversal
became exact was attributable purely to that, not to any per-analyzer option change.

---

> **9.22 removed** — was a duplicate audit of the same analyzer covered in §9.18 (Dominator). See the
> roster correction near the top of this document.

### 9.23 Thread — **AMBER**

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

#### `AdaptForSize` is a third scaling layer, invisible to the user

`ThreadAnalysisOptions.AdaptForSize(options, DumpSizeTier)` divides
`MaxThreadsToCaptureSnapshots` and `MaxSampledStackSnapshots` by **4 (Large) / 2 (Medium) / 1**.

So the effective value is `preset value ÷ size divisor` — and the divisor is derived automatically
from dump size, not configured. On a large dump the Balanced default of 20 thread snapshots becomes
**5**. Nothing surfaces this. Fifth instance of *configured value ≠ applied value* (§11.6).

**Q7 — `MaxFramesForThreadScan = 8`.** Eight frames per thread, four at Fast. Compare Jit's 200
(§9.10) and `RootSetCache`'s 256 (§6.3): three different frame budgets in three layers, differing by
32x. Any wait-pattern or hotspot conclusion drawn from 8 frames describes the top of the stack only.

**Q7 — thread snapshots are randomly sampled.** `SamplingSeed` exists to make the sample
*deterministic*, not complete — the same "make truncation reproducible" workaround as Boxing's
determinism sort (§9.1) and Module's (§9.3). Third instance.

---

### 9.24 ThreadStackCluster — **GREEN**

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

---

### 9.25 Hang — **GREEN**

[HangAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HangAnalysisOptions.cs)

| Knob | Default | Category | Action |
|---|---:|---|---|
| `MaxTasksToScan` | 50,000 | 3 | delete |
| `TopWaitingThreadsPerGroup` | 5 | 1 — rows | move to render |
| `TopContinuationTypesToShow` | 5 | 1 — rows | move to render |
| `LongWaitThreshold` | 5 | 5 — **varied by tier** | keep, stop varying |
| `HighThreadPoolThreshold` | 100 | 5 — **varied by tier** | keep, stop varying |

Two more semantics-by-tier thresholds for §3.1: a thread waiting 6 seconds is *not* long-waiting at
Fast (8) and *is* at Full (3). Whether a dump is diagnosed as hung depends on the tier.

---

### 9.26 Crash — **GREEN**, and it is the reference implementation

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

---

### 9.27 Memory — **GREEN**

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

---

### 9.28 HeapTopology — **GREEN**, one line, large effect

[HeapTopologyAnalysisOptions.cs](../../src/DumpDetective.Core/Options/HeapTopologyAnalysisOptions.cs)

One knob, `CountSohObjects`, and its own doc describes it as an exactness switch:

> When `false` (**default**), per-object counting is skipped for all SOH segments. Only LOH and POH
> segments are counted exactly. Set `true` when exact SOH object counts are required.

Fast and Balanced set `false`; only Full sets `true`. **The default configuration does not count the
small object heap** — the bulk of objects on nearly every dump.

Under an exactness goal this is the most favourable change in the audit: set it to `true`
permanently, delete the knob, delete the options class. No structural work, no new dependency.

**Q5 — the one cost to confirm.** This enables per-object counting across all SOH segments, i.e. a
full heap traversal that is currently skipped. It should be served from the existing index rather
than a fresh ClrMD walk; verify which it does before assuming the cost is free. Add to §11.4.

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

**Q2 — check before deleting.** Confirm these scans are index-backed rather than per-object ClrMD
walks; if the latter, unbounding is a genuine cost increase rather than a free one. Added to §11.4.

---

### 9.30 AllocationPattern — **AMBER**

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
| `AbsoluteDeadCountThreshold` | 10,000 | 5 | keep |
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
| D1 | **`ReferenceChainSearchMode`** — collapse to one exact strategy, or keep as a genuine algorithm selector? Deleting `AnalysisProfile` orphans the mapping but not the enum. | §9.20 |
| D2 | **Schema version bump.** Do domain results reach the serialized report? If so, removing `TypeScanCapped`, `TypeScanCapUsed`, `InstanceCountCap`, `ExcludedModuleCount` etc. is a schema change per [schema-versioning.md](../schema-versioning.md). Decide once, apply everywhere. | §9.1, §9.2, §9.3, §9.6 |
| D3 | **Fanout exclusions.** `RootPathLargeFanoutThreshold` (100) and `LargeFanoutThreshold` (100) *exclude* high-fanout objects from search rather than budgeting them. Resolving §6.2 alone leaves these in place. | §9.20, §9.22 |
| D4 | **Every Category 5 threshold keeps its Balanced value** — but sanity-check each on its merits rather than inheriting "Balanced was the middle option." Seven were varied by tier. | §3.1 |
| D5 | **Where do render-layer display limits live** — per-analyzer, or one shared policy? GCHandle alone has six lists governed by one knob today. | §9.5, §10 |
| D6 | **Report-artifact toggles** (`ProduceRawExports`, `EnableDiagnostics`, `SurfaceProbingExceptions`, `ProduceClusterExports`) are not analysis config. Move to report/diagnostics options or keep? | §9.12, §9.17, §9.19, §9.24 |
| D7 | **AllocationPattern's `SelectionMode` and `SelectionPriority`** survive the tier system as genuine algorithm choices. `ScanStrategy` collapses to `FullScan`. Pick one mode/priority and justify it. | §9.30 |
| D8 | **`ThreadAnalysisOptions.AdaptForSize`** — a third, automatic scaling layer keyed on `DumpSizeTier`. Delete with the caps, or keep as a memory guard on snapshot capture? | §9.23 |
| D9 | **`AsyncChainDetectionMode`** (Thread) and **`ReferenceChainSearchMode`** (§9.20) are the two remaining mode enums after `TypeEnumerationMode` and `ScanStrategy` collapse. Decide together — same shape of question. | §9.20, §9.23 |
| D10 | **The typed-resource quartet's caps are `private const`, not `AnalysisOptions`.** Promoting `MaxStateSamplesPerType`/`TopSampleCap` into a shared options surface (one per analyzer, or one shared record) is a bigger decision than deleting them, since the mechanism itself is currently outside the config system entirely. | §9.32-9.34 |

### 11.3 Verify before deleting

| # | Verification | Source |
|---|---|---|
| V1 | **`DependentHandleAnalysisOptions` is orphaned** — confirm no reflection- or serialization-based consumer, and whether a `DependentHandleAnalyzer` was planned rather than removed. | §9.6 |
| V2 | **`AsyncStateMachineAnalysisOptions.TopTypeLimit`** — no use found in `DumpDetective.Analysis`, but the grep was truncated and `AllocationPatternAnalyzer` has a same-named knob. Confirm cleanly. | §9.13 |
| V3 | **`ReferenceChainOptions.SkipArrays` semantics** — it feeds `IsNoisyType(type, skipArrays)`. Confirm whether it prunes traversal or only presentation before changing it; the two have very different blast radii. | §9.20 |
| V4 | Run the §5 **dead-knob grep on every remaining analyzer**, not just the audited ones. Three dead or inert knobs found so far (`ModuleSelectionMode`, `IncludeExcludedModuleSummary`, `DeduplicationStringCountThreshold`). | §5, §9.3, §9.12 |

### 11.4 Measurements deferred from Q5

Fold these into the same real-dump run as B3/B4 — **one dump at a time, foreground**.

| # | Measure | Source |
|---|---|---|
| M1 | Module: uncapped `EnumerateTypeDefToMethodTableMap()` across all modules/domains. The Full preset's own comment warns time "grows quickly with the number of modules." | §9.3 |
| M2 | AsyncStateMachine: uncapped histogram heap pass (all types x all instances vs 10 x 1,000). | §9.13 |
| M3 | Array: full element walks over all arrays ≥ `SparseSampleMinLength`. | §9.11 |
| M4 | GCRoot: per-candidate root path once served from the dominator tree rather than BFS. | §9.16 |
| M5 | String: memory profile of an unbounded fingerprint map at ~10 M unique strings. | §9.12 |
| M6 | HeapTopology: cost of `CountSohObjects = true` — confirm it is served from the existing index rather than a fresh ClrMD heap walk. | §9.28 |
| M7 | AsyncTask: confirm the three scan caps bound index-backed reads, not per-object ClrMD walks. If the latter, unbounding is a real cost increase. | §9.29 |
| M8 | Thread: cost of unbounded stack scans once `MaxFramesForThreadScan` (8) and `MaxStackRootsToCount` (256) are removed. | §9.23 |
| M9 | Typed-resource quartet: cost of removing `MaxStateSamplesPerType` (500/type) across DbConnection, WcfChannel, HttpObject. Each state read is one `heap.GetObject` + field read; bounded by resource-instance count, not heap size — expect cheap, but unmeasured. | §9.32-9.34 |
| M10 | **Dominator retention provider's concurrent resident cost** — not construction peak (already measured, §4), but the tree kept alive alongside every analyzer between module order 140 and 340 that queries it. `ExactDominatorTreeMemoryBudgetBytes` (20 GB) has never been validated against this. | §10a |

### 11.5 Ordering constraints (violating these blows the budget or the bisect)

1. **`CollectionAnalyzer.cs:1154` lands alone, first.** Only runtime read of `AnalysisProfile`; only predicate rewrite in the plan. | §8.3, §9.17
2. **Do not delete GCRoot's caps against the existing BFS.** `PathSearchTopN` removal is O(candidates x graph) until B2 lands. | §9.16
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
| F8 | `ThreadAnalysisOptions.AdaptForSize` divides two caps by 4/2/1 on dump size — invisible third scaling layer. | §9.23 |
| F9 | AllocationPattern's `TopTypeLimit x ScanMultiplier` compound into the real scan limit. | §9.30 |

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

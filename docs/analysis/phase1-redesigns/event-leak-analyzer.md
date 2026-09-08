# EventLeakAnalyzer — Redesign

> Ground-up redesign, driven by a measured profile rather than by inspection.
> Constraints unchanged: streaming-only heap traversal, no materialization, bounded
> memory, disk-backed indices, no allocation on the hot path.

---

## 0. The measured baseline

Reference dump: `Crash_IIS_BALTSTPRD` (3.35 GB, 14 003 unique MTs, prebuilt index loaded
in 0.99s). Single sequential `AnalyzeAsync`, Release, no warmup.

| Phase | Time | % | Nature |
|---|---:|---:|---|
| `PopulateEvidence` root-path BFS | 34.28s | 36% | 3321 instances × 10.3ms; **229 paths found**, 0 truncated |
| `BuildFieldLayouts` | 22.80s | 24% | per-unique-MT ClrMD metadata, ~1.6ms × 14 003 |
| `SweepModuleStaticFields` | 19.51s | 21% | second full walk over module types |
| `BuildRootHintMap` | 15.44s | 16% | *cold* `GetOrBuildValidRoots` (760 roots) |
| `ProcessPublisherEntry` (hot path) | 1.48s | 1.6% | the per-object scan |
| index enumeration overhead | 1.12s | 1.2% | |
| group build + sort, stats, snapshots | 0.10s | 0.1% | 1269 groups |
| **TOTAL** | **94.74s** | | |

Two facts dominate every design decision below.

**The per-object hot path is 1.5% of runtime.** Any redesign premised on eliminating
per-object allocation, string interning, or the top-instance min-scan is optimizing
1.48 seconds out of 94.7. Measured twice (1.53s, 1.48s). This closes the question.

**The root-set build is not ours.** `BuildRootHintMap` already routes through
`cache.GetOrBuildValidRoots`; the 15.44s is the cold build, and the *second* call to the
same method inside `PopulateEvidence` costs 0.00s. In a full pipeline run `GCRootAnalyzer`,
`StaticRootLeakDetector` and this analyzer share it — whoever runs first pays. It is a
platform cost, not an EventLeak cost.

**Attributable cost: ~79s** — BFS 34.3 (43%), field layouts 22.8 (29%), static sweep
19.5 (25%), hot path 1.5 (2%).

### Target

| Phase | Now | Target | Mechanism |
|---|---:|---:|---|
| Evidence enrichment | 34.3s | ~1.5s | bound by group, skip resolved, global budget (§3) |
| Type metadata (both walks) | 42.3s | ~18s | single registry pass serving scan + statics (§2) |
| Hot path | 1.5s | 1.5s | unchanged (§4) |
| **Attributable total** | **~79s** | **~21s** | |

### 0.1 Measured at scale (25.6 GB dump)

Same harness (`EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`), re-run against
`w3wp.exe_260421_175618.dmp` — the same 25.6 GB / 86.5M-object dump used in
[root-path-finder.md](root-path-finder.md) §4.2 (5037 roots → 2393 seeded). Median of 3 runs,
warm prebuilt index (first run paid a one-time 274s index build; runs 2–3 loaded it in ~1.3s).

| Phase | 3.3 GB | 25.6 GB (median of 3) | Stability |
|---|---:|---:|---|
| `BuildFieldLayouts` | 22.80s | **~121.6s** | rock steady: 121.61 / 121.06 / 122.22 |
| `ProcessPublisherEntry` (hot path) | 1.48s | 3.3s–28.2s | **noisy, not reproducible**: 28.15 / 19.11 / 3.31 |
| `SweepModuleStaticFields` | 19.51s | **~19.3s** | stable: 21.98 / 18.03 / 19.27 |
| `PopulateEvidence` (BFS) | 34.28s (229/3321 = 6.9% hit) | **~60.5s (39/2856 = 1.4% hit)** | stable: 64.22 / 55.59 / 61.63 |
| **`AnalyzeAsync` total** | **94.74s** | **~215s** (218.55 / 210.88, excl. one-time build) | |

Two findings hold up across all three runs and change the priority ordering above:

**`BuildFieldLayouts` is the dominant cost at scale, and disproportionately so.** 12,376
unique MTs here versus 14,003 at 3.3GB — *fewer* types — yet the walk takes ~5.3× longer.
Per-MT cost is not driven by MT count alone; something scales with heap size or object
population instead (candidate: `GetTypeByMethodTable` cache-miss rate, or field-enumeration
cost against a much larger metadata/PDB surface). This is now 57% of `FindEventLeaks`
(122s / 149–163s) at 25.6GB versus 24% at 3.3GB, making §2's registry the highest-value
lever at scale, but also raising the stakes on §2's "Open risk" — eager-build cost needs to
be measured against *this* number, not the 3.3GB one, before committing to lever 1 alone.

**The BFS hit rate degrades further at scale, not just the cost.** 6.9% → 1.4% (39/2856,
identical across all 3 runs), consistent with root-path-finder.md's own finding that the
shared `RootPathFinder` degrades on 25GB+ dumps. This confirms §3.1's diagnosis generalizes
past the 3.3GB reference dump, and reinforces §3.2's point that a fixed finder plus a
`MaxGroupsToEnrich` bound is not sufficient on its own at this scale — enrichment needs a
fallback that doesn't assume paths are findable.

**`ProcessPublisherEntry` is not usable evidence at this dump size.** It swung from 3.3s to
28.2s across identical repeated runs on a warm index — an order of magnitude more variance
than the metric itself. Likely GC pauses or unattributed time shifting in/out of the bucket
rather than a real hot-path regression. §4's "don't touch it" call should not be revisited
based on this number; a dedicated profile (not this harness) would be needed to say anything
about the hot path at 25GB+.

**`SweepModuleStaticFields` stays flat (~19s, both dump sizes)**, confirming it tracks module
type count rather than heap size — supports §5's registry-driven design as-is.

---

## 1. Ground rules for this pass

This is not an incremental patch — the current `EventLeakAnalyzer` /
`EventLeakFastScanner` / `SweepModuleStaticFields` triad is being replaced outright, not
tuned. Two documents motivate the shape below and are treated as settled inputs, not
re-litigated here:

- [eventleak-analyzer-audit.md](../phase1/eventleak-analyzer-audit.md) — the coverage,
  report-quality, and correctness audit. Its P0–P3 items are referenced by number
  throughout; this design's job is to make the *architecture* incapable of reproducing
  the defects the audit found, not just to patch each one.
- §0 above — the measured baseline. Every phase boundary below is still priced against
  those numbers; nothing here is exempt from being measured and reverted if it doesn't
  pay for itself.

Same constraints as before: streaming-only heap traversal, no materialization, bounded
memory, disk-backed indices, no allocation on the hot path.

---

## 2. Shape

Six phases. A and B are unchanged in *purpose* from the previous draft of this doc;
C through F are new or substantially reworked.

```
Phase A  registry      once, pre-scan   →  PublisherRegistry     (immutable)
Phase B  scan           hot, streaming   →  GroupAccumulator map  (no ClrMD)
Phase C  statics        once, post-scan  →  same map              (registry-driven)
Phase D  enrichment     bounded top-K    →  Evidence              (root paths + retained bytes)
Phase E  correlation    once, in-memory  →  cross-group views     (no heap access)
Phase F  present        once             →  domain result         (structured, not strings)
```

The current implementation interleaves A into B (lazy per-MT), repeats A inside C
(independent module walk), lets D run unbounded, and has no E at all — cross-group
insight (audit P2-1/P2-2) doesn't exist as a concept, only per-group breakdowns. F isn't
really a phase today either: pre-formatted strings (audit #7) are baked in during
snapshot construction instead of being a presentation-layer concern.

The structural change that makes B, C, and (eventually) audit P3-1/P3-2 share code
without duplicating the type walk is Phase A.

---

## 3. Phase A — `PublisherRegistry` and pluggable publisher shapes

**Target: 42.3s → ~18s**, same number as before — deduplicating the type walk is still
the largest structural win regardless of what consumes it. What changes is *what* the
registry serves.

### 3.1 The problem with three scan paths

Today there are three independent ways a publisher gets discovered:

- `EventLeakFastScanner` — pointer-based, the real hot path.
- `GetEventSubscribers` — `ClrObject`-based, dead in production (audit Area 1,
  "Unexpected Functionality"), kept alive only by tests.
- `SweepModuleStaticFields` — a second full module walk for static-only publishers.

Two of these exist to answer the same question ("does this field back an event") for
different populations (instance-bearing vs static-only types), and the third is a stale
duplicate nobody deletes because tests still reach it. This is also *why* audit P3-1
(`EventHandlerList`) and P3-2 (weak-event detection) are scoped as "Evolution" rather
than "Improvement": under the current shape, adding either means writing a fourth
independent walk.

### 3.2 `IPublisherShape` — one interface, one registry pass

```csharp
internal interface IPublisherShape
{
    // Called once per unique MT during the registry build. Return the descriptors
    // this shape recognizes on `type`, or empty if none. No heap access — metadata only.
    IEnumerable<EventFieldDescriptor> Describe(ClrType type);

    // Called during Phase B/C for an object whose MT matched this shape's descriptors.
    // Pointer-only reads via IMemoryReader — no ClrObject construction.
    void Extract(IMemoryReader reader, ulong objAddress, in EventFieldDescriptor descriptor,
                 List<SubscriberInfo> subscribersOut);
}
```

- `FieldBackedDelegateShape` — today's field-backed `event`/delegate pattern. Ships
  first; existing behavior, existing tests, no regression risk.
- `EventHandlerListShape` (audit P3-1) — reads WinForms `Control.Events`
  (`System.ComponentModel.EventHandlerList`) via its known internal layout. Additive:
  registers against the same registry, runs through the same Phase B/C dispatch, no new
  scan.
- `WeakEventShape` (audit P3-2) — recognizes `WeakEventManager`/`ConditionalWeakTable`-
  backed chains and tags matches as `IsWeakEvent = true` rather than suppressing them
  outright, so Phase F can render them as informational instead of a false-positive leak.

`PublisherRegistry.Build` runs each registered shape's `Describe` once per unique MT
during the single eager pass (§3.3), producing one flat descriptor array regardless of
how many shapes are registered. Phase B and Phase C both dispatch through
`registry.TryGetDescriptors(mt, out slice)` — neither knows or cares which shape produced
a given descriptor. `GetEventSubscribers` and the standalone module walk in
`SweepModuleStaticFields` are deleted, not deprecated (audit P2-7 folds in for free).

### 3.3 Structure and build levers (unchanged from the prior draft)

```csharp
internal sealed class PublisherRegistry
{
    private readonly FrozenDictionary<ulong, (int Start, int Length)> _byMT;
    private readonly EventFieldDescriptor[] _descriptors;   // flat, shape-tagged, no per-MT arrays
    private readonly string[] _names;                       // deduped; hot path uses int ids
    private readonly ulong[] _staticPublisherMTs;            // drives Phase C directly

    public readonly int DelegateTargetOffset;
    public readonly int DelegateInvListOffset;
    public readonly int DelegateInvCountOffset;

    public static PublisherRegistry Build(ClrHeap heap, IHeapAnalysisCache cache,
                                           IReadOnlyList<IPublisherShape> shapes,
                                           EventLeakOptions options, CancellationToken ct);
}

internal readonly struct EventFieldDescriptor
{
    public readonly int   Offset;      // ClrInstanceField.Offset frame
    public readonly int   NameId;      // index into _names
    public readonly bool  IsStatic;
    public readonly byte  ShapeId;     // which IPublisherShape produced this descriptor
}
```

Same three levers as before, in the same confidence order, unchanged by the shape
abstraction (each shape's `Describe` is still just metadata inspection, so the levers
apply per-shape identically):

1. **Deduplicate the walk** (high confidence, ~19s) — one enumeration serving every
   registered shape instead of one walk per publisher pattern.
2. **Filter before resolving** (medium) — skip full field enumeration for types that
   cannot be publishers under *any* registered shape.
3. **Restrict instance layouts to MTs with live instances** (medium) — via
   `TypeAggregateIndex.bin`; static-only types still need their static fields.

**Open risk, restated:** eager build pays for every candidate type under every
registered shape, even on dumps where most shapes find nothing. Lever 3 mitigates
this; Phase A must still be measured standalone (per shape, then combined) before
Phase B is touched. If adding `EventHandlerListShape`/`WeakEventShape` measurably taxes
the eager pass without proportional coverage gain, they ship disabled by default
(`EventLeakOptions.EnabledShapes`) rather than being reverted outright — the interface
cost is paid once either way; the per-shape cost is opt-in.

**Optional follow-up (not in scope):** the registry is a pure function of the dump's
module set and could be persisted next to the index. Defer until the in-memory version
is measured.

---

## 4. Phase D — bounded evidence enrichment

**Target: 34.3s → ~1.5s.** Largest single win, smallest change to the perf profile —
but the *content* of what gets enriched changes, per audit item #4 and #6.

Measured: 3321 instances, 10.3ms each, **229 root paths found (6.9%)**, 0 truncated.
We are paying 32 seconds to conclude "no path found."

The count is not a top-K. It is `TopDetailedInstancesPerGroup` (5) × 1269 groups — bounded
per group, unbounded in the number of groups.

### 4.1 Why the hit rate is 6.9% — root cause

Reading `RootPathFinder` explains both the failure rate and the cost, and the cause is
structural rather than a matter of tuning.

`TryFindAnyRootPath` runs three phases per instance: build a candidate set, build a
reverse index over that set, then BFS from each root inside it. Two defects:

**The candidate budget is consumed by seeding.** `CandidateSetBuilder.Build` seeds the
candidate set with the target **plus all 1411 roots** before expanding anything. Of the
`MaxCandidateNodes = 5_000` budget, 28% is gone at initialisation, leaving ~3589 nodes of
expansion split across two frontiers — over a heap with millions of objects.

**The target frontier expands in the wrong direction.** To learn what *retains* a
publisher you need its **incoming** references. The builder expands **forward** from the
target, and the comment in the code says so plainly: *"forward refs of target are not
useful for reverse; instead we do a second BFS from target outward… useful to collect the
neighbourhood."* The target-side frontier therefore explores what the publisher points
*at* — its own subscribers and fields — which is the one direction that cannot lead to a
root. The search is not bidirectional in the direction that matters.

A path is found only when a root's shallow forward shell happens to reach the publisher.
That is the ~7%: publishers sitting near a root (statics, directly-referenced singletons).
A genuinely leaked publisher, retained through a long chain, is structurally unreachable
at these limits — which is precisely the population this analyzer exists to find.

**"0 truncated" is not evidence of a complete search.** `searchTruncated` is set only when
the *final* BFS reports `limited`. That BFS runs inside an already-tiny candidate set and
terminates by exhausting a small connected component, never by hitting its cap. The phase
that actually runs out of budget — candidate-set construction — has no truncation signal
at all. The flag measures the wrong thing, and any tuning decision based on it is unsound.

**The cost is unconditional on success.** Phase 2 builds the reverse index over the full
5000-node candidate set — ~5000 × (`heap.GetObject` + reference enumeration) — *before*
any path attempt. Across 3321 instances that is ~16M ClrMD object materialisations at
~2µs each, which is where the 34s lives. Full price, no result, 93% of the time.

**This is platform-wide.** The same `RootPathFinder` backs `DominatorAnalyzer`,
`StaticRootLeakDetector`, `TimerLeakAnalyzer` and `ReferenceChainAnalyzer`. Their
"why is this alive" evidence is likely as thin, and the fix belongs in the shared
component. See [root-path-finder.md](root-path-finder.md) for the redesign; this section
depends on it.

**That shared redesign does not resolve to a general fix at 25GB+ scale.** The
whole-heap predecessor-column approach in `root-path-finder.md` §3 is correctness-verified
but fails its own wall-clock gate on dumps in the 25GB+ range (§4.2: 677–911s vs a 120s
cutoff), and a follow-up attempt to fix that with multi-runtime parallel BFS turned out to
be memory-unsafe on the same dump class rather than faster (§4.3: aborted at both 4 and 2
workers due to per-runtime memory-mapped-file and metadata-cache cost, before the BFS
speedup could even be realized). Practically, that means §3.2 below should not assume a
free, always-fast root-path search once the finder is "fixed" — on large dumps the fix is
a scoped candidate-reachable subgraph (root-path-finder.md §6), not a whole-heap build, so
enrichment still needs its own bound and fallback regardless of finder version.

### 4.2 What §4 becomes once the finder is fixed

The bound below is damage control while the hit rate is 7%. Once paths are found reliably
and cheaply, `MaxGroupsToEnrich` should be set by the *value* of the evidence rather than
by the cost of failing to produce it, and can likely be raised substantially.

**Redesign: rank groups, enrich only the head of the list.**

```csharp
// after grouping, before enrichment
groups.Sort(byTotalSubscribersDescThenSeverity);
int enrichCount = Math.Min(groups.Count, options.MaxGroupsToEnrich);   // default 25
```

Enrich instances belonging to those groups only: 25 × 5 = ~125 instances, not 3321.
At the measured 10.3ms that is ~1.3s.

Three additional guards, each independently worthwhile:

- **Skip the BFS when a root hint already exists.** [`PopulateEvidence`] falls back to
  `inst.RootHint` when the search fails, so a publisher that is already a known direct
  root gains nothing from a 10ms search.
- **Global wall-clock budget** (`MaxEvidenceEnrichmentMs`, default ~2000). Enrichment
  is best-effort decoration; it must never be the dominant cost again. Instances beyond
  the budget keep their `RootHint` and are marked not-enriched.
- **Investigate the 6.9% hit rate before optimizing around it.** 0 truncations with a
  93% failure rate suggests the limits may be structurally unable to reach these
  publishers. If the search cannot succeed for this shape of object, the correct fix is
  to remove or re-scope it, not to run less of it. **This must be answered before
  implementing the bound** — the answer changes the design from "enrich fewer" to
  "don't enrich this way."

No second heap pass is needed. Instances are already captured (capped) during Phase B at
negligible cost, so enrichment reads what the scan already collected.

### 4.3 Two evidence sources, not one conflated field (audit #4, #6)

`PopulateEvidence` today builds exactly one `rootPath`, from the publisher's address,
and stores it in a single `Evidence.SampleRootPath` field that also gets overwritten by
a subscriber-derived `RootHint` when the BFS fails. The two answer different questions
("why is the *publisher* alive" vs "here's a subscriber that happens to be near a root")
and collapsing them loses whichever one didn't win.

```csharp
public sealed record Evidence(
    int SchemaVersion,
    string? PublisherRootPath,      // BFS from the publisher, when it succeeds
    string? SampleSubscriberHint,   // direct-root hint on any subscriber, always cheap
    bool SearchTruncated,
    IReadOnlyList<EvidenceSignal> Signals);
```

Both are populated where available; Phase F decides how to present them (the publisher
path is the primary "why is this alive" answer; the subscriber hint is a fallback,
never a silent overwrite). The BFS bound from §4.1/§4.2 applies to `PublisherRootPath`
only — `SampleSubscriberHint` is a dictionary lookup already available from Phase B's
`rootHints` map, effectively free, and is populated for every enriched instance
regardless of whether the BFS ran.

### 4.4 Retained bytes: honest by default, exact where it's cheap (audit P0-2, #1, #3, P3-7)

`EstimateGroupRetainedBytes` iterating only the capped `TopInstances` (audit #3) is not
carried forward. Two tiers, both labelled, neither silent:

**Tier 1 — always on, cheap, aggregate.** `TotalSubscribers × avgSubscriberSizeByMT`
computed from the type statistics cache (already hydrated, ~0.03s per the baseline) —
covers *all* instances in a group, not the stored top-N. Labelled
`"Estimated (type-average, all instances)"`. This is audit P0-2/2-3 exactly as scoped
before; nothing about the shape abstraction changes it.

**Tier 2 — deferred, not designed further in this document.** Sketched here only to
record the constraint that ruled out the original approach; the mechanism itself
(§10 has the deferral rationale) is out of scope until `EventLeakAnalyzer`'s own
architecture — registry, shapes, phases A–F — has shipped and settled. Design detail
kept below for the record, not as something to build against yet.

**Tier 2 — opt-in, exact, top-K only, and not inside `AnalyzeAsync` at all.**
`EventLeakAnalyzer.AnalyzeAsync` only ever sees its own `AnalysisContext` — there is no
reliable way for one analyzer to ask "has `DominatorAnalyzer` already run" from inside
`RunAnalyzersPipelineStage`. Analyzers are ordered by `Order` but that's a scheduling
hint, not a completion guarantee visible to peers; `IsThreadSafe` analyzers can run
concurrently with each other; and there's no shared results bag mid-pipeline for an
analyzer to query. `AnalysisContext` carries the current analyzer's own inputs (heap,
cache, options), not the outputs of ones that happened to run earlier. Treating "already
ran" as checkable from inside `AnalyzeAsync` was wrong in the earlier draft of this
section — it assumes a capability the pipeline doesn't expose.

The place that *does* see every analyzer's completed output is the existing
post-pipeline cross-reference step: `InsightEngine.Analyze(IReadOnlyList<AnalyzerRunResult> runs)`,
which already does exactly this pattern — `FindResult<DominatorDomainResult>(runs)` reads
another analyzer's finished result after `RunAnalyzersPipelineStage` has produced the
full `AnalyzerRunResult[]` (see `docs/architecture.md` §14, "Pipeline Stages", and §5.6).
Tier 2 belongs there, not in `EventLeakAnalyzer`:

- `EventLeakAnalyzer` never references `DominatorAnalyzer` or its result type. It emits
  Tier 1 only, plus the publisher addresses needed to look a group up later
  (`EventLeakGroupSnapshot.SampleInstanceAddress`, already present as `PublisherAddress`
  on stored instances — no new field required).
- A new post-pipeline cross-reference step — either a dedicated
  `EventLeakDominatorEnricher` invoked from `GenerateFindingsStage` alongside
  `FindingGenerationPipeline`, or a small addition to `InsightEngine` itself if the
  output is meant to feed findings rather than the report body — reads both
  `EventLeakDomainResult` and `DominatorDomainResult` from the same `AnalyzerRunResult[]`
  and joins them by publisher address for the bounded top-K group set. If
  `DominatorDomainResult` is absent from `runs` (analyzer not registered, or excluded via
  config for this run), the join step no-ops and Tier 1 stands unlabelled-degraded — it
  was never anything but Tier 1 to begin with.
- This also removes the need for `EnableDominatorRetainedBytes` as an `EventLeakOptions`
  flag (§11) — whether Tier 2 runs is now a property of which analyzers were selected
  for the run, not a flag `EventLeakAnalyzer` has to check.

This would still close audit P3-7 ("Integrate with `DominatorAnalyzer` for accurate
per-event retained bytes") when built — at the layer the architecture actually supports
cross-analyzer reads at, instead of a mid-pipeline query capability that would need its
own concurrency contract (what does "already run" mean under `IsThreadSafe` parallel
execution?) to be reliable. But per §10, it's deferred until this document's own
architecture (§3–§8) has shipped — recorded here so the constraint isn't rediscovered
from scratch when the follow-up starts.

Total estimated retained bytes (Tier 1) is unaffected by this change and still gets
promoted into `EventLeakDomainResult`'s summary metrics directly (audit P1-1) — sum of
Tier 1 across all groups, computed once, no additional heap access, no dependency on
post-pipeline steps.

---

## 5. Phase B — the scan stays as it is

**1.48s. Do not touch it.**

Specifically **not** doing, on the evidence:

- **Numeric `(MT, offset, IsStatic)` group key.** The premise that the hot path allocates
  or interns strings per object is false — `DelegateFieldLayout` already carries
  pre-resolved names, resolved once per MT, and the hot path copies references. Worse,
  MT-keying *splits* groups that name-keying merges: in a multi-AppDomain process the
  same logical type has a distinct MT per domain, fragmenting one leak into N sub-threshold
  findings. Name-keying is a feature here.
- **Value-type `RawGroupCounter` replacing `GroupAccumulator`.** Optimizes 1.48s. The
  accumulator's `List`/`Dictionary` per group also feeds Phase D's instance capture for
  free; removing it would force a second pass to recover the same data.
- **Parallelizing Phase B.** There is 1.48s of parallelizable work. The historical
  regression is now explained: the dispatcher partitions by *address range* while
  EventLeak's dominant scan cost was per-*MethodTable* metadata, which address
  partitioning replicates across all K workers instead of dividing. Phase A removes that
  cost from the scan entirely, but what remains is too small to be worth partitioning.

The one change: Phase B looks up the frozen registry instead of lazily building
`_mtIndex`, so it performs **zero ClrMD calls**. Shape dispatch (§3.2) adds one array
index per matched descriptor, not a virtual call per object — descriptors are tagged
with `ShapeId` and resolved through a fixed small switch, not `IPublisherShape.Extract`
called through the interface on the hot path, to keep this measured cost at 1.5s rather
than reopening it.

---

## 6. Phase C — statics in one registry-driven pass

Iterate `_staticPublisherMTs` once after the scan, reading static delegate fields at
known offsets, for every registered shape that produced static descriptors. Statics
leave the hot path entirely.

This also fixes a live correctness bug. `SweepModuleStaticFields` accepts
`processedStaticMTs` and **never consults it** — it dedups only against its own local
`seenModuleMTs`, while the scanner separately adds MTs to `processedStaticMTs`. Any
static event field on a type that also has heap instances is accumulated **twice** today.
Registry-driven single-sweep makes the dedup set unnecessary rather than fixing it.

---

## 7. Phase E — cross-group correlation (audit P2-1, P2-2)

New phase; no equivalent exists today. Runs once after Phase C, over the completed
`GroupAccumulator` map — no heap access, no ClrMD, so its cost is a pure in-memory fold
and does not appear in the §0 baseline as a separate line item worth budgeting for
(sub-millisecond at the group counts measured there).

Two views, both flagged in the audit as "Low difficulty, High impact" — the highest
return-per-cost items in the whole document:

- **Top subscriber types across all groups.** Fold `GroupAccumulator.AllSubscriberTypeCounts`
  from every group into one dictionary. Surfaces "one type subscribing to fifty different
  publishers" — the type is the retention problem, not any single event — which no
  per-group view can show.
- **Top handler methods across all leaking events.** Same fold, keyed by
  `(SubscriberType, MethodName)` instead of type alone. Identifies a single factory or
  wiring method responsible for bulk subscription registration.

Both land in `EventLeakDomainResult` as their own named collections
(`TopSubscriberTypesAcrossGroups`, `TopHandlerMethodsAcrossGroups`), not buried in a
per-group breakdown — audit P2-1/P2-2 exactly as scoped, made a first-class phase instead
of a follow-up appendix so it can't be silently dropped from a future refactor the way
`GroupEventLeaks` (audit "Unexpected Functionality") was left as dead-but-present code.

---

## 8. Phase F — structured data, not pre-formatted strings (audit #7)

`EventLeakInstanceSnapshot.SubscriberTypes` today is `List<string>` holding
`"App.MyType (3)"` — report formatting baked into the domain model, so downstream
consumers (trend comparer, any future JSON/API consumer) can't sort or filter by count
without re-parsing a string built for display.

Phase F is the only place formatting happens. The domain model carries structured pairs:

```csharp
public sealed record SubscriberTypeCount(string Type, int Count);
```

`EventLeakSectionBuilder` renders `"{Type} ({Count:N0})"` at the point of display; the
domain result, trend comparer, and any other consumer see `IReadOnlyList<SubscriberTypeCount>`.
Same treatment applies to the other presentation-only decisions currently made too early:

- **Static leaks show `PublisherAddress = 0x0`** (audit #8) — the domain model keeps the
  real value (0 for static, meaningful for instance); Phase F renders `"(static)"` only
  when `IsStatic` is true, never a bare hex zero.
- **`PublisherGeneration = -1`** (audit #10) — same pattern: domain model keeps `-1`
  as "unknown/static", Phase F renders `"static"` or `"unknown"` based on `IsStatic`,
  not a dash.
- **`RootHint` translation** (audit P2-4) — raw ClrMD root-kind strings
  (`"LocalVar"`, `"StaticVar"`) are translated to human language (`"local variable"`,
  `"static field"`) in Phase F via a fixed lookup table, not stored translated in the
  domain model, so a future consumer that wants the raw `RootKind` still can.

---

## 9. Correctness fixes folded in

Independent of performance; each is a defect confirmed against the current code or the
audit.

**Orphaned subscribers — replaced, not patched (audit P0-1, Area 6 #2).**
`CountOrphanedSubscribers` tests `!rootHints.ContainsKey(addr)`, and `BuildRootHintMap`
contains only *direct* GC-root addresses. Nearly every transitively-retained subscriber
therefore counts as orphaned, applying a near-constant severity bonus that flattens
ranking. The audit's own conclusion is that a correct definition would need a
reverse-reference check expensive enough to not be worth it for a signal that's never
fired usefully — so this design doesn't try to fix "orphaned." It replaces the metric
with `IsDisposedButSubscribed` (audit P2-6): subscriber type implements `IDisposable`
and remains in an invocation list. Well-defined, cheap (one `ClrType.Interfaces` check
per unique subscriber MT, already resolved by the registry), and a strictly stronger
signal — disposed-but-still-subscribed is unambiguously a bug, unlike "not a direct GC
root," which is true of almost every live object. `SeverityOrphanedSubscriberBonus`/`Cap`
are removed from the severity formula and replaced by a new
`SeverityDisposedButSubscribedBonus`; the option keys are not kept reserved since nothing
should reintroduce the old definition.

**Retained bytes — see §4.4.** Superseded by the two-tier design there; no longer a
single "aggregate over all instances" fix, but a labelled Tier 1 (always) / Tier 2
(dominator-exact, opt-in) split.

**No process-lifetime static state.** `static ConcurrentDictionary<ulong, HashSet<string>>
_eventNameCache` is keyed by MethodTable — a per-dump address. Any host analyzing more
than one dump in a process can hit a stale entry at a colliding address and emit wrong
event names silently. Ownership moves into `PublisherRegistry`, whose lifetime is the
analysis — this falls out of §3 directly rather than being a separate fix, since the
registry already owns per-MT metadata and the event-name resolution that fed the old
cache is now part of `Describe`.

**Diagnostics and cancellation.** Twelve live `Console.Error.WriteLine("[PERF] …")` calls
(audit Area 1: "perf-investigation artifacts not gated on `EnableDiagnostics`") write to
stderr in production regardless of `EnableDiagnostics`. Route through the optional
`ILogger<T>` pattern. Thread `CancellationToken` through every phase with a periodic
check on a power-of-two interval (8192, not 10 000 — the `& (n-1)` mask requires it).

**Severity — continuous, versioned (audit #4, P3-6).** The step discontinuity at
`SeveritySubscriberThreshold` (9 subscribers → score 9, 10 → score 15, a 67% jump for a
1-subscriber difference) is replaced with a continuous curve
(`log2(subscriberCount + 1)`-scaled) so ranking degrades smoothly instead of clustering
around the old threshold. Because this changes what a given score number *means*,
`EventLeakDomainResult` carries a `ScoringVersion` stamp, and `EventLeakTrendComparer`
refuses to diff two results with different versions rather than silently reporting a
formula change as a severity swing. Raw subscriber count is retained as the ranking
tiebreak, unchanged from today.

**Delegate layout fallback for .NET Framework (audit #8, P2-8).** The hardcoded .NET 6+
offset fallback in delegate-layout discovery silently produces wrong reads on .NET
Framework 4.x dumps with incomplete symbols. `PublisherRegistry.Build` takes the runtime
version (already available from `ClrRuntime`) and selects the offset table explicitly;
when the version is ambiguous, the registry marks delegate-layout confidence as `Low` and
Phase F surfaces it as a caveat on the report rather than staying silent.

**Dead code removed, not deprecated (audit P2-7).** `GroupEventLeaks`,
`EnumerateEventEntries`, and `GetEventSubscribers` are deleted as part of the §3 rewrite
— they're superseded by the registry/shape dispatch, not left alongside it. If any of the
three is still test-only load-bearing today, that test moves to exercise the shape
interface directly instead of keeping the dead path alive to satisfy it.

---

## 10. Explicitly in scope now (previously deferred)

The prior draft of this document deferred `EventHandlerList` coverage and log-scaled
severity as separate proposals with their own risk. Both are folded in above because the
shape abstraction (§3) and the versioned-severity mechanism (§9) are cheap enough, once
built, that deferring them just means building the seam twice. What's still genuinely
out of scope:

- **§4.4 Tier 2, dominator-exact retained bytes, entirely — deferred, not just the
  unbounded version.** Beyond the top-K-only bound (still correct, kept in §4.4 for
  the record), Tier 2 depends on a post-pipeline join that lives outside
  `EventLeakAnalyzer` (`GenerateFindingsStage` or `InsightEngine`) and on
  `DominatorAnalyzer`'s output contract. Building that join now, before `EventLeakAnalyzer`'s
  own registry/shape/phase architecture (§3–§8) has shipped and its data model settled,
  risks designing the join against a domain-result shape (`EventLeakGroupSnapshot`,
  publisher-address exposure) that hasn't stabilized yet. Tier 1 (§4.4, always-on,
  in-analyzer) ships as part of this rewrite; Tier 2 is picked up as its own follow-up
  once §12's steps 1–7 are done and the shape of `EventLeakDomainResult` isn't moving
  under it anymore.
- **Subscription inventory mode** (audit "Evolution": scan all delegate fields
  independent of leak detection). A genuinely different mode, not a redesign of leak
  detection — the shape abstraction would serve it well if built, but it's a separate
  feature with its own options surface, not part of this rewrite.
- **Timer event specialization** (audit P3-3) and **`INotifyPropertyChanged` ranking**
  (audit #12). Both are a `WellKnownEventFilter` classification layered on top of Phase F
  output (tag findings whose event field name matches `Elapsed`/`Tick`/`PropertyChanged`),
  not a scanning or data-model change. Cheap to add once F exists as a real phase, but not
  required to ship the rewrite.

---

## 11. New options

```csharp
public IReadOnlyList<PublisherShapeKind> EnabledShapes { get; init; } =
    [PublisherShapeKind.FieldBackedDelegate];               // opt-in: EventHandlerList, WeakEvent
public int MaxGroupsToEnrich              { get; init; } = 25;     // Fast 10, Full 100
public int MaxEvidenceEnrichmentMs        { get; init; } = 2000;   // hard budget, best-effort
public int SeverityDisposedButSubscribedBonus { get; init; } = 15;
```

`SeverityOrphanedSubscriberBonus` / `Cap` are removed outright (§9) rather than reserved.

---

## 12. Sequencing

Each step is independently shippable and independently measured. No big-bang cutover.
The dependency order is stricter than a pure perf patch would need, because later steps
(shapes, correlation) build on the registry existing first.

1. ~~**Answer the 6.9% question.**~~ **Done** — see §4.1. The search expands forward from
   the target, which cannot reach a root, and spends 28% of its node budget seeding roots.
   The fix is in the shared `RootPathFinder`, not here: [root-path-finder.md](root-path-finder.md).
2. **§4 bounded enrichment**, including the publisher/subscriber evidence split (§4.3)
   and Tier 1 retained bytes (§4.4). Largest win (~33s), smallest diff, no structural
   change yet. Ship and measure alone. Interim measure — revisit the bound once the
   finder is fixed (§4.2).
3. **§9 correctness fixes** that don't depend on the registry: severity versioning,
   diagnostics/cancellation, dead code removal. Ship separately so any accuracy-test
   movement is attributable.
4. **§3 registry with `FieldBackedDelegateShape` only** — behavior-equivalent to today's
   scanner, measured standalone against the two walks it replaces (lever 1 first; levers
   2 and 3 only if the profile still justifies them). This is the point where
   `IsDisposedButSubscribed` (§9) becomes cheap to add, since it needs the registry's
   resolved subscriber MTs.
5. **§6 registry-driven statics.** Falls out of step 4; re-verify the double-count fix
   against `EventLeakAnalyzerAccuracyTests`.
6. **§7 correlation phase.** Falls out of step 4's completed accumulator map; no new
   heap access, low risk, ship promptly after.
7. **§8 structured data (Phase F).** Domain-model change with report-builder fallout;
   coordinate with any downstream JSON/API consumers before shipping.
8. **Deferred — §4.4 Tier 2 (dominator cross-reference).** Not part of this rewrite's
   sequence (§10). Picked up as its own follow-up once steps 1–7 have shipped and
   `EventLeakDomainResult`'s shape is stable; lives in `GenerateFindingsStage` or
   `InsightEngine` as a post-pipeline join, not inside `EventLeakAnalyzer`. Also
   contingent on `DominatorAnalyzer`'s own output contract being stable enough to depend
   on — check with that analyzer's owner when this is picked back up.
9. **`EventHandlerListShape` / `WeakEventShape`** (§3.2). Additive once the registry
   exists; ship independently, gated behind `EnabledShapes`, each with its own accuracy
   tests against a WinForms/weak-event fixture dump.
10. Re-measure the full profile. Stop when the remaining cost is not worth the risk.

**Test surface to hold steady throughout:** `EventLeakAnalyzerAccuracyTests`,
`EventLeakAnalyzerDiscrepancyTests`, `EventLeakFindingGeneratorTests`,
`EventLeakSectionBuilder`, `EventLeakTrendComparer` and its stored baselines, plus new
coverage this rewrite adds: `IPublisherShape` conformance tests per shape,
`IsDisposedButSubscribed` accuracy tests, and correlation-phase (§7) fold correctness
against a hand-built accumulator fixture. The discrepancy tests exist because the
fast-scanner and ClrMD paths already disagree; step 4 must declare which path is ground
truth before it changes either — and since `GetEventSubscribers` (the ClrMD path) is
deleted rather than kept as a comparison target (§9), that declaration is "the registry
path is ground truth" by construction, not a judgment call deferred to test-fixing time.

**Measurement harness:** `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`,
run as a single filtered test (`DD_RUN_DISCREPANCY_TESTS=1`), never the full suite.
Run-to-run variance on `FindEventLeaks` was measured at ~18% (63.4s vs 74.8s), so treat
single runs as coarse and compare medians of three. Phases E and F add negligible
measured cost (§7, §8) and are not expected to move this harness's numbers; if they do,
that's a signal something in the fold or formatting step is doing heap work it shouldn't.

---

## Summary of decisions

| Decision | Current | Redesigned | Evidence |
|---|---|---|---|
| Evidence enrichment | 5 × every group (3321) | top-K groups + budget (~125) | 34.3s, 6.9% hit rate |
| Evidence content | one conflated `rootPath` field | publisher path + subscriber hint, separate | audit #4, #6 |
| Retained bytes | top-5 instances, no caveat | Tier 1 all-instance estimate now; Tier 2 dominator-exact deferred (§10) | audit P0-2, #1, #3, P3-7 |
| Type metadata | two walks, one lazy one eager | one eager frozen registry serving pluggable shapes | 22.8s + 19.5s |
| Publisher detection | 3 independent code paths (1 dead) | 1 registry + `IPublisherShape` per pattern | audit Area 1, P2-7 |
| WinForms / weak events | not detected (Evolution, deferred) | `EventHandlerListShape` / `WeakEventShape`, additive | audit P3-1, P3-2 |
| Static sweep | independent module walk, broken dedup | registry-driven, dedup unnecessary | 19.5s; double-count bug |
| Hot path | `GroupAccumulator`, string keys | unchanged | 1.48s = 1.6% |
| Group key | `(string, string, bool)` | unchanged | MT-keying fragments AppDomains |
| Parallel Phase B | excluded | stays excluded | 1.48s parallelizable |
| Root set build | shared cold cost | unchanged (platform concern) | 15.4s, amortized in pipeline |
| OrphanedSubscribers | non-root = orphaned | replaced by `IsDisposedButSubscribed` | definitionally wrong; audit P2-6 |
| Cross-group correlation | none | Phase E: top subscriber types + handler methods | audit P2-1, P2-2 |
| Presentation data | pre-formatted strings in domain model | structured records, formatted only in Phase F | audit #7, #8, #10 |
| Severity scoring | step function, unversioned | continuous curve, `ScoringVersion`-stamped | audit #4, P3-6 |
| Event-name cache | process-lifetime static | registry-owned | cross-dump MT collision |
| .NET Framework delegate layout | silent wrong reads | runtime-version-selected, confidence-flagged | audit #8, P2-8 |
| Cancellation | entry point only | every phase | — |

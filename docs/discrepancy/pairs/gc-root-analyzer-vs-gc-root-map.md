# GCRootAnalyzer vs. GcRootMapCommand (+ GcRootsCommand as a secondary, targeted-mode comparison)

**Scope correction (2026-08-17, after initially publishing this file against only `gc-roots`)**:
`gc-roots` is *not* the right primary comparison — it's a targeted, `--type`-scoped, on-demand
command (`IncludeInFullAnalyze => false`), whereas `GCRootAnalyzer` runs automatically over every
root in the dump as part of a normal full analysis. The actual structural analog is
`GcRootMapCommand`/`GcRootMapAnalyzer` (`gc-root-map`, `IncludeInFullAnalyze => true`) — also
automatic, also heap-wide, also classifies roots by kind. This file now compares against
`gc-root-map` as primary and keeps the original `gc-roots` material as a secondary, clearly-labeled
comparison for the targeted-trace use case, which is still a real and relevant capability on their
side even though it isn't the direct structural match.

| | This tool | Other tool (primary) | Other tool (secondary) |
|---|---|---|---|
| Analyzer/command | `GCRootAnalyzer` | `GcRootMapAnalyzer`/`GcRootMapCommand` (`gc-root-map`) | `GcRootsAnalyzer`/`GcRootsCommand` (`gc-roots`) |
| Report/renderer | `GCRootIntelligenceSectionBuilder.cs` | `GcRootMapReport.cs` | `GcRootsReport.cs` (still not read — genuinely secondary) |
| Domain model | `GCRootDomainResult` / `RootFinding` / `RootPathFinding` | `GcRootMapData` / `RootKindSummary`(theirs) / `RootTypeEntry` | `GcRootsData` / `GcRootTarget` / `GcRootInfo` / `ReferrerInfo` |
| Scope | Automatic, heap-wide, severity-ranked | Automatic, heap-wide, classified by kind | Targeted, `--type`/`--address`, on-demand |
| Runs in `analyze --full`? | Yes (always runs) | Yes (`IncludeInFullAnalyze => true`) | No (`IncludeInFullAnalyze => false`) |

## Analysis

### Data computed

**This tool**: `TotalRoots`, `ByKind` (kind, count, **estimated retained bytes**, % of managed
heap), `TopRootsBySeverity` (`RootFinding`: root kind, root address, field description, target type,
target address, **estimated retained bytes**, severity score — a per-object, severity-ranked view),
`RootPaths` (forward-owned-subgraph type names per top root).

**Other tool (`gc-root-map`)**: `ByKind` (kind name, handle count, stack-root count, **estimated
memory — explicitly shallow, own-size only**, per its own report caption: *"Estimated memory = sum
of target object sizes per root kind (own size only, not retained graph)"*), `TopTypesByKind` (top 5
types per kind by count, with total shallow size), `TotalHandles`, `TotalStackRoots`,
`StackRootsPartial` flag, `TotalHandleMemory`. **No per-object severity ranking, no retained-size
estimate anywhere in this data model** — it's a classification-and-count view, not a "what should I
look at first" ranked view.

**This is a genuine, confirmed analysis-side advantage for this tool on the retained-size
question specifically** — `GCRootAnalyzer` estimates what each top root's target transitively
retains; `gc-root-map` only ever reports the shallow size of directly-rooted objects, explicitly and
honestly labeled as such.

### Root-kind vocabulary: different enumeration source, mostly-overlapping coverage, one real gap each way

**This tool**: `RootIndexReader.KindToString` — `None`, `FinalizerQueue`, `StrongHandle`,
`PinnedHandle`, `Stack`, `RefCountedHandle`, `AsyncPinnedHandle`, `SizedRefHandle`,
`ThreadStaticVar`, `StaticVar` (10 kinds) — sourced from ClrMD's higher-level `heap.EnumerateRoots()`,
which only enumerates things that are actually GC roots by definition (a weak handle is deliberately
excluded, since a weak reference doesn't keep its target alive).

**Other tool (`gc-root-map`)**: enumerates raw `ClrHandleKind` directly — `Strong`, `Pinned`,
`AsyncPinned`, `WeakShort`, `WeakLong`, `Dependent`, `SizedRef`, `RefCounted`, `WeakWinRT` — plus a
separately-counted `ThreadStack` bucket. **Includes `WeakShort`/`WeakLong`/`WeakWinRT` under a "GC
Root Classification" heading even though weak handles are not GC roots** — their own report's
`bullets` text is honest about this (*"WeakShort/WeakLong → weak references; target may still be
alive but not preventing collection"*), so the data isn't mislabeled to the reader, but the section
title's framing is looser than what it actually contains.

**Net, checked precisely rather than assumed**: neither tool has an uncovered *root* kind — this
tool doesn't need a weak-handle case in `GCRootAnalyzer` because weak handles aren't roots (that's
correctly `WeakReferenceAnalyzer`'s job, a separate analyzer in this tool's catalog). `Dependent`
handles are a genuinely GC-root-adjacent concern this tool's `GCRootAnalyzer` doesn't classify
directly — but this tool's `LeakCandidateAnalyzer` already has a dedicated `DependentHandleLeak`
`LeakClass` (confirmed in the Leak-Candidate pair file), so the concept is covered by a different
analyzer boundary, not missing outright. `ThreadStaticVar`/`StaticVar` (this tool's two static-root
kinds) don't have an obviously distinct bucket in `gc-root-map`'s output, though `static-refs`
(`StaticRefsCommand`, not yet deep-dived — see § Pending in [pairs/README.md](README.md)) may cover
this on their side instead.

### Algorithm

**This tool**: single pass over the cached root set (`heapCache.GetOrBuildRoots`), grouped by kind,
each top-severity root additionally BFS-walked for retained-size estimation (bounded, capped by
depth/breadth per `GCRootAnalysisOptions`).

**Other tool (`gc-root-map`)**: single pass over `ctx.Runtime.EnumerateHandles()` (all kinds at
once, one enumeration, not per-kind), plus a **separate, time-bounded** pass over
`thread.EnumerateStackRoots()` per thread — capped not by a node/depth budget the way every
BFS-based cap in this comparison set is, but by a **wall-clock time budget** (a literal
`StackRootTimeBudgetMs` constant, confirmed = 10 seconds from the report's own caption "partial — 10
s budget"). This is a genuinely different cost-control philosophy: this tool's caps are all
structural (node count, BFS depth); this is the first wall-clock-based cap found in either codebase
during this comparison series — worth noting as a real, distinct engineering choice, not better or
worse on its face, but different (a wall-clock cap adapts to machine speed; a node-count cap doesn't,
but is exactly reproducible across runs on different hardware).

### Performance / complexity

**This tool**: reuses the shared, cached root set — no independent handle/stack enumeration if the
disk index (`RootIndex.bin`) is already built for this run.

**Other tool**: `EnumerateHandles()` is its own independent pass (not confirmed shared with
`gc-roots`'s or any other command's handle enumeration in this codebase) — each of the 34 embedded
commands in a `--full` run that needs handle data appears to do its own pass, based on what's been
read across this comparison series so far; not confirmed whether a shared handle cache exists
elsewhere on their side.

### Correctness caveats

Covered under Data computed — `gc-root-map`'s shallow-only memory figure is honestly labeled, not a
correctness bug, just a narrower claim than this tool's retained-size estimate.

### Configuration & tunability

**This tool**: `GCRootAnalysisOptions` bound to the persistent config file (per the original version
of this file) — applies to `GCRootAnalyzer` including its retained-size BFS caps.

**Other tool**: `gc-root-map`'s `Help` text lists only `-o/--output` and `-h/--help` — **no tunable
options at all**, same as `deadlock-detection`. The 10-second stack-root time budget is a hardcoded
constant, not exposed as a flag or config value.

### Trend / multi-dump behavior

Mechanism confirmed via [analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md)
§ Cross-cutting: what "trend-analysis" actually compares. Pair-specific conclusion, corrected now
that the primary comparison is `gc-root-map`, not `gc-roots`:

**This tool**: `GCRootTrendComparer` (metrics listed in the original version of this file) produces
real `MetricDelta`s across dumps.

**Other tool**: `gc-root-map` **does** run in `analyze --full` (`IncludeInFullAnalyze => true`,
unlike `gc-roots`), so unlike the original version of this file's conclusion, its data **is** present
in a `trend-analysis --full` run — but, per the confirmed general mechanism, only as N full,
independently-rendered "GC Root Classification" sections (one per dump), never as a computed delta.
`gc-roots` remains fully out of scope for any trend run at all, for the reason already established
(not part of `--full`).

### Validation

**This tool**: no dedicated test file for `GCRootAnalyzer` (established previously).

**Other tool**: no dedicated test file found for `GcRootMapAnalyzer` either (`Glob` for
`*GcRootMap*.cs` under `DumpDetective.Tests` returns nothing) — same confirmed gap on both sides as
`gc-roots`.

## Report

### Data shown

**This tool**: as established previously — kind/count/retained-bytes/%-of-heap table, severity-
ranked top-roots table with root kind/addr/field/target type/addr/retained/severity all inline, a
finalizer-roots sub-table, and typed `RootPathGroups`.

**Other tool (`gc-root-map`)**: total handles/stack roots/handle memory as key-values, a "Root kind
breakdown" donut chart, a by-kind table (root kind/handles/stack roots/est. memory), and a
collapsible ("Top Types by Root Kind") section per kind — `Strong`/`Pinned` expanded by default,
others collapsed — each showing top-5 types by count with total shallow size.

### Presentation style

**This tool**: `BuildConfidenceBand` + narrative text, no chart.

**Other tool**: `sink.Explain(what, why, bullets, impact)` at the top (note: no `action` parameter
in this specific call, unlike `DeadlockReport`'s use of all five — `Explain`'s parameters are all
optional, so different call sites use different subsets), then a donut chart, a table, and a
collapsible-per-kind drill-down section. This is presentation-richer than this tool's section for
this specific pair — a chart *and* progressive disclosure (collapsible sections), neither of which
this tool's `GCRootIntelligenceSectionBuilder` uses, despite `ChartBlock` and
`CollapsibleSectionBeginBlock`/`EndBlock` both existing as available primitives in this tool's own
`SectionBlock` model (confirmed in
[analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md) §1) — **this is a
missed-opportunity gap, not a missing-capability gap**: the primitives exist in this codebase, this
particular section builder just doesn't use them.

### Severity/confidence communication

**This tool**: quantitative confidence score plus per-root severity score (established previously).

**Other tool (`gc-root-map`)**: no severity or confidence concept at all — purely descriptive
counts/sizes, consistent with having no per-object ranking in the data model either (see § Data
computed).

### Actionability of guidance

**This tool**: `GCRootFindingGenerator`'s four named findings, each with specific remediation
(established previously).

**Other tool (`gc-root-map`)**: the `Explain` block's `bullets` give kind-specific interpretation
guidance (*"Strong → explicit `GCHandle.Alloc(obj)` or static fields — most common leak source"*,
*"Pinned → IOCompletion / Marshal.AllocHGlobal patterns — prevent heap compaction"*) — this is
genuinely good, specific *interpretive* guidance for reading the table, though it's static
explanatory text attached once to the whole section, not a per-finding, threshold-triggered
recommendation the way this tool's `GCRootFindingGenerator` produces one finding per breached
threshold.

### Visualizations

**This tool**: none for this analyzer (established previously — still true).

**Other tool (`gc-root-map`)**: a donut chart (roots by kind) — a genuine visualization this specific
report has that this tool's equivalent section doesn't, on top of the collapsible-sections gap noted
above.

### Cross-analyzer / executive-summary participation

**This tool**: generic, confirmed (established previously).

**Other tool**: `gc-root-map` runs in `--full`, so — per the general `HealthScorer`-scoping finding —
its classification data almost certainly doesn't reach the top-level health score either, since none
of `GcRootMapData`'s fields are `DumpSnapshot` fields. Not independently re-verified field-by-field,
consistent with the caveat already stated for other pairs.

### Drill-down / cross-referencing

**This tool**: already good for the direct root→target relationship (established previously); no
link to `gc-root-map`-equivalent kind-classification data (this tool doesn't have a separate
kind-classification report to link to — `ByKind` is already part of the same section).

**Other tool**: the collapsible per-kind "top types" drill-down is a real, useful progressive-
disclosure pattern within the *same* section — a different kind of drill-down than a root-to-target
chain (this is "expand to see more of the same classification," not "follow a reference to a
different piece of evidence"), but worth recognizing as effective UI for a wide, kind-partitioned
dataset.

### Machine-consumability / HTML interactivity / Output format parity

Consistent with the general findings established for other pairs — no pair-specific deviation
identified for `gc-root-map` specifically.

## Bottom line

**Analysis**: this tool is ahead on the core question `gc-root-map` explicitly declines to answer
(retained, not just shallow, bytes) and covers real GC roots (not weak handles) under a correctly-
scoped analyzer boundary, with the `Dependent`-handle case already covered by a different analyzer
(`LeakCandidateAnalyzer`). The other tool's wall-clock-based stack-root cap is a legitimately
different, not worse, engineering choice. Neither side has dedicated test coverage for this pair.

**Report**: the other tool is ahead here specifically — a donut chart, collapsible per-kind
drill-down, and kind-specific interpretive guidance are all real, useful presentation choices this
tool's equivalent section doesn't use, *despite already having the underlying primitives
(`ChartBlock`, `CollapsibleSectionBeginBlock`/`EndBlock`) available in its own codebase* — making
this the clearest "missed opportunity, not missing capability" finding in this comparison series so
far.

## Recommendations

- **Cheap, do now**: add a donut/ranked-bar `ChartBlock` for the "GC root kinds" table in
  `GCRootIntelligenceSectionBuilder` — the chart primitive already exists in this codebase, this is
  wiring, not new infrastructure.
- **Cheap, do now**: consider collapsible per-kind sections if/when a "top types per root kind"
  breakdown is added to this tool's `GCRootDomainResult` — `gc-root-map`'s version of this view is
  genuinely useful and this tool's data model doesn't currently have an equivalent aggregation at
  all (not just a missing render — the underlying `TopTypesByKind`-equivalent doesn't exist in
  `GCRootDomainResult`).
- Still pending: read `GcRootsReport.cs` and `StaticRefsCommand`/`StaticRootLeakDetector` (the latter
  already next on the worklist) to settle whether `ThreadStaticVar`/`StaticVar` have a clean analog
  on their side.

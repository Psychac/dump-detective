# Per-Pair Deep Dives

One file per analyzer/command pair, each a **full pair diff** — both analysis-approach and
report-presentation for that specific pair, in equal depth, so a reader can act on the pair without
needing to cross-reference the split top-level docs (those still exist for findings that are
genuinely cross-cutting — e.g. the `Explain`-primitive and chain-rendering architecture findings —
but a per-pair fact belongs in that pair's file, not just in the cross-cutting doc). Every claim in
every file here is grounded in a direct source read on both sides — no file in this folder should
ever cite either tool's README/docs as evidence.

Parent docs: [analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md) ·
[analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md)

## Required structure for every pair file

Every file in this folder — new or existing — must cover both halves below in comparable depth.
Skipping the report half (or reducing it to a one-paragraph note) is exactly the gap this template
exists to prevent; the first four pairs originally under-covered it and have since been rewritten to
this structure.

1. **Header table** — analyzer/command, report/renderer, domain-model class names, on both sides.
2. **Analysis** (what's computed) — eight subsections:
   - *Data computed* — what fields/metrics/records actually exist in the domain model, on both
     sides.
   - *Algorithm* — the actual technique (heuristic, graph algorithm, exact computation, sampling),
     not just the name of the analyzer.
   - *Performance/complexity* — heap passes required, caps/limits applied, whether it's O(n) over
     the heap or something worse, whether it reuses shared indices/caches or does its own scan.
   - *Correctness caveats* — known approximations, sampling bias, or scenarios where the number can
     be wrong, confirmed from source comments or code structure, not guessed.
   - *Configuration & tunability* — are the caps/thresholds/depths this analyzer uses hardcoded
     constants, per-invocation CLI flags, or bound to a persistent config file a user can edit
     without a rebuild — confirmed by finding where (or whether) the option is actually wired up,
     not by finding the option's field declaration alone.
   - *Trend / multi-dump behavior* — do not stop at "a `TrendComparer` class is registered" or "the
     trend command's constructor takes the same command list" — that was tried for the first four
     pairs and produced vague, unconfirmed language, exactly the failure mode this template exists
     to prevent (see [analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md)
     § Cross-cutting: what "trend-analysis" actually compares, added after this was caught). Instead,
     name the actual metric keys/fields this pair contributes on each side (read the `ExtractMetrics`/
     `Compare` method body, or equivalent, and list what it emits — same rigor as the *Data computed*
     subsection), then trace where those values actually flow: does the top-level trend/diff
     mechanism receive this pair's *specific* domain object, or only a narrower shared snapshot type
     that this pair's data was never added to? A command can be re-run once per dump (so its data is
     technically present in a multi-dump report) while still never being diffed against itself
     across dumps — those are different claims, and the file should say which one is true, not both
     vaguely.
   - *Validation* — what tests (unit, integration, discrepancy/parity) exist on either side that
     would actually catch a regression in the specific claims made in this file; state plainly when
     a claim has no test backing it on either side.
3. **Report** (what's shown and how) — six subsections:
   - *Data shown* — which fields from the domain model actually reach the rendered report, and
     which are computed but never surfaced (a common gap on both sides).
   - *Presentation style* — narrative/explain blocks vs. tables vs. inline chains vs. cross-reference
     pointers; how the report guides the reader to a next action.
   - *Severity/confidence communication* — quantitative confidence score with named caveat
     deductions vs. qualitative alert level (Critical/Warning/Info) with narrative caveats — state
     which model each side actually uses for this pair, not just that the concept exists.
   - *Actionability of guidance* — is the "what to do next" text specific to the evidence in front
     of the reader (cites the actual type/value/pattern found) or generic boilerplate that would
     read the same regardless of what was found.
   - *Visualizations* — chart/gauge/graph primitives actually used for this pair's data, checked
     against what each tool's rendering primitives actually support (not assumed).
   - *Cross-analyzer / executive-summary participation* — does this specific pair's finding actually
     surface in either tool's top-level rollup (health score, executive summary, cross-metric
     narrative) when run as part of a full analysis, or does it only exist within its own section —
     confirmed by tracing whether the rollup mechanism structurally can see this analyzer's output at
     all, not assumed from either tool's architecture in general.
   - *Drill-down / cross-referencing* — does the report point to (or better, inline) related data
     from other analyzers/commands; is that pointer useful or a dead end.
   - *Machine-consumability* — could a script gate a build/alert on this pair's exact data point
     from the JSON (or other structured) output on either side without parsing prose, and is the key
     it would key off of stable (a short symbolic name) or coupled to the human-display string.
   - *HTML interactivity* — is this pair's specific rendered table/chart sortable/filterable/
     collapsible in the HTML output on either side, or static — confirmed per pair, not assumed from
     a general capability claim.
   - *Output format parity* — does this pair's data survive equally well into JSON/HTML/Markdown/Text
     (or their Bin/Json/Html/Text/Markdown sinks) on both sides, or does richness get lost in some
     formats.
4. **Bottom line** — one paragraph each for analysis and report, stated as who's ahead and why.
5. **Recommendations** — concrete, scoped suggestions, distinguishing "cheap, do this" from
   "requires new algorithmic work."

Some subsections above have already produced findings that turned out to be *identical across every
pair checked so far* (e.g. whether the top-level executive summary can see this pair's output at
all) — where that's confirmed to be architectural rather than pair-specific, state the pair-specific
conclusion in the pair file (so it's still a complete, standalone diff) but keep the underlying
mechanism explanation short and point back to the cross-cutting doc for the full mechanism writeup,
rather than re-deriving it from scratch in every file.

## Done

All four pairs below have been rewritten to the full template (both Analysis and Report halves, all
subsections) as of 2026-08-17.

| Pair | File | One-line verdict |
|---|---|---|
| `DominatorAnalyzer` vs. `object-inspect`/`memory-leak` retained-size | [dominator-analyzer-vs-object-inspect.md](dominator-analyzer-vs-object-inspect.md) | Analysis: we're ahead (real Lengauer-Tarjan vs. explicitly-approximate BFS; also stronger test coverage — a real-dump discrepancy test vs. none found on their side). Report: they're ahead (working inline chain cross-reference vs. bare hex address), but this tool's confidence/severity layering is more granular. |
| `LeakCandidateAnalyzer` vs. `memory-leak` | [leak-candidate-analyzer-vs-memory-leak.md](leak-candidate-analyzer-vs-memory-leak.md) | Analysis: mixed — we're ahead on classification structure (8-class taxonomy), they're ahead on tunability (real `--min-count`/`--include-system` flags vs. our hardcoded, unconfigurable scoring constants) and likely validation (real-scenario test vs. unit-only). Report: they're ahead structurally, but our per-candidate advice text is the most specific found in any pair. |
| `LockGraphAnalyzer` vs. `deadlock-detection` | [lock-graph-vs-deadlock-detection.md](lock-graph-vs-deadlock-detection.md) | Analysis: they're ahead (real wait-for-graph + DFS cycle detection vs. our flat co-occurrence heuristic; also a real-scenario deadlock test vs. zero test coverage on our side) — but their "confirmed" label overclaims in the multi-contested-lock case. Report: closer to even — our confidence scoring and honest self-disclosed caveats are a real strength. |
| `GCRootAnalyzer` vs. `gc-root-map` (+ `gc-roots` as secondary/targeted-mode) | [gc-root-analyzer-vs-gc-root-map.md](gc-root-analyzer-vs-gc-root-map.md) | **Scope-corrected 2026-08-17**: `gc-root-map`, not `gc-roots`, is the real structural analog (both automatic, heap-wide, run in `--full`). Analysis: we're ahead (real retained-bytes estimate vs. their explicitly-shallow-only "own size" figure). Report: they're ahead — donut chart + collapsible per-kind drill-down, despite this codebase already having those exact primitives (`ChartBlock`, `CollapsibleSectionBeginBlock`) unused in this section — the clearest "missed opportunity, not missing capability" finding so far. `GcRootsReport.cs` still not read (secondary comparison only). |

## Pending — prioritized worklist

Carried over from [analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md)
§ Suggested next pairs, reordered now that Lock Graph and GC Root (including its `GcRootMapCommand`
follow-up) are done:

1. **`WeakReferenceAnalyzer` vs. `weak-refs`** — this repo has a long, multi-session improvement
   history on this analyzer already (P0 through P2 items in `MEMORY.md`); worth checking whether
   that investment produced a genuine edge.
2. **`StringAnalyzer` vs. `string-duplicates`** — similarly went through a full P0+P1 pass
   (`SampledUniquePatterns`, `VeryLongStringFinding`, field-ownership scan); worth checking whether
   the other tool's duplicate-string detection covers the same ground.
3. **`EventLeakAnalyzer` vs. `event-analysis`** — event-handler leaks are a well-defined,
   comparable problem (publisher/subscriber graph) similar in shape to the Lock Graph pair.
4. **`TimerLeakAnalyzer` vs. `timer-leaks`**.
5. **`FinalizableObjectAnalyzer` vs. `finalizer-queue`**.
6. **`StaticRootLeakDetector` vs. `static-refs`** — also worth settling the open question from the
   GC-Root pair about whether `ThreadStaticVar`/`StaticVar` root kinds have a clean analog here.
7. **`WcfChannelAnalyzer` vs. `wcf-channels`** — this repo already did dedicated P0 work on WCF
   channel state/endpoint extraction (`MEMORY.md`); worth checking if that's now ahead of the other
   tool's equivalent.
8. **`ThreadAnalyzer`/`ThreadStackClusterAnalyzer` vs. `thread-analysis`**.
9. **`AsyncStateMachineAnalyzer`/`AsyncTaskAnalyzer` vs. `async-stacks`** — this repo also has an
   extensive improvement history here (P0 through P3 items in `MEMORY.md`); good second candidate
   for an "did our investment actually pay off vs. theirs" check, same rationale as items 1–2.
10. **`CrashAnalyzer` vs. `exception-analysis`**.
11. **`DbConnectionAnalyzer` vs. `connection-pool`**.
12. **`HttpObjectAnalyzer` vs. `http-requests`**.
13. **`LohFragmentationAnalyzer` vs. `large-objects`**, **`GCHandleAnalyzer` vs.
    `handle-table`/`pinned-objects`** — lower priority; likely straightforward heap-stat comparisons
    without much algorithmic depth to compare, based on the mapping table alone.

Not planned as pairs (confirmed one-sided gaps already noted in
[capability-comparison.md](../capability-comparison.md)/
[analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md), nothing to
compare):
`SegmentReservationAnalyzer`, `ArrayAnalyzer`, `BoxingAnalyzer`, `JitAnalyzer` (our side only) ·
`ClosureCaptureCommand`, `NativeInteropCommand`, `HeapFragmentationCommand` (their side only) · all
27 trace-side commands (no counterpart at all).

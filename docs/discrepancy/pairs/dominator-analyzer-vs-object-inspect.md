# DominatorAnalyzer vs. object-inspect / memory-leak's "Top Retainers"

| | This tool | Other tool |
|---|---|---|
| Analyzer/command | `DominatorAnalyzer` (`src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs`) | `ObjectInspectCommand` (`object-inspect`) + `MemoryLeakAnalyzer`'s BFS-retained sizing (surfaced via `MemoryLeakReport.RenderTopRetainers`) |
| Report/renderer | `DominatorSectionBuilder.cs` | `ObjectInspectRenderer.cs` (single-object tree) + `MemoryLeakReport.RenderTopRetainers` |
| Domain model | `DominatorDomainResult` / `TypeSnapshot` | `BfsIndexCache`/`BfsIndexBuilder.ComputeRetainedForFields` (no dedicated command-level record — the retained size is a field on `SuspectRow`) |

**Scope correction before comparing further**: `object-inspect` was read in full for this pass and
turns out to be a materially different shape of tool than `DominatorAnalyzer` — it's a
single-object, recursive field-tree inspector (`--address`, `--depth`, per-field retained-size
annotation), closer to this tool's *missing* "targeted single-object drill-down" capability
(already flagged in [capability-comparison.md](../capability-comparison.md)) than to a
top-N-suspects-by-retained-bytes ranking. The genuinely comparable command for Dominator's own
job — "rank types by estimated retained bytes across the whole heap" — is `MemoryLeakReport`'s "Top
Retainers" sub-table, which is what most of this file compares against. `object-inspect` is
referenced only for its retained-size *algorithm* (`BfsIndexBuilder.ComputeRetainedForFields`, the
same BFS machinery `memory-leak`'s retained sizing uses), not as a like-for-like report comparison.

## Analysis

### Data computed

**This tool**: `TopDominatorTypes` (`TypeSnapshot`: type name, count, Gen2 count, shallow bytes, LOH
bytes, `EstimatedRetainedBytes`, average size, `WasCapped`, sample address), `TopHighlyReferencedObjects`,
`TopRetentionTypes`, `FanInHistogram`, plus (since the 2026-08-16 Lengauer-Tarjan work)
`ExactRetainedBytesByTypeName` — a genuinely exact per-type retained-bytes figure when the exact
tree computation succeeds, not just an estimate.

**Other tool**: `SuspectRow.RetainedSize` (a single `long`, no breakdown of how it was computed) plus
an `Estimated: bool` flag surfaced at the `BfsIndexCache.ComputeRetained`/`ComputeRetainedForFields`
API boundary. No Gen2/LOH breakdown attached to the retained-size figure itself (those exist as
separate fields on `SuspectRow`, not cross-tabulated with retained size the way this tool's Gen2/LOH
dominator sub-table does).

### Algorithm

**This tool**: a real Lengauer-Tarjan dominator-tree computation (`LengauerTarjan.cs`,
`DominatorTreeComputer.cs`) over the whole reachable heap, gated behind
`RetentionOptions.EnableExactDominatorTree` (default on), falling back to a bounded-BFS heuristic
(`BoundedGraphWalk.ComputeExclusiveRetained`) on cap-exceeded or failure. This produces mathematically
exact retained-byte counts when it succeeds — the standard, correct algorithm for "what does this
object exclusively keep alive," not an approximation.

**Other tool**: `BfsIndexBuilder.ComputeRetainedForFields` — a BFS traversal per field, over the
pre-built 3-pass BFS index (`BfsIndexCache`), sampled/scaled when the object graph is large (per
`MemoryLeakReport.RenderTopRetainers`'s own caption: *"Retained size = BFS-computed: total bytes
freed if all instances were collected (sampled, then scaled)"*). This is explicitly an approximation
by design, not a fallback path taken only when something else fails — confirmed via a whole-repo
`grep -rli "dominator|idom|lengauer"` across `Rohit_DumpDetective` returning **zero matches**: there
is no dominator-tree computation anywhere in that codebase to fall back *from*.

### Performance / complexity

**This tool**: the exact Lengauer-Tarjan path is a known, bounded-complexity graph algorithm
(near-linear in practice) but requires materializing the reachable-object graph structure
(`ReachableGraph`, `ReachableGraphWalker`) — gated by a memory budget per the audit header, falling
back rather than running unbounded. The BFS fallback is bounded by the same depth/breadth caps every
other `BoundedGraphWalk` consumer in this codebase uses.

**Other tool**: BFS per field, bounded by `--retained-cap` (unlimited by default for `object-inspect`,
but `memory-leak`'s own retained-sizing presumably has its own cap — not independently confirmed in
this pass) — cheaper per-call than a full dominator-tree build, but doesn't produce a reusable
structure the way a dominator tree does; each retained-size query is its own bounded walk rather than
a lookup into a precomputed tree.

### Correctness caveats

**This tool**: the exact path is exact by construction when it succeeds; the fallback path carries
the same caveats as every other `BoundedGraphWalk` estimate in this codebase (capped depth/breadth
means distant retaining objects can be missed, producing a lower-bound estimate).

**Other tool**: always an estimate, by the algorithm's own design — the "sampled, then scaled"
language in `MemoryLeakReport.RenderTopRetainers`'s caption is an honest, explicit acknowledgment
that this isn't attempting exactness at all, distinct from this tool's "exact unless capped" framing.

### Configuration & tunability

**This tool**: `RetentionOptions` (`EnableExactDominatorTree`, `MaxBfsDepth`, `MaxBreadth`, etc.) is
bound to the persistent JSON config file — confirmed via `ConfigurationResolver.BuildMemoryLeakFromConfig`
(the method name is a historical artifact of `DominatorAnalyzer` having absorbed the former
`RetentionAnalyzer`, per the audit's Area 1 note — it returns `RetentionOptions`, not anything
memory-leak-specific on this tool's side) and its `[JsonSerializable]` registration.

**Other tool**: `object-inspect` exposes real per-invocation CLI flags for the retained-size
machinery specifically — `--retained`/`-r` (opt-in, since it's slower), `--retained-cap`
(default unlimited), `--no-cache`/`--no-save` (BFS index cache lifecycle control). `memory-leak`
itself doesn't appear to expose an equivalent retained-size cap flag in what was read for this pass
— not fully confirmed either way.

### Trend / multi-dump behavior

Mechanism fully confirmed by tracing the actual call path on both sides — see
[analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md) §
Cross-cutting: what "trend-analysis" actually compares. Pair-specific conclusion:

**This tool**: `DominatorTrendComparer` emits seven base metrics per dump (`dominator.candidates`,
`dominator.analyzed`, `dominator.retained.bytes`, `dominator.retention.pressure.ratio`,
`dominator.top.count`, `leak.highly.referenced`, `leak.highly.referenced.bytes`) plus per-type
metrics (`dominator.type.bytes`, `dominator.type.retained.bytes`, `leak.retention.type.bytes`,
`leak.retention.type.count`, all keyed by type name) — confirmed by this repo's own prior work (see
`MEMORY.md`'s P2-5 entry) and directly re-read from source for this pass. Each produces a real
`MetricDelta` between any two dumps, so a specific type's retained-bytes regression is directly
diffable, not just eyeballed.

**Other tool**: `object-inspect` requires `--address` and is not part of `analyze --full`
(`IncludeInFullAnalyze => false`), so it isn't among the commands `trend-analysis --full` even
re-runs per dump — same "structurally can't appear in a trend run at all" situation as `gc-roots`.
`memory-leak` *does* run in `--full` (`IncludeInFullAnalyze => true`), so its `SuspectRow.RetainedSize`
values are present in every per-dump section of a `trend-analysis --full` run — but, per the
now-confirmed general mechanism, only as N full, independently-rendered "Memory Leak" sections
(one per dump), never as a computed delta: `TrendAnalysisReport.RenderTrend` only ever receives
`List<DumpSnapshot>`, and `SuspectRow.RetainedSize` isn't a `DumpSnapshot` field. A reader tracking
whether a specific type's retained size is growing across dumps has to open each dump's own
"Memory Leak" section and compare the numbers themselves.

### Validation

**This tool**: strong, confirmed coverage — `DominatorFindingGeneratorTests.cs`,
`DominatorTreeComputerTests.cs`, `DominatorTreeIndexTests.cs`, `DominatorSectionBuilderTests.cs`,
and, notably, `DominatorAnalyzerExactTreeRealDumpTests.cs` under `Integration/CacheDiscrepancies/` —
a real-dump discrepancy test specifically validating the exact-tree path, comparable in rigor to the
other tool's scenario-based real-dump tests seen elsewhere in this comparison.

**Other tool**: no dedicated test file found for `object-inspect`'s retained-size path or
`BfsIndexCache.ComputeRetained` specifically (`Glob` for `*ObjectInspect*Test*.cs`/`*BfsIndex*Test*.cs`/
`*Retained*Test*.cs` under `DumpDetective.Tests` returns nothing). `MemoryLeakTest.cs` exists and is
presumably scenario-based like `DeadlockTest.cs`, but it's not confirmed whether that scenario
specifically exercises/asserts on the retained-size sampling path rather than just the count/size
suspect lists. **This tool has stronger, more specific validation for this pair's core algorithm**
— a reversal of the Lock-Graph/Deadlock pair's validation asymmetry.

## Report

### Data shown

**This tool**: `candidate_count`, `analyzed_count`, `total_retained_est`, `retention_pressure_ratio`,
`max_bfs_breadth`/`max_bfs_depth`, `highly_referenced_objects`, `top_retained_total`,
`skipped_ref_addresses` as key metrics; four compact tables (top dominator suspects, per-mille
impact, Gen2/LOH dominator suspects, highly-referenced objects, top retention types, fan-in
histogram) — the most tables of any pair examined so far.

**Other tool**: `MemoryLeakReport.RenderTopRetainers` shows type/count/own size/retained size/ratio/
Gen2 presence for the top 20 types with `RetainedSize > 0`, deduplicated across count- and
size-suspect lists.

### Presentation style

**This tool**: `BuildConfidenceBand` plus explicit narrative (`"Retained bytes are estimated with a
bounded BFS over N suspects..."`), and — since the Lengauer-Tarjan work — a conditional narrative
line specifically for the Gen2/LOH sub-table when the exact path succeeded (*"Gen2/LOH retained
bytes below are exact (Lengauer-Tarjan dominator tree) for this run"*), a good, specific instance of
communicating *which numbers in this exact table* are exact vs. estimated — most other pairs don't
distinguish precision at this granularity.

**Other tool**: `sink.Explain(what, why, bullets, action)` framing retained size as answering "why
does own size understate the real cost," then a single table with a caption stating the sampling
caveat inline.

### Severity/confidence communication

**This tool**: quantitative — `ConfidenceScoring.Compute(0.75, ...)` with named deductions
(object-scan-capped, reference-counting-skipped, skipped-reference-addresses), rendered as the
confidence band. `DominatorFindingGenerator` additionally produces severity-tiered `InsightFinding`s
(Critical ≥500 MB retained, Warning ≥100 MB; Critical ≥10 highly-referenced objects) — both a
section-level confidence score *and* per-finding severity, more layered than most other pairs.

**Other tool**: no per-row severity/alert on the "Top Retainers" table itself — the surrounding
`RenderFindings` alerts (Critical/Warning based on size/Gen2/LOH thresholds) are a separate, earlier
section keyed off `SuspectRow` fields generally, not specifically the retained-size column.

### Actionability of guidance

**This tool**: `DominatorFindingGenerator`'s recommendation text is comparatively generic —
*"Use retention section root paths confirm why type remains live"* — a cross-reference pointer
rather than evidence-specific advice (contrast with the Leak-Candidate pair's per-`LeakClass` advice,
which is much more specific). This is a real, confirmed asymmetry *within this tool's own analyzer
set*, not just against the other tool.

**Other tool**: `RenderFindings`' advice text is branch-specific (survived-Gen2 vs. not, LOH vs.
not) but doesn't reference the retained-size number specifically — similar genericity level to this
tool's dominator advice.

### Visualizations

Not confirmed for either side's rendering of this specific data beyond what's already established:
this tool's `ChartBlock` types don't appear in the `DominatorSectionBuilder` excerpt read (table-only
for this analyzer); `MemoryLeakReport`'s donut/stacked-bar charts (confirmed elsewhere in this
comparison) are attached to the heap-snapshot section, not specifically to the retained-size table.

### Cross-analyzer / executive-summary participation

**This tool**: `DominatorFindingGenerator`'s findings flow through the generic pipeline into the
executive summary, same as every analyzer.

**Other tool**: `object-inspect` never runs in `--full` (`IncludeInFullAnalyze => false`), so it's
out of scope for the executive summary the same way `gc-roots` is. `memory-leak` does run in
`--full`, but per the general finding in
[analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md) §6,
`HealthScorer.Score` only reads `DumpSnapshot` fields directly — `SuspectRow.RetainedSize` (computed
inside `MemoryLeakAnalyzer`, not a base heap-walk field) is very unlikely to be visible to the
top-level health score, though this wasn't independently re-verified field-by-field in this pass.

### Drill-down / cross-referencing

**This tool**: this is the pair's most-covered known gap — `DominatorSectionBuilder` renders only
`$"0x{type.SampleAddress:X}"`, no chain, despite `RootPathFinder`/`RootPathGroup` existing elsewhere
in this codebase (`GCRootIntelligenceSectionBuilder`). Already tracked in
[dominator-analyzer-audit.md](../../analysis/phase1/dominator-analyzer-audit.md) Audit Area 8.

**Other tool**: `RenderTopRetainers`'s own text explicitly tells the reader to go to "Step 4" (the
box-drawn root-chain section) for the retaining root — a working cross-reference within the *same*
report, to a section that *does* have the chain data (`MemoryLeakAnalyzer.BuildChainBFS`), unlike
this tool's dominator table, which has no working chain anywhere in the same document for its
sample address.

### Machine-consumability

Consistent with the general finding: this tool's `KeyMetrics` dictionary uses stable snake_case keys
(`total_retained_est`, `retention_pressure_ratio`); the other tool's JSON preserves the full
`ReportDoc` structure but keyed by display strings. No pair-specific deviation found.

### HTML interactivity

Consistent with the general parity finding (both default tables to sortable columns); not
independently re-verified table-by-table for this pair.

### Output format parity

Not independently re-verified for this pair in this pass.

## Bottom line

**Analysis**: this tool is ahead — a real, exact dominator-tree algorithm vs. an explicitly
approximate, sampled-and-scaled BFS estimate, confirmed absent as a concept anywhere in the other
tool's codebase — and, newly confirmed in this pass, this tool also has meaningfully *stronger* test
coverage for this specific algorithm (a real-dump discrepancy test for the exact-tree path vs. no
dedicated test found for the other tool's retained-size sampling).

**Report**: the other tool is still ahead on the core "why is this alive" question via its working
in-report chain cross-reference, but this tool's confidence/severity layering is more granular (a
section-level confidence score *and* per-finding severity *and*, since the Lengauer-Tarjan work, a
specific "these numbers are exact, these are estimated" narrative distinction within one table) —
and this tool's own advice text for this analyzer is comparatively more generic than its own
Leak-Candidate analyzer's advice, an inconsistency worth fixing independent of the cross-tool
comparison.

## Recommendations

- Carried forward from [dominator-analyzer-audit.md](../../analysis/phase1/dominator-analyzer-audit.md)
  Audit Area 8: build the inline root-chain rendering for the Gen2/LOH sub-table — this remains the
  single highest-value fix for this pair.
- **New, cheap**: rewrite `DominatorFindingGenerator`'s recommendation text to be as specific as
  `LeakCandidateAnalyzer`'s `LeakExplainer` — this is an internal consistency gap between two of this
  tool's own analyzers, not something that requires studying the other tool at all.
- **New, cheap**: confirm whether `trend-analysis`/`HealthScorer` on the other side actually surfaces
  `memory-leak`'s retained-size regressions — if confirmed absent (as the general architecture
  suggests but wasn't verified field-by-field here), that's one more concrete point in this tool's
  favor for the trend/executive-summary dimension.

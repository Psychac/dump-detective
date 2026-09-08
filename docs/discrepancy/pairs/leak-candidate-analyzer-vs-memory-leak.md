# LeakCandidateAnalyzer vs. MemoryLeakCommand

| | This tool | Other tool |
|---|---|---|
| Analyzer/command | `LeakCandidateAnalyzer` | `MemoryLeakAnalyzer`/`MemoryLeakCommand` (`memory-leak`) |
| Report/renderer | `LeakAnalysisSectionBuilder.cs` | `MemoryLeakReport.cs` |
| Domain model | `LeakCandidateDomainResult` / `LeakCandidateRecord` / `LeakClass` | `MemoryLeakData` / `SuspectRow` / `AccumulationPatternData` / `MemoryRootChain` |

## Analysis

### Data computed

**This tool**: `TotalCandidates`, `CandidatesByClass` (count per `LeakClass`), `TopCandidates`
(`LeakCandidateRecord`: type, suspicion score, severity, classification, total size, instance count,
Gen2%, root kind, is-finalizable, is-container, reference-field ratio), `HeuristicOnly` flag.

**Other tool**: `AllTypes` (full heap-stat table), `CountSuspects`/`SizeSuspects` (`SuspectRow`: name,
count, size, generation, Gen2/LOH count+size, `RetainedSize`, `LeakProbability` 0–100), heap totals
by generation, `RootChains` (`MemoryRootChain`: type, count, total size, sample chains with per-step
`ChainStep`), and `AccumulationPatternData` (five hardcoded pattern categories: strings, byte
arrays, collections, delegates, tasks — each with count/size and a type breakdown for collections/
delegates/tasks specifically).

The other tool's data model is broader in surface area (it includes a `LeakProbability` score per
suspect *and* the multi-hop root chains *and* the pattern-category breakdown in one record set) —
this tool's `LeakCandidateRecord` is narrower but each record carries a definitive classification
(`LeakClass`) the other tool's `SuspectRow` doesn't have an equivalent field for.

### Algorithm

**This tool**: classifies every candidate into one of eight `LeakClass` values (`StaticRetention`,
`EventLeak`, `CacheLeak`, `ThreadLocalLeak`, `FinalizerRetention`, `GCHandleRetention`,
`DependentHandleLeak`, `Unknown`) via `GetSeverity`/classification logic in `LeakCandidateAnalyzer`,
then scores each with additive point rules rendered directly in the report text: "+30 for Gen2-heavy
(>80%), +20 for >100 MB shallow size, +15 for finalizable types with >1,000 Gen2 objects, +10 each
for static-rooted, pinned, and dependent-handle candidates, +5 for container-like types, +5 for
reference-heavy shapes, +5 for delegate/event-style types."

**Other tool**: `LeakProbability` (0–100) — computed "from Gen2/LOH ratio, count magnitude, retained
size, and accumulation pattern heuristics" per its own doc comment (`SuspectRow.LeakProbability`) —
a single blended score, not attributed to a named category. The five `AccumulationPatternData`
categories are separately, independently threshold-checked (string count, byte-array LOH size,
collection total size, delegate count, task count) — a fixed set of pattern *detectors*, run in
parallel with the suspect-scoring path, not classification labels attached to individual suspects.

**Net difference**: this tool answers "which named leak pattern is this specific type" (a
classification); the other tool answers "how likely is this specific type to be a leak" (a
probability) *and separately* "which of these five known patterns shows up anywhere in this heap"
(pattern detection, not tied to a specific suspect). These aren't directly substitutable — a
`LeakProbability` score doesn't tell you *why*, but a `LeakClass` label doesn't give you a
confidence percentage.

### Performance / complexity

Both operate over the same single heap-walk pass each tool already performs for its core memory
analyzer set — no additional heap enumeration confirmed for either beyond what the base
`HeapObjectCollector`(theirs)/`DiskBackedObjectIndexWriter`(ours) passes already produce, aside from
the retained-size BFS and chain-BFS costs already covered in the Dominator and root-chain
discussions elsewhere in this comparison set.

### Correctness caveats

**This tool**: `HeuristicOnly` flag (see the Dominator-analyzer audit's Area 6 for its history — it
used to be permanently `true`, since resolved per that audit's P1 item). Classification logic can
mis-bucket a candidate into `Unknown` when "the retention pattern was not recognised," which is an
honest fallback, not silently wrong.

**Other tool**: `LeakProbability`'s blended-heuristic nature means two very different underlying
causes could produce the same score, giving no way to distinguish them from the number alone — the
same shape of limitation as any single blended score (see the Health Score discussion in
[capability-comparison.md](../capability-comparison.md) §7 for the general version of this
tradeoff).

### Configuration & tunability

**This tool**: no dedicated `LeakCandidateAnalysisOptions` class was found (`Glob` for
`*LeakCandidate*Options*.cs` returns nothing) — **the +30/+20/+15/etc. scoring constants are
hardcoded directly in the analyzer, with no CLI flag or config-file binding at all.** This is a real,
confirmed gap: the formula is *transparent* (rendered in the report text, so a reader can see it)
but not *tunable* — an ops team can't adjust the Gen2-heavy threshold or point values without
recompiling.

**Other tool**: `memory-leak` exposes real, working CLI flags — `--min-count` (default 500, minimum
instance count for the suspect table) and `--include-system` (include `System.*`/`Microsoft.*` types).
**This is a genuine, confirmed tunability edge for the other tool on this specific pair** —
contrasting with the Dominator pair, where this tool's `RetentionOptions` config-file binding was the
one ahead. Tunability doesn't consistently favor one tool across pairs; it has to be checked per
pair.

### Trend / multi-dump behavior

Mechanism fully confirmed by tracing the actual call path on both sides — see
[analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md) §
Cross-cutting: what "trend-analysis" actually compares. Pair-specific conclusion:

**This tool**: `LeakCandidateTrendComparer` emits three base metrics per dump
(`leak.candidates.total`, `leak.candidates.top.score`, `leak.candidates.top.count`) plus per-type
metrics (`leak.candidate.type.bytes`, `leak.candidate.type.score`, keyed by type name) — each
producing a real `MetricDelta` between any two dumps, so a specific type's suspicion-score trend
(e.g. "was 45, now 78 — escalating") is directly computed, not just visible in two separate reports.

**Other tool**: `memory-leak` runs in `--full`, so its full section (including
`SuspectRow.LeakProbability`, `AccumulationPatternData`, `RootChains`) is present once per dump in a
`trend-analysis --full` run — but, per the confirmed general mechanism, only as N full,
independently-rendered "Memory Leak" sections, never as a computed delta:
`TrendAnalysisReport.RenderTrend` only ever receives `List<DumpSnapshot>`, and none of
`SuspectRow`'s fields live on `DumpSnapshot` directly. A reader tracking whether a specific type's
`LeakProbability` is climbing across dumps has to open each dump's own "Memory Leak" section and
compare the numbers by hand — the same gap as the Dominator pair's retained-bytes trend, for the
identical underlying reason.

### Validation

**This tool**: `LeakCandidateFindingGeneratorTests.cs` exists — a unit test for the finding-generation
layer specifically, not confirmed to cover the classification/scoring logic in `LeakCandidateAnalyzer`
itself end-to-end.

**Other tool**: `DumpDetective.Tests/Integration/MemoryLeakTest.cs` exists, following the same
`ScenarioTestBase<TScenario>`/`DiagnosticScenario` pattern confirmed for `DeadlockTest.cs` — a
real-dump, real-scenario integration test. **Likely a genuine validation edge for the other tool**,
consistent with the Lock-Graph/Deadlock pair's finding, though not independently confirmed whether
the `MemoryLeakScenario` specifically induces and asserts on the classification/root-chain paths
versus only the count/size suspect lists.

## Report

### Data shown

Already detailed in the original version of this file — repeated here for completeness: this tool
shows `total_candidates`, `heuristic_only`, `top_suspect`, `top_suspicion_score` as key metrics, plus
"Candidate groups by leak class" and "Top leak candidates by suspicion score" tables with 11 columns
including root kind, finalizable, container, and reference-field ratio flags. The other tool shows a
full heap snapshot (gen breakdown, top-8-by-count donut chart), count/size suspect tables, an
accumulation-pattern table with per-category status strings, a "Top Retainers" BFS-retained-size
table, and the box-drawn root-chain section (covered under the Dominator pair and the cross-cutting
report doc).

### Presentation style

**This tool**: step-free — one section, four blocks of narrative text plus tables, no numbered
investigative flow.

**Other tool**: explicitly step-numbered ("Step 1" heap snapshot → "Step 2" suspect types → "Step 3"
accumulation patterns → "Step 4" root chains), each with a `sink.Explain(what, why, bullets, action)`
block — this is the report-comparison doc's §1 finding, confirmed specifically for this pair.

### Severity/confidence communication

**This tool**: quantitative — `ConfidenceScoring.Compute(0.75, ConfidenceScoring.F(leak.HeuristicOnly,
0.15, ...))`, rendered as a 4-dot confidence symbol, plus a per-candidate `Severity` column
(`FindingSeverity` enum) directly in the main table.

**Other tool**: qualitative — `AlertLevel.Critical`/`Warning`/`Info` via `RenderFindings`, branched
on Gen2-survival percentage and size/LOH thresholds; no numeric confidence score anywhere in this
report, consistent with every other pair examined.

### Actionability of guidance

**This tool**: `LeakExplainer.Explain` gives one of eight distinct, classification-specific remedies
— e.g. `CacheLeak` → *"Apply a size limit (MemoryCache), use WeakReference values, or add an
eviction policy"*; `ThreadLocalLeak` → *"Ensure Dispose() is called on the ThreadLocal wrapper when
threads finish."* This is the single most specific, evidence-tied advice text found across every
pair examined so far, because it's keyed off an actual classification rather than a generic
size/severity threshold.

**Other tool**: `RenderFindings`'s advice is threshold-branched (survived-Gen2 vs. not, LOH vs. not)
but not tied to a specific named leak pattern the way this tool's is — e.g. every "high instance
count, mostly Gen0/1" finding gets the same generic *"may be churn rather than a leak... compare with
trend-analysis"* regardless of what kind of object it is.

### Visualizations

**This tool**: none for this analyzer's own section (table-only).

**Other tool**: a stacked-bar chart (heap by generation) and a donut chart (top-8 types by instance
count) in the heap-snapshot section that precedes the suspect tables — richer visual framing before
the reader even reaches the suspect list, though these charts describe the whole heap, not the leak
candidates specifically.

### Cross-analyzer / executive-summary participation

**This tool**: generic, confirmed — same architecture as every analyzer.

**Other tool**: `memory-leak` runs in `--full`, and unlike most other commands in this comparison set,
its accumulation-pattern signals (Gen2%, LOH size, string/collection counts) plausibly *do* overlap
with fields `HealthScorer.Score` reads directly off `DumpSnapshot` — this is the one pair in this
comparison set where the other tool's top-level rollup might actually see *some* of this command's
signal, even though the leak-specific classification/probability itself still wouldn't. Not
independently confirmed field-by-field.

### Drill-down / cross-referencing

**This tool**: `RootKind` rendered as a bare word in the main table, with a text pointer ("Investigate
root paths in §A5") — the same cross-reference-without-inlining gap as the Dominator pair.

**Other tool**: the "Top Retainers" table's caption points to "Step 4" for the retaining root, and
Step 4 *is* in the same document with a real chain — a working, inlined pointer within the same
report, unlike this tool's pointer to a differently-structured section.

### Machine-consumability

Consistent with the general finding — no pair-specific deviation identified.

### HTML interactivity

Consistent with the general parity finding — not independently re-verified table-by-table for this
pair.

### Output format parity

Not independently re-verified for this pair in this pass.

## Bottom line

**Analysis**: this tool is ahead on classification structure (a named, extensible `LeakClass` enum
vs. five hardcoded pattern checks) and has by far the most specific remediation text of any pair
examined — but the other tool is ahead on tunability (`--min-count`/`--include-system` are real,
working flags; this tool's scoring formula has no config-file or CLI binding at all despite being
transparently documented in the report text) and likely ahead on validation (a real-scenario
integration test vs. a finding-generator-only unit test on this side).

**Report**: the other tool is still ahead structurally (step-numbered `Explain` flow, working inline
chain pointer, pre-suspect-list charts) — but this tool's per-candidate advice text is the strongest,
most evidence-specific guidance found across every pair in this comparison set, which is worth
recognizing as a genuine strength independent of the overall report-structure gap.

## Recommendations

- **Cheap, do now**: add config-file/CLI binding for the scoring-formula constants — the formula is
  already documented and transparent, so this is "make the existing, already-explained numbers
  editable," not new design work.
- **Cheap, do now**: port this analyzer's classification-specific advice style
  (`LeakExplainer.Explain`) as the model for `DominatorFindingGenerator`'s more generic
  recommendation text — an internal consistency fix, no comparison to the other tool required.
- Carried forward: the inline-chain recommendation from the Dominator pair applies identically here.

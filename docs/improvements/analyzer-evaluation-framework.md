# Analyzer Evaluation Framework

## Status
Draft — created 2026-07-16 to support a pass over every analyzer in `src/DumpDetective.Analysis/Analyzers/`.

## Purpose
The existing [analysis-project-critical-review.md](analysis-project-critical-review.md) covers code
*structure* (class size, ownership, layering). It does not ask whether each analyzer's output is
correct, meaningfully actionable, or efficiently computed/cached.

This framework is for a **per-analyzer audit** along three axes:
1. Insight quality — is the output meaningful and actionable?
2. Accuracy — is the data correct?
3. Performance & caching — is it computed efficiently, and could caching help?

Use it to score each analyzer in [Analyzers/](../../src/DumpDetective.Analysis/Analyzers/) and produce a
ranked backlog of improvements, rather than a generic "looks fine" pass.

## Scope
Current analyzer inventory (39 files in `Analyzers/`, one row per analyzer under audit):
`AsyncStateMachineAnalyzer`, `DominatorAnalyzer`, `FinalizableObjectAnalyzer`,
`GCGenerationAnalyzer`, `GCHandleAnalyzer`, `JitAnalyzer`, `ObjectShapeAnalyzer`,
`SegmentReservationAnalyzer`, `DbConnectionAnalyzer`, `EventLeakFastScanner`, `HttpObjectAnalyzer`,
`TimerLeakAnalyzer`, `WcfChannelAnalyzer`, `AllocationPatternAnalyzer`, `DependentHandleAnalyzer`,
`EventLeakAnalyzer`, `GCRootAnalyzer`, `HeapTopologyAnalyzer`, `LeakCandidateAnalyzer`,
`LockGraphAnalyzer`, `MemoryAnalyzer`, `MemoryLeakAnalyzer`, `ModuleAnalyzer`,
`StaticRootLeakDetector`, `StringAnalyzer`, `ThreadAnalyzer`, `ThreadStackClusterAnalyzer`,
`ReferenceChainAnalyzer`, `BoxingAnalyzer`, `WeakReferenceAnalyzer`, `AsyncTaskAnalyzer`,
`LohFragmentationAnalyzer`, `ArrayAnalyzer`, `CrashAnalyzer`, `HangAnalyzer`, `CollectionAnalyzer`.

(`AnalyzerHelpers.cs`, `CollectionAnalysisHelpers.cs`, `SegmentKindMapper.cs` are shared helpers, not
analyzers themselves — audit them as dependencies of the analyzers that use them, not standalone rows.)

## Axis 1 — Insight Quality
Ask, for each analyzer's `XxxDomainResult`:

- **Actionability**: If a user sees this finding, do they know what to do next? Or is it just a number
  with no interpretation?
- **Signal vs. noise**: Are thresholds (e.g. "top 50", "> 10MB", severity cutoffs) justified by
  reasoning or evidence, or are they arbitrary magic numbers copied across analyzers?
- **Ranking/scoring**: If the analyzer ranks or scores suspects, is the scoring formula sound? Does it
  combine size, count, and lifetime (Gen2/LOH, age) in a defensible way, or just sort by one dimension?
- **Missed correlations**: Could this analyzer's output be meaningfully cross-referenced with another
  analyzer's (e.g. `EventLeakAnalyzer` + `StaticRootLeakDetector` + `GCRootAnalyzer` root paths) to
  produce a stronger combined finding? (This is `InsightEngine`'s job today — check whether the
  analyzer emits enough structured data for `InsightEngine` to actually use, e.g. addresses/type ids
  rather than only pre-formatted strings.)
- **Coverage gaps**: Are there common leak/perf patterns in this analyzer's domain that ClrMD makes
  available but the analyzer doesn't check? (e.g. does `TimerLeakAnalyzer` check timer callback target
  retention, not just timer count? Does `LockGraphAnalyzer` detect lock-order-inversion cycles, or only
  list held locks?)
- **False positive / false negative risk**: What's the failure mode — over-reporting benign patterns,
  or staying silent on real leaks? Which is worse for this analyzer's category, and does current
  behavior lean the wrong way?

Score: `None / Weak / Adequate / Strong` per bullet, plus 2-3 sentences on the single highest-value
improvement.

## Axis 2 — Accuracy
Ask:

- **ClrMD correctness**: Does it check `obj.IsValid` and `obj.Type != null` before use? Does it read
  fields via `field.ReadObject(obj.Address)` correctly, or does it risk reading stale/wrong offsets?
- **Numeric correctness**: Sizes as `ulong` throughout (no accidental `int` truncation on LOH-scale
  objects)? Percentage/ratio math protected against divide-by-zero on empty heaps?
- **Edge cases**: Behavior on empty heap, single-object heap, heap with no instances of the target
  type, corrupted/partial dumps, multi-AppDomain or multi-threaded-stack scenarios.
- **Consistency with ClrMD semantics**: Does the analyzer's notion of "root", "generation", "segment",
  etc. match ClrMD's actual definitions, or does it reimplement/assume something that's drifted?
- **Determinism**: Same dump analyzed twice produces identical output (no dependency on dictionary
  iteration order, `HashSet` enumeration order, or parallel-task completion order leaking into results
  or ranking).
- **Cross-check opportunity**: Is there a cheap way to validate this analyzer's output against a known
  ground truth (e.g. compare object counts/sizes against `!dumpheap -stat` equivalent, or against a
  synthetic test dump with known allocations)? See [Testing](#validation--test-plan) below.

Score: `None / Weak / Adequate / Strong` per bullet, plus concrete evidence (file:line) for any
"Weak"/"None" finding.

## Axis 3 — Performance & Caching
Baseline rules are in [performance-checklist.md](../performance-checklist.md) — don't re-derive those;
apply them per analyzer and note violations. Additionally ask, per analyzer:

- **Heap scans**: How many full heap enumerations does this analyzer perform? Could any be folded into
  the Phase 1 streaming index build instead of a dedicated Phase 2 pass?
- **Cache read usage**: Does it read from `HeapAnalysisCache` / type-metadata caches where available,
  or does it re-resolve `ClrType`/field layouts itself? (Check `GetOrBuildFieldLayout`-style patterns —
  `CollectionAnalyzer.cs:988` already does this; is the same pattern applied consistently elsewhere?)
- **Cache write opportunity**: Does this analyzer compute something expensive and reusable (a
  type→count map, a root index, a field-layout map) that isn't cached today but is likely to be
  requested again by another analyzer or a repeat query in the same run?
- **New cache candidates**: Given the `docs/cache/` roadmap (content-addressed cache, per-section
  checksums, disk-backed indices), is there a specific new cache section this analyzer would benefit
  from that doesn't exist yet? Say what it would store and its estimated size/build cost.
- **Algorithmic complexity**: Any accidental O(n²) — e.g. nested heap loops, repeated `ReferenceGraph`
  traversal from scratch per candidate instead of a shared BFS budget, or LINQ chains re-materializing
  per object.
- **Parallelism fit**: Is this analyzer's work independent per-type/per-object (parallelizable per the
  performance checklist's "parallelize only type analysis / independent queries" rule), and if so, is
  it actually parallelized (see `RunParallelCollectionAnalysis` in `CollectionAnalyzer.cs:160` as a
  reference pattern)?
- **Benchmark presence**: Is there a `BenchmarkSuite1` entry for this analyzer (see
  `DependentHandleAnalyzerBenchmark.cs`, `PipelineHotspotBenchmark.cs`)? If not, and the analyzer scans
  the full heap, add one.

Score: `None / Weak / Adequate / Strong` per bullet, plus a concrete before/after estimate if a change
is proposed (e.g. "avoids second heap pass, saves ~O(n) ClrMD calls on a 10GB dump").

## Per-Analyzer Audit Template
Copy this block per analyzer into the working audit doc/spreadsheet:

```
### <AnalyzerName>
File: src/DumpDetective.Analysis/Analyzers/<AnalyzerName>.cs
Domain result: <XxxDomainResult>
Category/Tags: <from IAnalyzer.Category / .Tags>

Insight quality: <score> — <top improvement, 1-2 sentences>
Accuracy: <score> — <top risk, with file:line if applicable>
Performance/caching: <score> — <top opportunity, with estimated impact>

Priority: <High/Med/Low>  (based on: heap-scan cost x how commonly this analyzer's output drives decisions)
Notes: <anything cross-analyzer, e.g. overlaps with GCRootAnalyzer, could share traversal>
```

## Validation / Test Plan
For accuracy claims to be more than opinion, back them with:
- Synthetic dumps with known object counts/sizes/roots for the analyzer category (e.g. a dump with N
  timers, M known-leaked event handlers) — check whether these fixtures already exist under `tests/`
  before creating new ones.
- Cross-tool comparison against WinDbg/ClrMD-native output (`!dumpheap -stat`, `!gcroot`) for a sample
  of findings, spot-checked rather than exhaustive.
- Idempotency check: run the analyzer twice on the same cached index, assert identical output.

## Prioritization
Rank analyzers for the pass using:
1. **Blast radius**: analyzers behind `InsightEngine` correlations or shown prominently in the default
   report (leak/thread/GC-handle family) first.
2. **Heap-scan cost**: analyzers doing full/multiple heap enumerations before ones doing bounded
   index-only queries.
3. **Known weak spots already flagged**: `MemoryAnalyzer` and `GCRootAnalyzer` are already flagged in
   [analysis-project-critical-review.md](analysis-project-critical-review.md) as oversized — audit
   these first since structural cleanup and insight/accuracy audit can land together.

## Output of the Pass
Produce one findings doc (e.g. `docs/improvements/analyzer-audit-findings.md`) with:
- one filled-in template block per analyzer
- a ranked top-N list of concrete follow-up work items (mirroring the "Concrete Refactor Opportunities"
  style already used in the project critical reviews, for consistency)
- explicit call-outs for any accuracy bug found (treat these as bugs, not backlog — fix or file
  immediately, don't let them sit in a prioritized list next to nice-to-haves)

# Roadmap — Closing the Gap

Ordered by leverage (cheapest to verify / highest confidence impact first), not by how "impressive"
the item sounds. Item 1 is a confirmed fix, not a diagnostic. Items 2–4 are diagnostic, not
implementation — do not skip to implementation on those before they're done, or effort may be spent
optimizing something that isn't actually the bottleneck.

## Phase 0 — Measure before changing anything

1. **Parallelize analyzer execution.** This is no longer diagnostic — it's a confirmed, verified bug
   in the true sense: `IAnalyzer.IsThreadSafe` exists but is dead code (zero callers, confirmed via
   the code graph), and every analyzer runs strictly sequentially through a `foreach` in
   `AnalysisPipeline.RunAnalyzerBatchAsync`, contradicting `docs/architecture.md`'s own description
   of the intended design. The other tool's 8-way LPT-scheduled parallel execution is directly
   comparable prior art. This should be fixed before spending time on the ClrMD question below,
   because it's cheaper to test, cheaper to fix, and very likely explains a meaningful chunk of the
   full-report wall-clock gap on its own — see
   [performance-comparison.md](performance-comparison.md) § Hypothesis 0.
2. **Run the ClrMD 3.1 vs. 4.0 isolation experiment**
   ([performance-comparison.md](performance-comparison.md) § Hypothesis 1). Two throwaway console
   apps, same dump, count objects/sec. This is the cheapest possible experiment for the *heap-walk*
   phase specifically (as opposed to the analyzer phase, which item 1 covers) and gates every
   heap-walk-level performance decision — if ClrMD 4.0 itself is slower, no analyzer-level tuning will
   close that part of the gap until it's addressed (upstream issue, pin back to 3.1.x, or accept the
   tradeoff for whatever 4.0 buys us).
3. **Get one same-dump, same-machine timing run of this tool** at a comparable scale to the other
   tool's published 25GB/86.5M-object benchmark, with the same phase breakdown (heap-walk
   objects/sec, collection time, peak working set) — do this *after* item 1's parallelization fix
   lands, so the number reflects the tool's real potential rather than a scheduling bug. Without
   this, "theirs is faster" stays qualitative. See
   [performance-comparison.md](performance-comparison.md) § What a fair comparison requires.
4. **Confirm whether reverse-index construction is skipped when no selected analyzer needs it.**
   If `--include-analyzers` narrows the run to, say, just `ModuleAnalyzer`, does the pipeline still
   pay the reverse-index build cost? If yes, this is a cheap, contained fix (gate the build behind
   "does any selected analyzer's `Tags` include a reverse-index-consuming tag") with no architectural
   risk.

## Phase 1 — Cheap, contained wins (no new subsystems)

5. **Externally tunable thresholds**, matching the other tool's `dd-thresholds.json` pattern
   (silent fallback to compiled defaults). Low risk, directly requested-adjacent value (ops teams
   retuning severity without a rebuild), and this repo already has the `HealthScorecardBuilder`
   machinery to hang it off of.
6. **`object-inspect`-equivalent single-object drill-down command**, backed by `QueryEngine` plus a
   bounded `BoundedGraphWalk` for retained-size — this repo's own Phase 0 review already flagged
   this as a confirmed gap independent of this comparison. Doesn't require the plugin system or
   trace analysis to land first.
7. **Explicit `load`/`close`-style cache lifecycle commands** wrapping the existing `cache.bin` +
   reverse-index build, so a user who plans to run multiple commands/analyzers against the same
   dump can pay the expensive-index cost once. This is largely UX/CLI-surface work over
   infrastructure that already exists (`IHeapIndexBuilder.PrebuildHeapIndex`,
   `DumpIndexPaths.ResolveCacheDirectory`) — not a new indexing subsystem.

## Phase 2 — Structural, higher-effort, higher-payoff

8. **Evaluate whether `AnalysisReportDocument` can become a serializable, polymorphic, replayable
   structure** (`ReportDoc`-equivalent), as the prerequisite for `render`/`diff` commands
   (§5 of [architecture-comparison.md](architecture-comparison.md)). This is the single change that
   unlocks the most other-tool capability at once — `render`, `diff`, and Brotli-compressed `.bin`
   archival all fall out of having one true serializable report format that every output sink
   replays, rather than three commands each reimplementing report traversal.

## Phase 3 — New product surface (large, sequence last)

9. **Trace (`.nettrace`/`.etl`) analysis** is the largest gap in this comparison but is also a
   genuinely new subsystem (`DumpDetective.Analysis.Trace`-equivalent, `TraceEvent` dependency,
   11 new analyzer types), not a tuning problem. Sequence this last, and only once the health-score,
   drill-down, and cache-lifecycle gaps (Phases 1–2) are closed — those improve the existing
   memory-dump product every user already has; trace analysis is a new product line on top of it.
10. **Plugin system**, if third-party/internal-team extensibility becomes a stated goal — otherwise
    the existing `IAnalyzer` catalog's 4-type registration friction (already flagged in
    `docs/analysis/phase-0/phase0-deliverable-9-industry-benchmark.md`) should be reduced first
    (sensible defaults for generator/comparer/section-builder types), since a plugin system built on
    top of today's 4-type-per-analyzer contract would just export that friction to third parties.

## Explicitly not recommended

- Chasing native AOT as a performance response — see
  [performance-comparison.md](performance-comparison.md) § Hypothesis 3; the evidence suggests
  neither tool is actually running AOT in its shipped build, so this wouldn't explain or close any
  real gap.
- Matching the other tool's exact command-per-signal CLI shape (`heap-fragmentation`, `high-refs`,
  `thread-pool` as separate top-level commands) purely for parity — this repo's broader
  cross-analyzer catalog with a shared `Evidence`/confidence model is a deliberate, arguably stronger
  design per the existing industry-benchmark review; the gap that matters is the *missing*
  drill-down/replay/cache-lifecycle UX (Phase 1), not the granularity of command naming.

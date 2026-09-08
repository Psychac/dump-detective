# Performance Comparison

**Status: we do not yet have a same-dump, same-hardware timing comparison.** One factor below
(Hypothesis 0) is no longer a hypothesis — it's a confirmed, source-verified difference in how the
two tools schedule analyzer/command execution, found by reading both codebases directly rather than
trusting either tool's docs. Everything past Hypothesis 0 is either (a) a number the other tool's
own README publishes, which we have no reason to doubt but also haven't reproduced, or (b) a
hypothesis for the reported gap that is falsifiable with a specific, cheap experiment. Nothing here
should be read as "our tool is N seconds slower" — that number doesn't exist yet. Getting it is
[roadmap.md](roadmap.md)'s top item.

## What the other tool publishes (read from `Docs/Architecture.md` / `README.md`, 2026-08-17)

Benchmark dump: ~25 GB IIS worker dump, 86,546,865 managed objects, on their measurement hardware
(hardware spec not fully captured here — re-check `README.md` § Requirements for the assumed
16-core/16GB-RAM baseline before treating these as portable numbers).

| Metric | Value |
|---|---|
| Heap walk throughput | ~652,676 objects/s (no plugins), ~598,530 objects/s (with plugins) |
| Full collection time | 156.7s (no plugins) / 168.6s (with plugins) |
| `analyze --full` total wall-clock | 203.1s (no plugins) / 203.4s (with plugins) |
| Peak working set | 11.00 GB (no plugins) / 11.96 GB (with plugins) — roughly 0.44–0.48x the dump file size |
| BFS index cold build (87M nodes) | 211.9s total (3-pass + Brotli save) |
| BFS index warm load (same dump, cached) | 9.8s |
| `trend-analysis --full` across 3×~25GB dumps, cold | 1827.9s (~30.5 min) |
| `trend-analysis --full` across 3×~25GB dumps, warm cache | 365.3s (~6.1 min) — **~5x speedup from caching alone** |

These numbers are internally consistent and plausible for a ClrMD-based tool at this scale — no
reason to doubt them. The open question is entirely about our side of the comparison.

## Hypothesis 0 (confirmed, not hypothetical — highest priority): sequential vs. parallel analyzer execution

This is no longer a hypothesis; it was verified directly against both codebases' source (see
[architecture-comparison.md](architecture-comparison.md) §4):

- **This tool runs its 31 analyzers strictly sequentially.** `AnalysisPipeline.RunAnalyzerBatchAsync`
  is a plain `foreach` loop that `await`s each analyzer before starting the next. `IAnalyzer.IsThreadSafe`
  exists on the interface but has zero callers anywhere in the codebase (confirmed via a code-graph
  `uses`-edge query returning an empty list) — it is dead code, not a working opt-in-to-parallel
  mechanism, despite `docs/architecture.md` describing it as one.
- **The other tool runs its full analyzer set 8-way parallel with LPT (Longest Processing Time)
  scheduling.** Confirmed by reading `AnalyzeReport.RenderEmbeddedReports` directly: a
  `Parallel.ForEach` over a `NoBuffering` partitioner, `MaxDegreeOfParallelism = 8`, with an explicit
  source comment confirming commands are pre-ordered slowest-first in `CommandRegistry` so the
  parallel workers pick up the most expensive work first.

**Why this matters more than raw ClrMD throughput for the `analyze --full`-equivalent wall-clock
number specifically:** for a fixed set of N analyzers/commands with heterogeneous individual costs,
sequential execution's wall-clock time is *approximately the sum* of every analyzer's time; 8-way
LPT-scheduled parallel execution's wall-clock time is *approximately the time of the single most
expensive analyzer* (assuming the other 7 slots absorb the rest). For a 31-analyzer catalog where
several analyzers do real BFS/graph work (`DominatorAnalyzer`, `GCRootAnalyzer`,
`StaticRootLeakDetector`, `ReferenceChainAnalyzer` all call into `BoundedGraphWalk`), this difference
alone could easily be a multi-x wall-clock gap on the analyzer phase, independent of ClrMD version,
independent of index-build strategy, and reproducible on a dump of any size — including small ones,
where the other hypotheses below wouldn't apply at all. **If report-generation wall-clock time is
the main complaint (as opposed to the heap-walk/index-build phase), start here, not with ClrMD.**

This is also the cheapest of all four hypotheses to test in isolation: instrument
`RunAnalyzerBatchAsync` (or a copy of it) to run the same 31 analyzers against the same
already-built index, once via the current `foreach` and once via `Parallel.ForEach` with
`MaxDegreeOfParallelism` bounded appropriately, and compare wall-clock time. This requires no
dump-size scaling and no ClrMD-version isolation harness — it can be measured on the smallest dump
already in the test suite.

## Hypothesis 1: ClrMD 3.1 vs. 4.0

This repo depends on `Microsoft.Diagnostics.Runtime 4.0.732401`; the other tool depends on
`3.1.512801` (confirmed via `grep` on both `.csproj` files — see
[architecture-comparison.md](architecture-comparison.md) §1). This branch's own name,
`upgrade/clrmd-4`, documents that this is a recent, deliberate, in-flight major-version bump.

**Why this matters more than any architectural difference:** every single per-object operation in
both tools — `heap.EnumerateObjects()`, `obj.Type` resolution, field reads — goes through this exact
library. If ClrMD 4.x has a per-object overhead regression relative to 3.1.x (a plausible outcome of
a major version bump touching DAC interop and heap-walk internals), it would show up as a uniform
slowdown across *every* analyzer and command, independent of anything this repo's own code does.

**How to test this in isolation, cheaply:** write a minimal console harness that does nothing but
`heap.EnumerateObjects()` and counts objects/bytes, against the same dump, once linked against
ClrMD 3.1.512801 and once against 4.0.732401 (two throwaway console projects, no analyzer catalog
involved). Compare objects/sec. This isolates the ClrMD-version variable completely from every other
difference in this document and should be the very first experiment run, before touching any
analyzer code, because if it confirms a regression, no amount of analyzer-level optimization in this
repo will close the gap until the ClrMD upgrade itself is investigated or reverted.

## Hypothesis 2: implicit vs. explicit expensive-index construction

As detailed in [architecture-comparison.md](architecture-comparison.md) §3, this tool's reverse
(parent-lookup) index build is part of the normal analysis pipeline unless
`DD_SKIP_REVERSE_INDEX_BUILD=1` is set, whereas the other tool only pays its equivalent (`BfsIndexBuilder`,
3-pass, ~212s cold on an 87M-node heap) when `load` is invoked or an analyzer needs it, and reuses it
across every subsequent run against that dump. If a real-world usage pattern for either tool is
"open the dump, run one focused command," the other tool's model pays for the expensive index only
when actually needed; this tool may be paying for it every time regardless of which analyzers were
requested via `--include-analyzers`/`--exclude-analyzers`. Worth checking: does
`RunAnalyzersPipelineStage` skip reverse-index construction when no selected analyzer needs it, or
is it unconditional?

## Hypothesis 3: AOT is very likely not the explanation

Investigated and effectively ruled out — see
[architecture-comparison.md](architecture-comparison.md) §2. The other tool's own architecture doc
claims `PublishAot=true`, but its actual `DumpDetective.Cli.csproj` sets
`PublishAot` to `false` for RID-specific builds, and their documented publish command doesn't
override it. Both tools appear to ship framework-dependent JIT builds on `net10.0` in practice. Not
worth spending time on a native-AOT rewrite as a performance response until this is re-confirmed
against their actual shipped binary (not just their docs).

## Hypothesis 4: per-object work density in the heap-walk consumer set

This tool's `DiskBackedObjectIndexWriter` does more work per unique `MethodTable` (not per object)
during the Phase 1 scan than a bare heap-stats pass would — computing type flags, delegate/async
detection, field-shape detection, satellite candidate collection (task/event/LOH-free-block/
large-object candidates) all in the same pass. This is architecturally correct (avoiding N extra
passes), but it means the *baseline* single-pass walk in this tool is doing strictly more work than
a heap-stats-only walk would. The other tool's `HeapWalker.Walk` also runs many consumers in the
same pass (`TypeStatsConsumer`, `InboundRefConsumer`, `StringGroupConsumer`, `AsyncMethodConsumer`,
etc. — a comparable list), so this is likely **not** a differentiator on its own, but it means a
fair benchmark must compare "run everything" against "run everything," not a partial run on one
side against a full run on the other.

## What a fair comparison requires

1. Same dump file (or same-size/same-object-count dump from the same source application), same
   machine, same run — not a comparison across different incidents.
2. Compare `analyze --full`-equivalent (full analyzer catalog, no `--include-analyzers` filtering)
   against this tool's default run (which already runs the full catalog with no CLI flag needed —
   confirm this is actually equivalent scope before comparing numbers).
3. Report the same phase breakdown the other tool's README gives: heap-walk objects/sec, total
   collection time, peak working set, and (if this repo builds one) reverse-index build time
   separately from analyzer time.
4. Run the ClrMD-version isolation experiment (Hypothesis 1) *before* drawing conclusions from the
   end-to-end number, so a ClrMD regression doesn't get misattributed to this repo's own pipeline
   code.

See [roadmap.md](roadmap.md) for how to sequence this.

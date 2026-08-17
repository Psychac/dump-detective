# Discrepancy Analysis — DumpDetective vs. Rohit_DumpDetective

> Scope: a structural, evidence-based comparison of this codebase (`upgrade/clrmd-4` branch)
> against a sibling implementation at `d:/POC/Rohit_DumpDetective` (same product name, independent
> codebase, referred to below as "the other tool"). Written to identify concrete capability and
> architecture gaps worth closing, not to declare a winner for its own sake.

Every claim below is grounded in something read directly from one of the two repos on 2026-08-17 —
primarily source code, verified two ways: direct `Read`/`Grep` on this repo, and `tokensave`'s code
graph (pointed at `graph_root: d:/POC/Rohit_DumpDetective`, which has its own `.tokensave/` index)
for the other repo, so its README's claims aren't taken at face value. Every class/command named in
`capability-comparison.md` and `architecture-comparison.md` (`PluginLoader`, `RenderCommand`,
`DiffCommand`, `ObjectInspectCommand`, `LoadCommand`, `CloseCommand`, `TraceAnalyzeCommand`,
`HealthScorer`, `BfsIndexBuilder`, `ThresholdLoader`) was confirmed to exist as a real, non-trivial
implementation via `tokensave_search` against that graph — not assumed from prose. This also caught
one thing the other tool's own README doesn't mention: a `RootCauseTraceCommand`
(`DumpDetective.Commands/Trace/RootCauseTraceCommand.cs`, implements both `ICommand` and
`ITraceSubAnalyzer`) exists in their code but isn't in their documented command table — their docs
undersell their own command surface by at least one command. Where a claim would require running a
benchmark we don't yet have, it is marked **unverified — needs a same-dump run** rather than stated
as fact. ClrMD version and AOT settings were confirmed directly against both repos' `.csproj` files
(code, not docs) — see [architecture-comparison.md](architecture-comparison.md) §1–2.

## Documents in this set

| Doc | Covers |
|---|---|
| [capability-comparison.md](capability-comparison.md) | Command surface, analyzer/consumer coverage, output formats, caching UX, plugin system |
| [architecture-comparison.md](architecture-comparison.md) | Index/cache strategy, ClrMD version, execution model, report model, AOT |
| [performance-comparison.md](performance-comparison.md) | The other tool's published benchmarks, our current lack of equivalent numbers, and concrete hypotheses for the reported gap |
| [roadmap.md](roadmap.md) | Prioritized list of gaps worth closing, ordered by leverage |

## Headline findings

1. **Confirmed, not hypothesized: this tool's 31 analyzers run strictly sequentially; the other
   tool runs its full command set 8-way parallel with LPT scheduling.** `AnalysisPipeline.RunAnalyzerBatchAsync`
   is a plain `foreach`/`await` loop — `IAnalyzer.IsThreadSafe` exists on the interface but has zero
   callers anywhere in the codebase (confirmed via a code-graph `uses`-edge query), meaning
   `docs/architecture.md`'s claim that "analyzers may run in parallel when `IsThreadSafe` is opted
   in" describes something that was never actually wired up. The other tool's
   `AnalyzeReport.RenderEmbeddedReports` was read directly and confirmed to run a genuine
   `Parallel.ForEach` (`MaxDegreeOfParallelism = 8`) with commands pre-sorted slowest-first, per an
   explicit source comment naming the technique as LPT scheduling. This is likely the single
   largest, cheapest-to-fix contributor to any full-report wall-clock gap, independent of dump size
   or ClrMD version — see [performance-comparison.md](performance-comparison.md) § Hypothesis 0 and
   [roadmap.md](roadmap.md) item 1.
2. **The other tool covers two input types we don't touch at all: `.nettrace`/`.etl` trace
   analysis (11 trace commands, all confirmed to exist as real analyzer classes via the code graph)
   and cross-source trace+dump correlation (`ITraceDumpCorrelationRule`/`CorrelationEngine`,
   confirmed the same way).** This is not a "do it better" gap, it's a "doesn't exist here" gap —
   see [capability-comparison.md](capability-comparison.md).
3. **The other tool ships a persistent, reusable on-disk cache (`load`/`close` commands, `.bfs.idx`
   BFS index built via a confirmed 3-pass `BfsIndexBuilder`) that is *explicitly* opt-in and
   measured at ~5x speedup across repeat runs.** We build a disk-backed index too (`cache.bin`), but
   it isn't exposed as a first-class, user-controlled lifecycle the way `load`/`close` are — see
   [architecture-comparison.md](architecture-comparison.md) § Cache lifecycle.
4. **The other tool is pinned to ClrMD 3.1.512801; this branch is mid-upgrade to ClrMD 4.0.732401**
   (branch name `upgrade/clrmd-4` literally documents this, confirmed directly from both `.csproj`
   files). A ClrMD major-version regression is a live, testable hypothesis for the heap-walk phase
   specifically — see [performance-comparison.md](performance-comparison.md) § Hypothesis 1.
5. **We have no same-dump, same-hardware timing comparison yet.** The other tool's README
   documents specific, reproducible numbers (25 GB / 86.5M-object dump, full breakdown by phase).
   Until we run our own `analyze` against a similarly-sized dump — after fixing item 1 above, so the
   number isn't dominated by a scheduling bug — "theirs is faster" stays directionally credible but
   unquantified on our side.
6. **Where we're ahead:** analyzer breadth on a single heap pass for memory-dump-only analysis (31
   analyzers spanning memory/GC/threads/async/infra-resource-leaks with a formal `Evidence`/
   confidence model — see `docs/analysis/phase-0/phase0-deliverable-9-industry-benchmark.md` for the
   pre-existing tool-vs-tool comparison this builds on), and a stricter architectural discipline
   around bounded memory (hard 20-depth BFS cap confirmed in `BoundedGraphWalk` and covered by a
   dedicated depth-clamp test, `ArrayPool`, no full graph materialization stated as an enforced rule,
   not just an aspiration).

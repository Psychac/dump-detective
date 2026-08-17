# Architecture Comparison

Source: this repo's `docs/architecture.md` vs. the other tool's `Docs/Architecture.md` and csproj
files, read 2026-08-17.

## 1. ClrMD version — the single most testable performance hypothesis

| | This tool | Other tool |
|---|---|---|
| `Microsoft.Diagnostics.Runtime` | **4.0.732401** | **3.1.512801** |

This repo's current branch is literally named `upgrade/clrmd-4` — this is a known, in-progress
migration, not an oversight. ClrMD had breaking internal changes between the 3.x and 4.x lines
(heap-walk internals, DAC interop). A major-version jump in the exact library that performs every
heap enumeration, field read, and type resolution is the single most direct, testable explanation
for a reported "runs way faster" gap, and should be checked **before** assuming the difference is
architectural. See [performance-comparison.md](performance-comparison.md) § Hypothesis 1 for how to
verify this in isolation.

## 2. Target framework / AOT

| | This tool | Other tool |
|---|---|---|
| TFM | `net10.0` | `net10.0` |
| `PublishAot` | Not set (defaults to framework-dependent JIT) | `Docs/Architecture.md` states `PublishAot=true`, but the actual `DumpDetective.Cli.csproj` sets `<PublishAot Condition="'$(RuntimeIdentifier)' != ''">false</PublishAot>` — i.e. AOT is **off** even for RID-specific builds unless overridden elsewhere. The README's `dotnet publish -r win-x64 -c Release` self-contained-exe instructions do not pass an AOT override, so the shipped tool build is likely framework-dependent JIT too, matching this tool, not the "native AOT" framing in their own architecture doc |

**Takeaway: this is very likely not an AOT-vs-JIT difference in practice**, despite the other
tool's docs claiming AOT — their own csproj contradicts their own architecture doc. Worth flagging
to avoid chasing a non-issue; the real levers are ClrMD version (§1) and index/cache strategy (§3),
not startup-time JIT warmup.

## 3. Index / cache strategy

Both tools do a single-pass heap walk feeding multiple consumers — the core design is the same
shape (`HeapWalker.Walk` here vs. `HeapObjectCollector`/`IHeapObjectConsumer` there). The divergence
is in what happens around that walk:

| | This tool | Other tool |
|---|---|---|
| Primary index | `cache.bin` — single columnar container (`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`), disk-backed for large dumps, memory-backed for small | `.ddcache/<dump-name>/` — multiple purpose-built files (`stringGroups.bin`, `fragmentation.bin`, `gc-roots.bin`, `static-roots.bin`, `hot-addr-types.bin`, `finalizer-queue.bin`) plus a separate `<dump>.bfs.idx` (CSR-format BFS graph) and `<dump>.parent.map` |
| Retained-size / BFS graph | Built via `BoundedGraphWalk` on demand, per analyzer call, hard-capped at depth 20; a disk-backed reverse (parent-lookup) index is built during the main run (skippable via `DD_SKIP_REVERSE_INDEX_BUILD=1`) | Built once via a dedicated 3-pass parallel CSR builder (`BfsIndexBuilder`: enumerate → count edges → fill edges), saved Brotli-compressed, explicitly reusable across runs and across commands via `load` |
| Reverse/parent index construction cost | Happens inside the normal analysis run (part of the pipeline unless explicitly skipped) | Happens only when `load` is invoked, or lazily on first `--retained` use, and is cached going forward — normal `analyze` runs do not pay the full BFS-build cost unless something needs it |
| Cache lifecycle exposed to the user | Implicit — resolved via `--cache-dir` tiering (`DumpIndexPaths.ResolveCacheDirectory`), no explicit build/delete verbs | Explicit — `load <dump>` (pre-build, `--force` to rebuild), `close <dump>` (delete, `--dry-run` to preview) |
| Measured cache reuse win (their numbers) | Not measured on our side yet | ~5x on a 3-dump trend run: 1827.9s cold → 365.3s with pre-built caches |

The architectural difference worth internalizing: **the other tool treats "build the expensive BFS
index" as an explicit, user-controlled, one-time cost that many subsequent commands amortize
against**, whereas this tool's reverse index is built as an implicit part of every run's pipeline
unless the caller knows to set an env var to skip it. For a workflow where the same dump is queried
repeatedly (which is exactly the `object-inspect`/`gc-roots`-style repeated-drill-down workflow this
tool currently doesn't expose as separate commands — see
[capability-comparison.md](capability-comparison.md) §2), an explicit `load`/`close` lifecycle is a
real UX and performance win independent of any raw ClrMD speed difference.

## 4. Execution / parallelism model

**This section was corrected after reading the actual pipeline code — `docs/architecture.md` §14
("analyzers may run in parallel when `IsThreadSafe` is opted in") does not match what the code
does, and the true gap here is larger than that doc claims.**

| | This tool (verified against source) | Other tool (verified against source) |
|---|---|---|
| Heap walk parallelism | Confirmed real: `Parallel.For` over `ClrHeap.Segments` in `DiskBackedObjectIndexWriter.cs`, tiered degree of parallelism by dump size (`Min(ProcessorCount, 8)` Large / `4` Medium / `2` otherwise — read directly from the source, lines ~111–208) | 8-way `Parallel.ForEach` over the heap walk (`HeapObjectCollector.CollectHeapObjectsCombined`/`CollectHeapObjects`) |
| Per-analyzer/per-command parallelism | **None. Fully sequential.** `AnalysisPipeline.RunAnalyzerBatchAsync` (`src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs:106`) runs every analyzer in a plain `foreach` loop, one at a time, `await`ing each `AnalyzerExecutionRunner.ExecuteAsync` before starting the next. `IAnalyzer.IsThreadSafe` (`src/DumpDetective.Core/Abstractions/IAnalyzer.cs:13`) has **zero callers anywhere in the codebase** — confirmed via a `uses`-edge query on the code graph, which returned an empty caller list. The property exists on the interface, every analyzer implicitly inherits its `false` default, and nothing ever reads it. This is dead code, not an opt-in mechanism — the doc's "opted in" framing describes a feature that was never wired up, or was removed and the doc never updated. | Confirmed real via direct source read of `AnalyzeReport.RenderEmbeddedReports` (`DumpDetective.Reporting/Reports/AnalyzeReport.cs:64`): `Parallel.ForEach` over a `Partitioner.Create(..., EnumerablePartitionerOptions.NoBuffering)`, `MaxDegreeOfParallelism = 8`, with an explicit code comment: *"NoBuffering: each thread picks the next available index one-at-a-time in enumeration order, so the LPT ordering in `CommandRegistry` is honoured."* Each worker renders into a per-index `CaptureSink`, replayed via `ReportDocReplay.Replay` (confirmed to exist, `DumpDetective.Reporting/ReportDocReplay.cs`) |

**This is a bigger, more directly actionable finding than the ClrMD-version hypothesis in
[performance-comparison.md](performance-comparison.md).** On a 31-analyzer catalog, running every
analyzer strictly one-after-another instead of 8-wide in parallel means the wall-clock cost of the
analyzer phase is close to the *sum* of every analyzer's individual runtime, not the runtime of the
single slowest one. If even a handful of this tool's 31 analyzers are BFS/graph-heavy (`DominatorAnalyzer`,
`GCRootAnalyzer`, `StaticRootLeakDetector`, `ReferenceChainAnalyzer` all call into `BoundedGraphWalk`),
sequential execution of all of them back-to-back is a direct, mechanical, non-hypothetical source of
wall-clock time that has nothing to do with ClrMD version and would show up on every single run
regardless of dump size. See [performance-comparison.md](performance-comparison.md) § Hypothesis 0
for how this changes the priority order.

## 5. Report / replay model

| | This tool | Other tool |
|---|---|---|
| Report data model | `AnalysisReportDocument` → formatters (`TextCanonicalReportFormatter`, `MarkdownCanonicalReportFormatter`, HTML renderer) render directly from the same document | `ReportDoc` (polymorphic `Chapter[] > Section[] > Element[]`, element kinds: Table/Alert/Text/KeyValues/CallTree/Gauges) is a serializable intermediate that `ReportDocReplay` feeds into **any** `IRenderSink` — this is what makes `render`/`diff` possible without re-opening the dump |
| Diffing two reports | Not a modeled capability | `ReportDiffer.Diff(a, b)` operates purely on two `ReportDoc` trees — no ClrMD, no dump access, pure data diff (row-matched tables, alert-matched-by-title, key-value diff) |

The other tool's `render`/`diff` capability isn't really a "feature," it's a direct consequence of
having a fully polymorphic, serializable report-document format as the *only* path to any output —
console, HTML, and JSON are all just different replays of the same `ReportDoc`. Retrofitting
`render`/`diff` onto this tool would require confirming `AnalysisReportDocument` is (or can become)
that same kind of pure, serializable, replayable structure, rather than bolting a diff command onto
the existing formatters.

## 6. Plugin system

The other tool's plugin system (`ICommand`/`ITraceSubAnalyzer`/`ITraceDumpCorrelationRule`,
assemblies dropped into `plugins/` or `~/.dumpdetective/plugins/`, scanned by `PluginLoader`) has no
equivalent here. This repo's `docs/analysis/phase-0/phase0-cross-analyzer-architecture-review.md`
lineage already flagged "plugin/discovery mechanism for analyzers" as a known gap (see the
`.vs/CopilotSnapshots` references to this in earlier planning notes) — this comparison confirms a
working, documented implementation of exactly that gap exists in a sibling codebase and could be
used as a reference design if third-party extensibility becomes a priority.

## 7. What this tool does more strictly

Not everything favors the other tool. This repo's `docs/performance-checklist.md` and
`CLAUDE.md` encode rules the other tool's docs don't explicitly state as enforced:

- A single canonical bounded-BFS primitive (`BoundedGraphWalk`, hard 20-depth cap enforced
  internally, not left to caller discipline) — the other tool's BFS is per-consumer
  (`BfsIndexBuilder`, `object-inspect --retained-cap`), with depth/size caps configured per call site
  rather than centrally enforced.
- Explicit "never build the full reverse graph in memory" as a stated, enforced constraint (scoped
  reverse index, hash-partitioned, disk-backed) vs. the other tool's `.parent.map` being a full
  child→parent map for every object in the heap (~1.2–1.3 GB for a ~100M-object heap) kept as a
  cache file — a legitimate design tradeoff (their approach is simpler and reusable across many
  query types; this repo's is more memory-conservative by construction) worth being explicit about
  rather than treating as strictly worse.

## 8. Retained-memory computation: exact dominator tree (ours) vs. BFS heuristic (theirs) — but their report reads clearer

**Algorithm: we're ahead, confirmed by source on both sides.** This branch has a real
Lengauer-Tarjan dominator-tree computation (`LengauerTarjan.cs`, `DominatorTreeComputer.cs`,
`DominatorTreeIndexWriter`/`Reader.cs`) wired into `DominatorAnalyzer`, producing exact retained
bytes for the Gen2/LOH sub-table when the exact path succeeds
(`RetentionOptions.EnableExactDominatorTree`, default on; falls back to the pre-existing bounded-BFS
heuristic on cap-exceeded or failure — see
[dominator-tree-lengauer-tarjan.md](../phase1-redesigns/dominator-tree-lengauer-tarjan.md) and
[dominator-tree-implementation-plan.md](../phase1-redesigns/dominator-tree-implementation-plan.md)).
A whole-repo text search (`grep -rli "dominator\|idom\|lengauer"`) across every `.cs` file in
`Rohit_DumpDetective` returns zero matches — they have no equivalent. Their "retained size" is
computed by `BfsIndexCache.ComputeRetained`/`BfsIndexBuilder.ComputeRetainedForFields`, a BFS
approximation over their 3-pass BFS index whose own return type — `(long Size, bool Estimated)` —
documents that it's a known-approximate estimate, not an exact dominance computation.

**Report clarity for "what's keeping this object alive": they're ahead, also confirmed by source.**
Reading `MemoryLeakReport.cs` (their `memory-leak` command) directly shows a deliberately narrated,
step-numbered report ("Step 1 — dumpheap -stat" → "Step 2 — Suspect Types" → "Step 3 —
Accumulation Pattern Checks" → "Step 4 — GC Root Chains"), where every section opens with an
explicit `sink.Explain(what, why, bullets, action)` block teaching the reader what the numbers mean
and what to do next. Critically, `RenderRootChains` renders each suspect type's actual retention
chain as literal ASCII art inline in the report (box-drawing characters `┌─`/`│`/`└►` confirmed
directly in `RenderRootChains`; example below is illustrative of the shape, not a literal quoted
sample):

```
┌─ Instance  0x00007f3a2c1b4020  (48 B)
│   → List<CacheEntry>
│   → Dictionary<string, CacheEntry>
└► ROOT  static field 'AppCache._entries' on Program
```

— chains are grouped/deduped by identical shape with an explicit `×N instances — same chain`
multiplicity marker, capped to the top 3 unique shapes per type, and the terminal ROOT step is
always labeled with its `RootKind` (HandleTable / thread stack / static field) inline, not in a
separate table.

**Our `DominatorSectionBuilder` does not do this today, and it isn't for lack of underlying data.**
We confirmed (via source, not assumption) that:
- `DominatorSectionBuilder`'s "Top dominator suspects" and "Gen2/LOH dominator suspects" tables
  render only `$"0x{type.SampleAddress:X}"` — a bare hex address, no chain, no root kind, no
  narrative.
- The typed data to do better already exists elsewhere in this codebase:
  `RootPathFinding` (`GCRootDomainResult.cs:22`) already carries `RootKind` and `PathTypeNames`,
  and `GCRootIntelligenceSectionBuilder` already assembles them into a first-class, serializable
  `RootPathGroup`/`RootPath` slot (`AnalyzerDetailSection.RootPathGroups`) — but only for the
  separate GC-root section, never linked from or reused by the dominator section. This is the same
  cross-reference gap Audit Area 1 of
  [dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md) already flagged
  ("an engineer diagnosing 'why is this object alive?' must consult three separate sections with no
  cross-reference").
- One structural caveat worth being precise about: `RootPathFinding.PathTypeNames` is documented in
  its own source comment as "forward BFS from target outward (owned subgraph), not root-to-target
  chain" — i.e. even where this data is used today, it isn't yet the same root→target chain shape
  their report renders. Porting their narrative-chain approach into the dominator section is more
  than a rendering change; see the audit doc's updated recommendations for the concrete scope.

See [dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md) for the specific,
source-verified improvement proposals this generated.

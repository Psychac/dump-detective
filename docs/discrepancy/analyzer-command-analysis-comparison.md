# Per-Analyzer Analysis-Approach Comparison

Scope: for each of this tool's analyzers, what does the equivalent command in
`Rohit_DumpDetective` actually *compute* — same algorithm, a cruder heuristic, a better one, or
nothing at all? This is the algorithm/data-model side of the comparison. Report *presentation*
(how the numbers are shown to the reader) is covered separately in
[analyzer-command-report-comparison.md](analyzer-command-report-comparison.md) — a pair can win on
one axis and lose on the other, as the Dominator/MemoryLeak pairs below show.

**Methodology**: every class/method named below was confirmed to exist via direct source read or a
`tokensave_search`/`tokensave_body` call against the actual code graph of the named repo — not
assumed from either tool's docs or README. Where a pair has been read closely enough to make a
specific algorithmic claim, it's listed under "Deep-dived pairs." Everything else is a
name/tag-based mapping only — verified to exist, not verified to be equivalent in approach. Treat
the mapping table as a worklist, not a finished comparison.

## Corrected counts

Earlier drafts of these discrepancy docs used "31 analyzers" (this tool) and "28 memory + 11 trace
commands" (other tool), both sourced from README-level descriptions. Reading `Modules` in
`DefaultAnalyzerFeatureModuleCatalog.cs` and listing files directly gives the actual counts:

| | This tool | Other tool |
|---|---|---|
| Count | **33 analyzers** (`DefaultAnalyzerFeatureModuleCatalog.Modules`, lines 18–50) | **36 files under `Commands/Memory/`** (34 real analysis commands + `LoadCommand`/`CloseCommand` cache-lifecycle utilities) + **30 files under `Commands/Trace/`** (27 real trace analyzers + `ITraceSubAnalyzer.cs`/`TraceEventTypesSection.cs`/`TraceOpener.cs` support files) + **2 top-level** (`RenderCommand`, `DiffCommand`) |

The other tool's surface is considerably larger than "28+11" suggested, mostly because of the trace
side, which has no counterpart on this tool at all (see § Trace side below).

## Mapping table — memory/heap-side pairs

| This tool's analyzer | Other tool's command(s) | Deep-dived? |
|---|---|---|
| `DominatorAnalyzer` | `object-inspect` (retained-size), `memory-leak`'s "Top Retainers" sub-table | **Yes** — see [pairs/dominator-analyzer-vs-object-inspect.md](pairs/dominator-analyzer-vs-object-inspect.md) |
| `LeakCandidateAnalyzer` | `MemoryLeakCommand` (`memory-leak`) | **Yes** — see [pairs/leak-candidate-analyzer-vs-memory-leak.md](pairs/leak-candidate-analyzer-vs-memory-leak.md) |
| `GCRootAnalyzer` | `GcRootsCommand` (`gc-roots`), `GcRootMapCommand` (`gc-root-map`) | **Yes** — see [pairs/gc-root-analyzer-vs-gc-root-map.md](pairs/gc-root-analyzer-vs-gc-root-map.md) |
| `LockGraphAnalyzer` | `DeadlockDetectionCommand` (`deadlock-detection`) | **Yes** — see [pairs/lock-graph-vs-deadlock-detection.md](pairs/lock-graph-vs-deadlock-detection.md) |
| `StaticRootLeakDetector` | `StaticRefsCommand` (`static-refs`) | No |
| `ReferenceChainAnalyzer` | folded into `gc-roots`'/`memory-leak`'s chain output, no dedicated command | No |
| `WeakReferenceAnalyzer` | `WeakRefsCommand` (`weak-refs`) | No |
| `EventLeakAnalyzer` | `EventAnalysisCommand` (`event-analysis`) | No |
| `TimerLeakAnalyzer` | `TimerLeaksCommand` (`timer-leaks`) | No |
| `FinalizableObjectAnalyzer` | `FinalizerQueueCommand` (`finalizer-queue`) | No |
| `GCHandleAnalyzer` | `HandleTableCommand` (`handle-table`), `PinnedObjectsCommand` (`pinned-objects`) | No |
| `LohFragmentationAnalyzer` | `LargeObjectsCommand` (`large-objects`) | No |
| `SegmentReservationAnalyzer` | *(no equivalent found)* | No — capability gap in the other tool's favor to note, not ours |
| `MemoryAnalyzer` | `HeapStatsCommand` (`heap-stats`) | No |
| `GCGenerationAnalyzer` | `GenSummaryCommand` (`gen-summary`) | No |
| `AllocationPatternAnalyzer` | `MemoryPressureCommand` (`memory-pressure`), `CachePatternsCommand` (`cache-patterns`) | No |
| `ObjectShapeAnalyzer` | `TypeInstancesCommand` (`type-instances`) | No |
| `HeapTopologyAnalyzer` | *(closest: `HighRefsCommand`, `high-refs`)* | No |
| `ModuleAnalyzer` | `ModuleListCommand` (`module-list`) | No |
| `StringAnalyzer` | `StringDuplicatesCommand` (`string-duplicates`) | No |
| `CollectionAnalyzer` | `DataTableAmpCommand` (`data-table-amp`, narrower — DataTable-specific), `CachePatternsCommand` overlap | No |
| `ArrayAnalyzer` | *(no dedicated equivalent found)* | No — gap in the other tool's favor |
| `BoxingAnalyzer` | *(no equivalent found)* | No — capability gap in **our** favor; the other tool has no boxing-specific analysis command |
| *(no equivalent here)* | `ClosureCaptureCommand` (`closure-capture`) | No — capability gap in the **other tool's** favor; we have no closure-capture-specific analyzer |
| `AsyncStateMachineAnalyzer` | `AsyncStacksCommand` (`async-stacks`) | No |
| `AsyncTaskAnalyzer` | folds into `AsyncStacksCommand` too — likely overlapping scope, not confirmed | No |
| `ThreadAnalyzer` | `ThreadAnalysisCommand` (`thread-analysis`) | No |
| `ThreadStackClusterAnalyzer` | folds into `thread-analysis`'s clustering — not confirmed as separate | No |
| `HangAnalyzer` | *(closest: `ThreadPoolCommand`, `thread-pool`)* | No |
| `CrashAnalyzer` | `ExceptionAnalysisCommand` (`exception-analysis`) | No |
| `DbConnectionAnalyzer` | `ConnectionPoolCommand` (`connection-pool`) | No |
| `WcfChannelAnalyzer` | `WcfChannelsCommand` (`wcf-channels`) | No |
| `HttpObjectAnalyzer` | `HttpRequestsCommand` (`http-requests`) | No |
| `JitAnalyzer` | *(no equivalent found)* | No — capability gap in **our** favor |
| *(no equivalent here)* | `NativeInteropCommand` (`native-interop`) | No — gap in the **other tool's** favor |
| *(no equivalent here)* | `HeapFragmentationCommand` (`heap-fragmentation`) | No — gap in the **other tool's** favor (already noted in [capability-comparison.md](capability-comparison.md)) |
| *(no equivalent here)* | `DiagnosticSummaryCommand` (`diagnostic-summary`) — likely a cross-analyzer roll-up, comparable to our `ExecutiveSummarySectionBuilder`/`InsightsSectionBuilder`, not a leaf analyzer | No |
| *(no equivalent here)* | `AnomalyDetectionCommand` (`anomaly-detection`, under `Commands/Trace/` but worth flagging here) | No |

## Trace side: entire category has no counterpart

All 27 real analyzers under `Commands/Trace/` (`AllocTraceCommand`, `CpuTraceCommand`,
`SqlTraceCommand`, `GcTraceCommand`, `ContentionTraceCommand`, `ThreadPoolStarvationCommand`,
`RootCauseTraceCommand`, etc. — full list in `git ls` of that directory) have zero equivalent here,
because this tool has no `.nettrace`/`.etl` ingestion path at all. This was already the headline
finding in [capability-comparison.md](capability-comparison.md) and isn't re-litigated
analyzer-by-analyzer here — there's nothing on this side to compare against.

## Cross-cutting: what "trend-analysis" actually compares across dumps, on both sides

Found while fixing a validation gap flagged directly by review: the four pairs' original "Trend /
multi-dump behavior" subsections asserted that a `TrendComparer` class existing (this side) or
`TrendAnalysisCommand` taking the same command list as `AnalyzeCommand` (their side) was enough
evidence — it wasn't. Read properly, by tracing the actual call path end to end on both sides, not
by confirming a class/constructor exists:

**This tool**: `TrendOrchestrationService.BuildSnapshot` collects `run.Result` for **every**
successful `AnalyzerRunResult` into `AnalysisSnapshot.DomainResults` — all 33 analyzers, generically,
every run. Each analyzer's registered `IAnalyzerTrendComparer.Compare(baseline, current)` then
produces a real `MetricDelta` per named metric key (e.g. `LockGraphTrendComparer` emits
`lock.contested`, `lock.max.waiters`, `lock.deadlock.candidates`; `GCRootTrendComparer` emits
`gcroot.total.roots`, `gcroot.strong.handle.count`, `gcroot.finalizer.count`, plus a
`gcroot.top.target.bytes`/`gcroot.top.target.severity` pair per named target type). This is a
structured, per-metric, per-analyzer before/after diff with an explicit `MetricTrendDirection`
(`HigherIsWorse`/`Neutral`) — the same mechanism for all 33 analyzers, confirmed generic by
construction, not asserted from the architecture in general.

**Other tool**: read `TrendAnalysisCommand.Run` end to end. Two distinct things happen, and they are
**not the same mechanism**:
1. `TrendAnalysisReport.RenderTrend(snapshots, sink, ...)` — the actual "trend" output (arrows,
   deltas) — takes `List<DumpSnapshot>` only. `DumpSnapshot` is the base heap-walk snapshot object;
   this is structurally the same scoping as `HealthScorer.Score` (§ Executive-summary participation,
   [analyzer-command-report-comparison.md](analyzer-command-report-comparison.md) §6). **The
   trend/delta mechanism itself only ever sees base heap-walk fields, never any of the 34 embedded
   commands' own domain data** (`DeadlockData`, `GcRootsData`, `LockGraphDomainResult`-equivalent,
   etc. — none of it reaches `RenderTrend`).
2. When `--full` is passed, `AnalyzeReport.RenderEmbeddedReports` is **re-run once per dump** and
   each dump's full rendered `ReportDoc` is captured (`capturedSubReports[i] = cap.GetDoc()`). These
   are then replayed **sequentially, one full per-dump report after another**
   (`ReportDocReplay.Replay(doc, sink)` in a loop over dumps) at the end of the trend output. So
   `deadlock-detection`'s (or any other embedded command's) data for every dump genuinely is present
   in a `trend-analysis --full` run — but **as N complete, independent report sections, not as a
   diff**. There is no `MetricDelta`-equivalent structure anywhere in this path comparing, say,
   `ConfirmedCycles.Count` between dump 1 and dump 3 — a reader gets three separate
   "Deadlock Detection" sections and has to compare the numbers by eye.

**Net**: this is a genuine, now properly confirmed (not asserted) asymmetry. This tool's trend
feature structurally compares every one of its 33 analyzers' actual domain data, per named metric.
The other tool's trend feature structurally compares only base heap-walk fields; its 34 embedded
commands' per-dump data is included in a `--full` run for completeness, but only as repeated,
undiffed full sections, not as a comparison. Each pair file's Trend subsection below states this
pair-specifically and links back here for the mechanism, per the "state the pair-specific
conclusion, point back to the cross-cutting doc for the shared mechanism" convention in
[pairs/README.md](pairs/README.md).

## Deep-dived pairs

All four pairs deep-dived so far live in their own files under [pairs/](pairs/README.md), one file
per pair — see that folder's index for the current list of done and pending pairs, plus a one-line
verdict for each:

- [pairs/dominator-analyzer-vs-object-inspect.md](pairs/dominator-analyzer-vs-object-inspect.md)
- [pairs/leak-candidate-analyzer-vs-memory-leak.md](pairs/leak-candidate-analyzer-vs-memory-leak.md)
- [pairs/lock-graph-vs-deadlock-detection.md](pairs/lock-graph-vs-deadlock-detection.md)
- [pairs/gc-root-analyzer-vs-gc-root-map.md](pairs/gc-root-analyzer-vs-gc-root-map.md)

## Suggested next pairs to deep-dive

See [pairs/README.md](pairs/README.md) for the current prioritized worklist — `GCRootAnalyzer` and
`LockGraphAnalyzer` (originally listed here as the top two candidates) are now done; the GC-Root
pair in particular refined the report-comparison doc's §2 finding — the "chain rendering" capability
turned out to be a single-analyzer investment on the other tool's side too
(`MemoryLeakAnalyzer.BuildChainBFS`), not a shared primitive they have and this tool lacks.

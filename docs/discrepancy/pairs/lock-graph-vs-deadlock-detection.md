# LockGraphAnalyzer vs. DeadlockDetectionCommand

| | This tool | Other tool |
|---|---|---|
| Analyzer/command | `LockGraphAnalyzer` (`src/DumpDetective.Analysis/Analyzers/LockGraphAnalyzer.cs`) | `DeadlockAnalyzer`/`DeadlockDetectionCommand` (`DumpDetective.Analysis.Memory/Analyzers/DeadlockAnalyzer.cs`, `DumpDetective.Commands/Memory/DeadlockDetectionCommand.cs`) |
| Report/renderer | `LockGraphSectionBuilder.cs` | `DeadlockReport.cs` |
| Domain model | `LockGraphDomainResult` / `LockContention` / `DeadlockCandidate` | `DeadlockData` / `MonitorLockEntry` / `DeadlockCycle` / `IndependentWaiter` / `WaitForEdge` |

Both read verbatim from source, not docs/README on either side.

## Analysis

### Data computed

**This tool**: `AllHeldLocks`, `ContestedLocks` (locks with ≥1 waiter), `DeadlockCandidateCount`,
`UnresolvedOwnerCount`, `LocksWithOwnerAddress`, `TopContestedTypes` (by cumulative waiter count),
`ContestedLockDetails` (address/type/waiters/owner/recursion), `DeadlockCandidateDetails` (thread
IDs, held lock types/addresses, blocked-at frame, up to 3 owner-thread stack frames).

**Other tool**: a superset in shape — `MonitorLocks` (address/type/owner/**explicit waiter ID
list**/recursion — this tool has no per-lock waiter list, only a count), `ConfirmedCycles`
(`DeadlockCycle`: ordered thread-ID cycle + parallel lock address/type arrays), `IndependentWaiters`
(threads blocked on non-Monitor primitives — `WaitHandle.WaitOne/WaitAny`, `Task.Wait`,
`SemaphoreSlim.Wait/WaitAsync`, `Thread.Join`, `ReaderWriterLockSlim.Enter*`, `Mutex.WaitOne` — all
individually classified), `WaitForGraph`/`WaitForEdge` (explicit adjacency list, each edge flagged
`IsInCycle`). **This tool has no concept of "independent waiter" at all** — a thread blocked on
`Task.Wait()` with zero lock ownership is invisible to `LockGraphAnalyzer` entirely, whereas the
other tool explicitly classifies and reports it (correctly, as non-deadlock-related) rather than
silently dropping it.

### Algorithm

**This tool (`BuildLockGraph`)**: a thread is a "deadlock candidate" if it (a) owns ≥1 lock
(`locksByOwnerManagedId.ContainsKey`) and (b) its own top stack frame substring-matches
`"monitor.wait"`/`"monitor.enter"`. **No wait-for graph is built, no edge connects a specific waiter
to the specific thread holding the specific lock it's blocked on, and no cycle detection runs.** Two
threads blocked on completely unrelated locks, owned by completely unrelated (non-blocked) threads,
would both be flagged as candidates.

**Other tool (`DeadlockAnalyzer.Analyze`)**: builds an actual `Dictionary<int,int> waitForGraph`
(waiter→owner) via `RebuildLocksWithWaiters`, then runs a textbook DFS cycle-detection algorithm
(`DetectCycles`/`Dfs` — path-tracking with an `onStack` position map, cycle extracted on revisiting a
node still on the current DFS path, deduplicated by thread-ID set, self-loops rejected). This is a
real, standard graph algorithm, not a heuristic, *given* a correct graph.

**The important nuance** (confirmed by the algorithm's own source comments, not assumed): both tools
hit the identical ClrMD limitation — `ClrSyncBlock` doesn't expose a per-lock waiter-thread list.
This tool's response is to give up on graph construction entirely. The other tool's response is to
still build a graph, but when there are *multiple* contested locks, waiters are distributed to locks
by **round-robin** (`RebuildLocksWithWaiters`'s own comment: "Heuristic: round-robin if multiple
locks"), not by actual evidence linking a specific waiter to a specific lock. The DFS step is exact
*given* the graph, but the graph itself can contain fabricated edges in the multi-lock case. In the
single-contested-lock case, the waiter→owner edge is unambiguous and a "confirmed" cycle really is
confirmed.

### Performance / complexity

**This tool**: one pass over `heap.EnumerateSyncBlocks()` (via `ObjectScanCounter`), one pass over
`runtime.Threads` building `threadByAddress`, one pass over `AllHeldLocks` building
`locksByOwnerManagedId` (pre-built specifically to avoid an O(M×N) lookup per the inline comment),
then one pass over threads for candidate detection. Reuses the shared `ThreadStackScanDispatcher`
single-pass stack walk (`IThreadStackScanParticipant`) instead of independently calling
`EnumerateStackTrace()` per thread when running inside the normal pipeline — an explicit, commented
optimization to avoid duplicating the stack walk `ThreadAnalyzer`/`HangAnalyzer`/
`ThreadStackClusterAnalyzer` also need. Falls back to an independent `EnumerateStackTrace()` call
only when invoked directly (tests/benchmarks), per `_participantScanSucceeded`.

**Other tool**: also a single `EnumerateSyncBlocks()` pass, plus a **second, independent** full
`t.EnumerateStackTrace().Take(20)` walk per thread inside `DeadlockAnalyzer.Analyze` itself — no
shared stack-scan dispatcher equivalent visible in this analyzer; if other commands in the same
`analyze --full` run also walk stacks independently, that's a repeated-work pattern this tool's
`IThreadStackScanParticipant` design specifically avoids. Not confirmed whether their architecture
has an equivalent shared-scan concept elsewhere — flagged as a follow-up if a full performance
audit of their thread-analysis commands is ever done.

### Correctness caveats

Covered above under Algorithm — this tool's caveat is total (no cycle detection at all, so no
"confirmed" claim is ever made, but also no real signal beyond co-occurrence); the other tool's
caveat is narrower and edge-case-specific (multi-lock round-robin assignment can produce a
"confirmed" cycle that isn't real), but doesn't apply in the common single-contested-lock case.

### Configuration & tunability

**This tool**: `LockGraphAnalysisOptions.MaxContestedLocksToShow` is bound to a persistent JSON
config file, confirmed via `ConfigurationResolver.BuildLockGraphAnalysisFromConfig` and the
`LockGraphAnalysisOptions` `[JsonSerializable]` registration in `CliConfigurationModels.cs` — a user
can edit `dumpdetective.config.json` (or equivalent) to change how many contested-lock rows show up,
without a rebuild, and it persists across every run.

**Other tool**: `DeadlockDetectionCommand`'s own `Help` text lists exactly two options —
`-o/--output` and `-h/--help`. **There are no tunable thresholds, caps, or limits at all** for this
command — no way to change how many locks/cycles are shown, no depth/breadth cap to adjust. This is
a real, confirmed gap in the other tool's favor for tunability generally (their targeted commands
like `gc-roots`/`object-inspect` do expose flags — see that pair's file — but `deadlock-detection`
specifically does not).

### Trend / multi-dump behavior

Mechanism fully confirmed by tracing the actual call path on both sides, not just confirming a class
exists — see
[analyzer-command-analysis-comparison.md](../analyzer-command-analysis-comparison.md) §
Cross-cutting: what "trend-analysis" actually compares. Pair-specific conclusion:

**This tool**: `LockGraphTrendComparer` emits three real metrics per dump with explicit
`MetricTrendDirection.HigherIsWorse` — `lock.contested`, `lock.max.waiters`,
`lock.deadlock.candidates` — each producing a real `MetricDelta` (baseline value, current value,
direction) between any two dumps via `Compare(baseline, current)`. A user running this tool's
`--trend` gets a structured "deadlock candidates went from 0 to 3" delta automatically.

**Other tool**: `deadlock-detection`'s data (`ConfirmedCycles.Count`, `MonitorLocks.Count`, etc.) is
**not** part of `TrendAnalysisReport.RenderTrend`'s comparison at all — that call only ever receives
`List<DumpSnapshot>`, the same base heap-walk object `HealthScorer.Score` reads. It's only present
in a `trend-analysis --full` run as N full, independently-rendered "Deadlock Detection" sections (one
per dump, via `ReportDocReplay.Replay` in a loop over dumps) — a reader has to manually compare the
`ConfirmedCycles.Count`/contested-lock numbers across those N sections by eye; there is no computed
delta for this command's data anywhere in the trend output.

### Validation

**This tool**: no dedicated test file found for `LockGraphAnalyzer` (`Glob` for `*LockGraph*.cs`
across the whole repo returns only production code plus `BenchmarkSuite1/LockGraphAnalyzerBenchmark.cs`
— a performance benchmark, not a correctness test). **There is no unit, integration, or discrepancy
test backing any claim about this analyzer's output correctness.**

**Other tool**: `DumpDetective.Tests/Integration/DeadlockTest.cs` exists and is a **real-scenario,
real-dump test** — `DeadlockScenario` (a `DiagnosticScenario`) presumably launches an actual
reproducer program that deadlocks itself, captures a real dump, and asserts
`Scenario.Validate(Doc)` + `DocAssert.HasContent(Doc)` against the rendered report. This is a
meaningfully stronger validation approach than a synthetic/mocked unit test would be — it tests the
whole pipeline (sync-block enumeration → wait-for graph → DFS → report) against a genuine deadlock,
not a fabricated `DeadlockData` record. **This is a confirmed, real gap in the other tool's favor**:
they have a test that could catch a real regression in cycle detection; this tool has no test that
could catch a regression in candidate detection at all.

## Report

### Data shown

**This tool**: renders `held_locks`, `contested_locks`, `max_waiters_on_single_lock`,
`deadlock_candidates`, `unresolved_owners` as key metrics; "Top contested lock types," "Contested
lock objects," "Deadlock candidate threads" (with a `Stack Trace` column), and a derived "Suspected
deadlock locks" table (contested locks owned by a thread that's *also* a deadlock candidate) as
compact tables.

**Other tool**: renders `TotalThreadsByRuntime`, `MonitorLocks.Count`, `contested`, `monitorWaiters`,
`IndependentWaiters.Count`, `ConfirmedCycles.Count`, `NamedThreadCount` as key-values; a full
"Monitor Lock Table" (address/type/owner/**explicit waiting-thread list**/waiter count/recursion —
richer than this tool's count-only waiter column); a dedicated "Independent Waiting Threads" table
plus per-thread collapsible stack traces (data this tool doesn't compute at all, see § Data
computed); and, when cycles exist, a dedicated "Deadlock Cycles" section.

### Presentation style

**This tool**: `BuildConfidenceBand` + a single-line `T(...)` narrative per outcome ("Potential
deadlock pattern detected...", "Lock contention present...", "No lock contention/deadlock candidates
detected.") + a `SectionLeadFinding` with a fixed `Recommendation` string when candidates exist.

**Other tool**: `sink.Explain(what, why, bullets, impact, action)` at the top of the report — note
this call site uses **five** named parameters, one more than the `what/why/bullets/action` shape
documented in [analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md) §1;
`Explain`'s actual signature includes a separate `impact` field distinct from `action`
(`DeadlockReport.cs`: `impact: "A confirmed deadlock means those threads will never make progress
without a process restart..."`, `action: "For confirmed cycles: identify the lock-acquisition
order..."`) — worth correcting that cross-cutting doc to note `impact` explicitly. Then a
severity-branched `sink.Alert` (Critical for confirmed cycles, Warning for contention-only, Info for
independent-waiters-only or nothing found), each with its own specific `advice` string tailored to
that branch.

### Severity/confidence communication

**This tool**: quantitative — `d.CalculateOwnerResolutionConfidence()` produces a 0.0–1.0 score
rendered as a 4-dot symbol (`●●●●`…`●○○○`), with a fixed caveat list attached to the `SectionLeadFinding`
explicitly stating the heuristic's limitations (quoted in full below). Severity itself is always
"Warning" when any candidate exists — there is no Critical tier for this analyzer, consistent with
never claiming a confirmed cycle.

**Other tool**: qualitative — `AlertLevel.Critical`/`Warning`/`Info` mapped directly from
`ConfirmedCycles.Count > 0` / `contested > 0` / neither. No numeric confidence score anywhere in this
report. Per the Algorithm section above, the Critical label is earned in the common
single-contested-lock case but can overclaim in the multi-lock round-robin case — and nothing in the
rendered report itself flags that narrower risk (the caveat lives only in `DeadlockAnalyzer.cs`'s
source comments, not in `DeadlockReport.cs`'s rendered output).

### Actionability of guidance

Both are genuinely specific here, not generic boilerplate — a rare case where neither tool is behind:

- This tool's caveats are evidence-specific to the method, not generic: *"Deadlock candidates are
  based on top-frame heuristics (Monitor.Wait/Enter) and do not confirm an actual cycle,"* *"Two
  independently blocked threads (unrelated locks) may both appear as candidates,"* *"Detection does
  not cover non-monitor primitives (ReaderWriterLockSlim, SemaphoreSlim, etc.)"* — this is an honest,
  precise self-disclosure of exactly the algorithmic gap identified in this file's Analysis section,
  rendered directly in the report rather than left in code comments only.
- The other tool's `action` text is branch-specific and concrete: *"Enforce a consistent
  acquisition order (always acquire Lock A before Lock B). Consider async alternatives with
  cancellation timeouts (SemaphoreSlim.WaitAsync with CancellationToken)"* for confirmed cycles,
  vs. *"Reduce the scope of your lock blocks, consider upgrading to ReaderWriterLockSlim..."* for
  contention-only — different, evidence-appropriate advice per severity tier, not one fixed string.

### Visualizations

**This tool**: none for this analyzer — no chart/gauge block in `LockGraphSectionBuilder`.

**Other tool**: `sink.Gauges([("Contested lock ratio", ...%), ("Threads blocked on locks", ...%)],
barMax: 100)` — a simple percentage-gauge/progress-bar visualization. Checked whether this tool has
an equivalent chart *kind*: `report.renderers.charts.js` supports `rankedbar`/`histogram`/`treemap`/
`heatmap`/`waterfall` — no `gauge` kind exists. **This is a small, confirmed, genuine presentation
gap**: not because this tool's chart repertoire is weaker overall (treemap/heatmap/waterfall are
individually more sophisticated than a gauge), but because a simple "quick percentage at a glance"
visualization has no equivalent here, and this specific analyzer would benefit from exactly that.

### Cross-analyzer / executive-summary participation

**This tool**: `LockGraphFindingGenerator`'s `InsightFinding` flows through the generic
`FindingGenerationPipeline` into `ExecutiveSummarySectionBuilder`/`HealthScorecardBuilder` — no
special-casing needed, confirmed by the architecture being uniform across all 33 analyzers (see
[analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md) §6).

**Other tool**: confirmed by tracing `AnalyzeCommand.Run`'s actual call order — `HealthScorer.Score(snap,
thresholds)` runs *before* `AnalyzeReport.RenderEmbeddedReports(...)` (which is what actually invokes
`DeadlockDetectionCommand`), and `HealthScorer.Score` only reads fields off the `DumpSnapshot` object
produced by the base heap-walk collection, not off any embedded command's own result.
**`DeadlockAnalyzer`'s confirmed cycles — even a genuinely confirmed one — cannot appear in this
tool's top-level health score or `BuildFindingsBullets` narrative**, no matter how severe. A reader
running `analyze --full` and only skimming the top summary would never see a real deadlock unless
they scroll to this command's own section.

### Drill-down / cross-referencing

**This tool**: the "Suspected deadlock locks" table cross-references `ContestedLockDetails` against
`DeadlockCandidateDetails`'s owner IDs internally within the same section — a genuinely useful,
already-inlined piece of drill-down (unlike the Dominator/Leak-Candidate pairs' bare-address
problem), because this analyzer's own data already contains both halves of the relationship.

**Other tool**: no cross-reference to other commands' output from within `DeadlockReport` — it's
self-contained (consistent with the "each command is its own report" design noted architecturally in
[analyzer-command-report-comparison.md](../analyzer-command-report-comparison.md) §3).

### Machine-consumability

**This tool**: `KeyMetrics["deadlock_candidates"]` — a short, stable, snake_case machine key
independent of display text; a script gating a build on this value doesn't need to parse any prose.

**Other tool**: `JsonSink` serializes the full typed `ReportDoc` (confirmed via source read of
`JsonSink.cs`: it delegates to `CaptureSink` then AOT-serializes the same structure `render`/`diff`
consume) — genuinely structured, not prose-dump. But the `KeyValues` entries use human-display key
strings (`"Confirmed deadlock cycles"`) rather than a short symbolic key — a script would match on
that exact display string, which is coupled to wording and would break silently if the label text
were ever reworded. Both are machine-parseable; this tool's key is more change-resistant.

### HTML interactivity

Checked directly, not assumed: this tool's `report.renderers.sections.js` defaults every table
column to `sortable: true` unless explicitly overridden; the other tool's `HtmlSink.cs` emits
`onclick="sortTable(...)"` with a sort-icon span on every table header unconditionally. **Parity —
both apply sortable columns to essentially every table by default**, including every table in this
specific pair. Neither tool's capability docs should claim this as a differentiator.

### Output format parity

Not fully verified line-by-line for this specific pair in this pass, but no structural reason to
expect asymmetry beyond what's already covered above (`Gauges` has no home in this tool's
Text/Markdown-equivalent renderers either, consistent with the chart-kind gap noted above).

## Bottom line

**Analysis**: the other tool is ahead — a real wait-for graph with DFS cycle detection, explicit
per-lock waiter lists, and a distinct "independent waiter" classification this tool doesn't have at
all — but their "confirmed" label overclaims specifically in the multi-contested-lock case, and this
tool has zero test coverage for this analyzer while the other tool has a real-scenario deadlock
reproducer test backing its correctness claim.

**Report**: closer to even than the Analysis gap suggests — this tool's confidence-scoring and
honest self-disclosed caveats are a genuine strength, its "Suspected deadlock locks" drill-down is
already inlined and useful, and both tools give evidence-specific (not generic) remediation advice.
The other tool's edges are the executive-summary participation gap (structural, in this tool's
favor) balanced against a small, real gauge/visualization gap and a more explicit per-severity-tier
narrative.

## Recommendations

- **Cheap, do now**: add a `gauge`-kind `ChartBlock` variant (contested-lock ratio, threads-blocked
  percentage) — small, well-scoped, no algorithmic work.
- **Cheap, do now**: write at least one real-scenario deadlock test for `LockGraphAnalyzer`,
  ideally reusing the same kind of induced-deadlock reproducer approach the other tool's
  `DeadlockScenario` uses, since this is the single most concrete, low-effort way to close the
  validation gap identified above.
- **Requires new algorithmic work**: implement the wait-for-graph + DFS-cycle-detection structure
  (this repo already has in-house graph-algorithm experience via `LengauerTarjan.cs`/
  `DominatorTreeComputer.cs`), and add an explicit `IndependentWaiter`-style classification for
  non-Monitor blocking primitives, which this analyzer currently doesn't distinguish from "not
  blocked at all."
- **Cheap, do now**: expose `LockGraphAnalysisOptions.MaxContestedLocksToShow` more visibly as a
  documented, discoverable config knob — it's already wired to the config file, just not
  highlighted anywhere a user would find it without reading source.

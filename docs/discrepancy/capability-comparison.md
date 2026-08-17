# Capability Comparison

Source: this repo (`upgrade/clrmd-4`) vs. `d:/POC/Rohit_DumpDetective` (README.md, Architecture.md,
source tree), read 2026-08-17.

## 1. Input types

| Input | This tool | Other tool |
|---|---|---|
| `.dmp` / `.mdmp` memory dumps | Yes | Yes |
| `.nettrace` / `.etl` traces | **No — does not exist** | Yes — `trace-analyze` runs 11 sub-analyzers (CPU, allocation, GC, exceptions, contention, threadpool-starvation, async, JIT, HTTP, SQL) in one pass over an opened `TraceLog` |
| Cross-source (trace + dump together) | **No** | Yes — `trace-dump-analyze`, 10 built-in correlation rules, plugin-extensible |
| Linux/.NET Core dumps | Not confirmed in current docs | Explicitly documented ("Windows and Linux/.NET Core 8") |

This is the largest capability gap. It is a different product surface, not a slower version of the
same one — closing it would mean building a new `DumpDetective.Analysis.Trace` layer from scratch,
not tuning an existing analyzer.

## 2. CLI shape

| Aspect | This tool | Other tool |
|---|---|---|
| Command model | Single root command (`System.CommandLine` with one `RootCommand`, no subcommands); one dump path argument, ~20 flags, always runs the full analyzer catalog (filtered via `--include-analyzers`/`--exclude-analyzers`) — confirmed by reading `RootCommandBuilder.cs` directly | Verb-based subcommands (`analyze`, `trend-analysis`, `render`, `diff`, `load`, `close`, `object-inspect`, `gc-roots`, `type-instances`, plus 28 memory + 11 documented trace commands) — every command class named here confirmed to exist via `tokensave_search` against the other repo's code graph, not just its README. One command exists in their code but isn't in their documented command table: `RootCauseTraceCommand` (`DumpDetective.Commands/Trace/RootCauseTraceCommand.cs`, implements both `ICommand` and `ITraceSubAnalyzer`, wraps `AsyncTraceAnalyzer` internally) — their actual command surface is at least one command larger than their own docs claim |
| Targeted single-object drill-down | **No** — flagged as a confirmed gap already in `docs/analysis/phase-0/phase0-deliverable-9-industry-benchmark.md` (`QueryEngine` only exposes `TopTypesBySize`/`ObjectsOfType`) | Yes — `object-inspect --address <hex> --retained [--depth N]` walks fields of one object with optional per-field BFS retained-size, backed by a reusable `.bfs.idx` cache |
| Ad hoc "all instances of type X" | Partial (`QueryEngine.ObjectsOfType`, not CLI-exposed as its own command) | Yes — `type-instances --type <name>` |
| Saved-report replay without re-opening the dump | **No** | Yes — `render <file.bin\|file.json>` re-renders any saved report to any format in under a second, no ClrMD involved |
| Diffing two saved reports | **No** | Yes — `diff before.bin after.bin` — row-matched table diff, alert/finding diff, key-value diff |
| Explicit cache lifecycle commands | **No** — cache is implicit, built/reused per run based on `--cache-dir`/colocated `.dumpindex/` resolution | Yes — `load <dump>` pre-builds all caches once; `close <dump>` deletes them; `--force` to rebuild |

## 3. Output formats

| Format | This tool | Other tool |
|---|---|---|
| Text | Yes | Yes |
| Markdown | Yes | Yes |
| HTML | Yes | Yes — with dark mode toggle, sticky nav, sortable/paged tables, embedded charts, decoded compiler-generated method names (`<Foo>b__N`, async state machines) in every stack/call-tree column |
| JSON | Yes, but narrowly: confirmed via source that `--report-format`/config only accept `text`/`markdown`/`html` (`RootCommandBuilder.ParseReportFormat` and `ConfigurationParseHelpers.ParseReportFormat` both throw `ArgumentException` for anything else, including `json`) — the only path to JSON output is `--separate-json`, which is gated to fire only when the primary format is `html` (`ReportOutputWriter.cs:28`: `if (resolved.Report.SeparateJson && resolved.Report.Format == ReportFormat.Html)`). `JsonCanonicalReportFormatter` and `ReportFormat.Json` exist internally but aren't independently CLI-selectable. | Yes — full `ReportDoc` structure, re-renderable |
| Compressed binary (Brotli) | **No** — confirmed by source: no `Brotli`/`BinSink`-equivalent anywhere in this repo | Confirmed via source — `DumpDetective.Reporting/Sinks/BinSink.cs` is a real, separate sink alongside `HtmlSink.cs`/`JsonSink.cs`/`MarkdownSink.cs`/`TextSink.cs`/`CaptureSink.cs`, used for `.bin` output and as the `render`/`diff` input format |
| Multiple outputs in one run | **No** — confirmed by source: `_outputPathOption` is a single `Option<string?>` (`RootCommandBuilder.cs`), not a repeatable/list option; `AnalysisCommandRequest.OutputPath` is a single `string?` | Yes — repeatable `-o`/`--format`, e.g. `-o report.html -o report.bin` in one invocation |

## 4. Memory-dump analyzer coverage

Both tools cover a similar breadth of memory/GC/thread/infra signals, but organized differently —
this tool as one 31-analyzer catalog with a shared `IHeapAnalysisCache`; the other as 28
CLI-addressable commands each backed by one or more `IHeapObjectConsumer`s over a single
`heap.EnumerateObjects()` walk. Rough correspondence:

| Signal | This tool (analyzer) | Other tool (command) |
|---|---|---|
| Heap by type/size | `MemoryAnalyzer` | `heap-stats` |
| GC generation breakdown | `GCGenerationAnalyzer` | `gen-summary` |
| Heap fragmentation | *(not a dedicated analyzer — see gap below)* | `heap-fragmentation` |
| LOH/large objects | `LohFragmentationAnalyzer` | `large-objects` |
| Pinned handles | `GCHandleAnalyzer` (general) | `pinned-objects` (dedicated) |
| Leak candidates + root chains | `LeakCandidateAnalyzer`, `DominatorAnalyzer`, `GCRootAnalyzer` | `memory-leak` |
| Highly-referenced "hub" objects | *(no direct equivalent — closest is `HeapTopologyAnalyzer`)* | `high-refs` |
| Duplicate strings | `StringAnalyzer` | `string-duplicates` |
| Finalizer queue | `FinalizableObjectAnalyzer` | `finalizer-queue` |
| GC handle table | `GCHandleAnalyzer` | `handle-table` |
| Static reference roots | `StaticRootLeakDetector` | `static-refs` |
| Weak references | `WeakReferenceAnalyzer` | `weak-refs` |
| Thread state / blocking | `ThreadAnalyzer`, `ThreadStackClusterAnalyzer` | `thread-analysis` |
| ThreadPool state | *(covered inside `HangAnalyzer`/`AsyncTaskAnalyzer`?  not a dedicated analyzer)* | `thread-pool` (dedicated) |
| Deadlock detection | `LockGraphAnalyzer` | `deadlock-detection` |
| Async state machines | `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer` | `async-stacks` |
| Exceptions | `CrashAnalyzer` | `exception-analysis` |
| Event handler leaks | `EventLeakAnalyzer` (+ fast-scan path) | `event-analysis` |
| HTTP in-flight / ServicePoint | `HttpObjectAnalyzer` | `http-requests` |
| DB connections | `DbConnectionAnalyzer` | `connection-pool` |
| WCF channels | `WcfChannelAnalyzer` | `wcf-channels` |
| Timers | `TimerLeakAnalyzer` | `timer-leaks` |
| Loaded modules | `ModuleAnalyzer` | `module-list` |
| Boxing | `BoxingAnalyzer` | *(no direct equivalent found)* |
| JIT | `JitAnalyzer` | *(dump-side: no equivalent; trace-side: `jit-trace`)* |
| Collections (Dictionary/List/etc. shape analysis) | `CollectionAnalyzer` | *(no direct equivalent found)* |
| Object shape / field layout | `ObjectShapeAnalyzer`, `ArrayAnalyzer` | *(no direct equivalent found)* |
| Segment reservation | `SegmentReservationAnalyzer` | *(no direct equivalent found)* |

Net: **this tool has broader per-object-shape/type-system analysis** (boxing, collection internals,
object shape, arrays, segment reservation); **the other tool has a few dedicated dump commands we
fold into broader analyzers** (`heap-fragmentation`, `high-refs`, `thread-pool` as first-class
signals rather than sub-signals of something else) — worth checking whether folding those into
broader analyzers loses report-level visibility for triage.

## 5. Extensibility

| Aspect | This tool | Other tool |
|---|---|---|
| Add a new analyzer | Implement `IAnalyzer`, then also `IFindingGenerator`, `IAnalyzerTrendComparer`, `IAnalyzerSectionBuilder`, then register one `Module(...)` entry — 4 coordinated types per analyzer (already flagged as friction in `docs/analysis/phase-0/phase0-deliverable-9-industry-benchmark.md`) | Implement `ICommand` (dump) or `ITraceSubAnalyzer`/`ITraceDumpCorrelationRule` (trace); drop the assembly in `plugins/` or `~/.dumpdetective/plugins/` — no rebuild of the host binary |
| Third-party plugin loading at runtime | **No — not a concept in this codebase** | Yes — `PluginLoader` scans configured directories, registers discovered `ICommand`s after built-ins (name conflicts silently favor built-ins); documented in `Docs/Plugins.md` with a working example plugin project |

## 6. Trend / multi-dump analysis

| Aspect | This tool | Other tool |
|---|---|---|
| Compare N dumps over time | Yes — `--trend` (semicolon-separated paths), `TrendOrchestrationService` → `TrendAnalyzer` | Yes — `trend-analysis d1.dmp d2.dmp d3.dmp`, directory or `--list file.txt` input, `--baseline` selection |
| Save raw snapshot data for later re-render | **No** | Yes — `trend-analysis --full -o snapshots.bin`, then `render`/`diff` against it any time without re-touching the dumps |
| Per-dump full sub-report extraction from a saved trend file | **No** | Yes — `render snapshots.bin --from 4 --command memory-leak` |

## 7. Health scoring

Both tools produce a health scorecard: this tool via `HealthScorecardBuilder`/
`TrendHealthScorecardBuilder` (domain-grouped severity/finding-count rollup, derived from
`InsightFinding.Severity` per analyzer run), the other tool via `HealthScorer.Score(DumpSnapshot,
ScoringThresholds)` (an explicit 0–100 score with per-finding point deductions, e.g. "-20 event leak
> 1000 subscribers," "-15 thread pool saturated"). Two concrete differences:

- **Externally tunable thresholds.** The other tool loads `dd-thresholds.json` from next to the
  executable at runtime (`ThresholdLoader`, silent fallback to compiled defaults if missing/invalid)
  — an ops team can retune severity cutoffs without a rebuild. No equivalent externally-overridable
  threshold file was found in this codebase (`grep` for `ThresholdConfig`/`thresholds.json` found
  no matches); severity cutoffs appear to be compiled into each analyzer/finding-generator.
- **Single numeric score vs. domain rollup.** The other tool surfaces one 0–100 number with a
  Healthy/Stable/Degraded/Critical label — a single at-a-glance triage signal. This tool's
  `HealthScorecardBuilder` produces a domain-grouped severity/finding-count breakdown rather than
  one blended number; that may be a deliberate choice (a single score can hide which domain is
  actually driving it) but it means there's no direct "one number" answer to "how bad is this dump"
  the way the other tool's `analyze` output gives immediately.

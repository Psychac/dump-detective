# Phase 0 — Deliverable 10: Platform Roadmap

> Scope: **Deliverable 10**, the final deliverable, from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Consolidates [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) through
> [Deliverable 9](phase0-deliverable-9-industry-benchmark.md) into a roadmap, and closes by
> explicitly answering the review's seven Success Criteria questions.

---

## Correction — 2026-07-21: verified heap-scan analyzer count

The "**Up to 26 of 36 analyzers**" figure used throughout this doc (Weaknesses, Biggest Risks
item 1, Success Criteria Q5/Q6/Q7) and in
[phase0-deliverable-5-shared-infrastructure.md](phase0-deliverable-5-shared-infrastructure.md)
item 1 / [phase0-deliverable-8-performance-architecture-review.md](phase0-deliverable-8-performance-architecture-review.md) §1
was self-flagged in Deliverable 8 as architectural/estimated, not measured. Direct
verification (grepping all `IAnalyzer` implementations under
`src/DumpDetective.Analysis/Analyzers/`) found a materially different, smaller number:

- **9 analyzers** stream the on-disk `HeapEntry` object index via
  `HeapAnalysisCache.EnumerateIndexedEntries()` / `EnumerateIndexedEntriesAsTuples()`:
  `DbConnectionAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer` (two call sites),
  `AsyncTaskAnalyzer`, `HangAnalyzer`, `EventLeakAnalyzer` (two call sites),
  `MemoryLeakAnalyzer`, `WcfChannelAnalyzer`, `StringAnalyzer`.
- **5 more analyzers** perform a full `ClrHeap.EnumerateObjects()` sweep with no index path at
  all: `TimerLeakAnalyzer`, `HttpObjectAnalyzer`, `FinalizableObjectAnalyzer`, plus
  `LohFragmentationAnalyzer`/`HeapTopologyAnalyzer` (per-segment, not whole-heap). These are
  architecturally distinct from the index-scan problem — a live ClrMD walk, not a read of the
  on-disk index — so a dispatcher built around `HeapAnalysisCache.EnumerateIndexedEntries()`
  cannot help them without a second, separate mechanism.

So **14 of 35** analyzers do some form of full/broad heap traversal, not 26 of 36, and only
**9 of 35** are addressable by a single shared index-scan dispatcher in one pass. This changes
the priority calculus without eliminating it:

- The single-pass dispatcher (P0, Performance track) remains correctly prioritized in kind —
  9 sequential full-index reads on a 10GB+ dump is still a direct threat to the "reasonable
  runtime" bar, and the dispatcher is still the highest-leverage single fix available. But the
  claimed blast radius (~26x) was roughly **3x smaller** than stated (~9x for the index path),
  so this item should be re-scored as high-value-but-not-uniquely-dominant rather than the
  runaway biggest risk on the roadmap — worth weighing against the Correctness track (evidence
  bus / leak-scoring fragmentation) with fresher eyes rather than assuming Performance
  automatically outranks it.
- The 5 `EnumerateObjects()`-based analyzers are **not** addressed by the planned dispatcher
  shape at all. If they matter for the 10GB+ goal, they need to be tracked as a distinct,
  currently-unscoped follow-up (a second dispatcher variant wrapping live
  `ClrHeap.EnumerateObjects()` fan-out, or migrating each onto the on-disk index first) —
  this roadmap did not previously call that out as a separate risk.

See [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md) for
the implementation plan built on this verified breakdown, including a proof-of-concept scoped
to `DbConnectionAnalyzer` only pending re-prioritization.

## Re-prioritization — 2026-07-21: dispatcher demoted from P0 to P1

Following the verified count above, the single-pass index scan dispatcher is reclassified from
Immediate Priorities (P0) to Near-term (P1), and the Correctness track (evidence bus /
leak-scoring fragmentation) now leads P0 alone. Three reasons:

1. **Blast radius is 3x smaller than originally estimated** (9x, not 26x) — a real cost on
   10GB+ dumps, but no longer plausibly the platform's single largest architectural risk without
   re-weighing against fragmented leak evidence (Biggest Risk #2).
2. **Implementation review surfaced a real, unbudgeted blocker**: `DbConnectionAnalyzerDiscrepancyTests.cs`
   and likely other call sites invoke `analyzer.AnalyzeAsync()` directly, bypassing
   `AnalysisPipeline` — the mechanism that would prime dispatcher-participant state before
   `AnalyzeAsync` runs. Difficulty was already rated High before this was found; see
   [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md)'s
   "Open design problem" section.
3. **Fragmented leak evidence is a product-credibility risk, not just a code-quality one**
   (Deliverable 9) — independent of and non-blocking relative to the dispatcher, and a closer
   match to what actually differentiates DumpDetective from dotMemory today.

The dispatcher is not deprioritized to "someday" — it stays P1, ahead of the other P1 items, and
should be revisited once either the direct-`AnalyzeAsync`-call blocker has a clear resolution, or
a profiling run (Deliverable 8's own open item) confirms the 9x scan cost is significant on a
representative 10GB+ dump, whichever comes first.

## Correction — 2026-07-21: inter-analyzer result bus confirmed as new work, not `Order`-derived

Deliverable 5 item 11 previously flagged the `Order` field as a plausible existing implementation
of the inter-analyzer result bus, pending confirmation. Direct verification found it is not:

- `IAnalyzer.Order` is consumed in exactly one place, `AnalyzerFilterService.Order()`
  (`src/DumpDetective.Cli/Execution/AnalyzerFilterService.cs:53`), which sorts analyzers purely to
  determine execution and report-section sequence.
- `AnalysisContext` carries only `Runtime`, `Heap`, `Cache`, `AnalysisOptions`, `Diagnostics`,
  `DiagnosticsSink`, `Progress` — no field through which one analyzer could read another's result.
- `AnalysisPipeline.ExecuteAsync` (`src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs:24`)
  runs analyzers sequentially but never threads a running results collection back into the context
  mid-loop; each `AnalyzerRunResult` is only appended to a local list.
- A working precedent for the *shape* the bus should take already exists, though:
  `InsightEngine.FindResult<T>(IReadOnlyList<AnalyzerRunResult> runs)`
  (`src/DumpDetective.Analysis/Insight/InsightEngine.cs:1574`) already performs the typed
  cross-analyzer lookup the bus needs, but only post-hoc — called from `InsightEngine.Analyze`
  after the full run completes (`AnalysisPipeline.cs:257`) — and is currently `private` to
  `InsightEngine`.

**Recommended shape**: a **post-hoc** bus (generalizing `FindResult<T>` into a public post-run
query surface), not a live/mid-run one keyed off `Order`. A live bus would make correctness depend
on execution order staying stable as analyzers are added/reordered — the same "precedent" risk
already flagged for `HeapTopologyAnalyzer → Pipeline` (Biggest Risk #3) — and would fight
`IsThreadSafe`/future parallel-execution work. This does not change item 11's priority or its
position blocking items 6/8/9 — it only resolves the previously-open question of whether it's new
work (it is) and specifies the implementation shape.

See [phase0-deliverable-5-shared-infrastructure.md](phase0-deliverable-5-shared-infrastructure.md)
item 11 for the full analysis.

**Update — 2026-07-21: implemented.** See the Correctness track entry below for the shipped shape.

---

## Current Architecture Assessment

### Strengths

- **The core architectural bet is right.** An extensible `IAnalyzer` catalog producing automated,
  cross-cutting, single-pass analysis of a static dump is closest in spirit to dotMemory's
  automated-inspection philosophy — the highest-value approach of the four industry tools
  benchmarked (Deliverable 9). Nothing in this review suggests abandoning that bet.
- **Genuinely strong extensibility relative to the industry.** Adding a new analyzer is "implement
  an interface and register it," versus WinDbg's native-extension-DLL friction or PerfView's
  internal-event-model friction (Deliverable 9).
- **Breadth no single comparison tool matches.** Memory, GC, threads, locks, exceptions, and leak
  candidates in one pass is a real differentiator over WinDbg + PerfView + dotMemory run
  separately (Deliverable 9).
- **The type-metadata caching layer is correctly designed.** `HeapAnalysisCache` is shared by
  nearly every analyzer for `MethodTable → ClrType` resolution — the one piece of the caching
  story that was already right before this review (Deliverable 4 §3, Deliverable 8 §4).
- **Several analyzers are reference examples of correct scoping**: `SegmentReservationAnalyzer`
  and `JitAnalyzer` are correctly isolated from the object index they don't need
  (Deliverable 1/3/6); `StringAnalyzer` stays appropriately large-but-single-purpose without
  drifting into scope creep (Deliverable 1).
- **No analyzer earned an outright removal verdict** (Deliverable 6) — the platform's problem is
  duplication and coupling, not wasted capability.

### Weaknesses

- **9 of 35 analyzers independently stream the full on-disk object index**, with no shared
  single-pass dispatcher (Deliverable 4 §1, Deliverable 8 §1) — see the
  [Correction](#correction--2026-07-21-verified-heap-scan-analyzer-count) above; originally
  estimated at "up to 26 of 36," verified smaller. A further 5 analyzers do a full live
  `ClrHeap.EnumerateObjects()` sweep, which this dispatcher cannot address. Still the single
  largest weakness in the platform in kind, though its blast radius is ~3x smaller than
  originally stated.
- **Leak/retention evidence is fragmented across 6 analyzers** with no unified scoring or
  confidence model (Deliverable 3, 5, 7, 9) — the platform's weakest point relative to the
  industry benchmark specifically.
- **At least 4 duplicate-logic clusters exist by convention, not by contract**: the resource-state
  sampler quartet, the thread-domain quartet, the static-field sweep pair, and the handle-table
  trio (Deliverable 1, 3, 4, 7).
- **One analyzer boundary is simply wrong**: `ModuleAnalyzer`/`AppDomainAnalyzer` overlap, compounded
  by `AppDomain` being a largely vestigial concept in modern .NET (Deliverable 6).
- **A handful of infrastructure-leakage outliers**: `CollectionAnalyzer`'s lone logging dependency,
  `AsyncTaskAnalyzer`'s private on-disk index format, `HeapTopologyAnalyzer`'s dependency on the
  orchestration layer (Deliverable 3, 7, 8).
- **Real capability gaps remain**, most notably DI-container leak detection, EF Core awareness,
  and crash minidump exception-stream triage (Deliverable 2, 9).

### Biggest Risks

1. **The ~9x on-disk index-scan multiplier (verified; originally estimated at ~26x — see
   Correction above) is a direct threat to the project's own definition of done** ("works on
   10GB+ dumps... reasonable runtime," CLAUDE.md). This is not a theoretical concern — it's a
   structural mismatch between the platform's stated performance goal and its current execution
   model, though smaller in magnitude than first estimated and no longer unambiguously the
   single biggest risk on this list without re-weighing against risk #2.
2. **Fragmented leak evidence threatens the product's credibility**, not just its code quality —
   Deliverable 9 showed this is the one gap that actually undermines DumpDetective's core value
   proposition against the tool it's most philosophically similar to (dotMemory).
3. **The `HeapTopologyAnalyzer` → `Pipeline` dependency is a small violation today that risks
   metastasizing** as more analyzers are added without an enforced dependency direction
   (Deliverable 7) — cheap to fix now, more expensive the longer it's the "precedent."
4. **The 4x registration fan-out compounds every future analyzer addition and every Deliverable 6
   merge/split**, and nothing currently prevents it from growing unchecked as the analyzer count
   increases past 36 (Deliverable 7, 9).

---

## Immediate Priorities (P0)

Per the [2026-07-21 re-prioritization](#re-prioritization--2026-07-21-dispatcher-demoted-from-p0-to-p1)
above, the index scan dispatcher (formerly this section's Performance track) has moved to
[Near-term (P1)](#near-term-p1). The Correctness track below is now the sole P0 track.

**Correctness track**
- Inter-analyzer result bus (Deliverable 5 item 11) — **done 2026-07-21.** Implemented as a
  post-hoc bus: `AnalyzerRunResultsExtensions.GetResult<T>(this IReadOnlyList<AnalyzerRunResult>
  runs)` (`src/DumpDetective.Core/Models/AnalyzerRunResult.cs`), an `internal` extension usable
  from any assembly with `InternalsVisibleTo` on `DumpDetective.Core` (Analysis, Reporting, Cli,
  Tests, BenchmarkSuite1). `InsightEngine.FindResult<T>` (`InsightEngine.cs:1574`) now delegates
  to it instead of duplicating the scan. Confirmed no live/mid-run channel was added —
  `AnalysisContext` is unchanged; consumers call this only after the pipeline finishes, same as
  `InsightEngine.Analyze` already did. See Deliverable 5 item 11 for the full analysis.
- Evidence builder (Deliverable 5 item 6) and replace `LeakCandidateAnalyzer`'s scanning strategy
  with an aggregation strategy over it (Deliverable 6) — unblocked; can consume
  `AnalyzerRunResultsExtensions.GetResult<T>` directly.
- Confidence scoring wired to the existing `ConfidenceSectionBuilder` (Deliverable 5 item 9) —
  design together with the ranking engine, not after it.

**Both tracks**
- Fix the `HeapTopologyAnalyzer` → `Pipeline` dependency (Deliverable 7) — cheap now, and doing it
  before the dispatcher work establishes the dependency-direction discipline the dispatcher itself
  needs to respect.

---

## Near-term (P1)

- **Single-pass index scan dispatcher** (Deliverable 5 item 1, Deliverable 8 §1; demoted from P0
  — see [2026-07-21 re-prioritization](#re-prioritization--2026-07-21-dispatcher-demoted-from-p0-to-p1)
  above) — addresses 9 of 35 analyzers (verified), not 26 of 36 as originally estimated. Still a
  real fix for 10GB+ dump performance, but sequence after the P0 Correctness track and after the
  direct-`AnalyzeAsync`-call blocker documented in
  [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md) has a
  resolution, or a profiling run confirms the 9x scan cost is significant on a representative
  10GB+ dump.
- Per-type statistics computed once inside that same pass (Deliverable 5 item 2) — cheap once the
  dispatcher exists, removes a correctness risk (disagreeing "total bytes" numbers across reports).
- Root/retention graph service: route `RetentionAnalyzer`(→merged into `DominatorAnalyzer`),
  `StaticRootLeakDetector`, `EventLeakAnalyzer` through the shared `Traversal` primitive
  (Deliverable 5 item 3, Deliverable 8 §3).
- Shared type-classification layer for the 8 analyzers currently rolling their own type-name
  pattern matching (Deliverable 5 item 4).
- Object metadata classification (generation/segment bucket) computed once, sequenced after the P0
  dispatcher (Deliverable 5 item 5).
- Shared typed-resource sampler for the Db/Wcf/Http/Timer quartet (Deliverable 5 item 7).
- Execute the three Deliverable 6 merges: `AppDomainAnalyzer` into `ModuleAnalyzer`,
  `RetentionAnalyzer` into `DominatorAnalyzer`, `DependentHandleAnalyzer` into `GCHandleAnalyzer`.
- Move `AsyncTaskAnalyzer`'s private task-index format fully behind `Indexing.Container`; resolve
  `CollectionAnalyzer`'s logging dependency one way or the other (Deliverable 7).
- Introduce shared contracts for the resource-sampler and thread-domain quartets so they're
  coupled by compiler-checked interface, not copy-paste convention (Deliverable 7).
- Close the crash-triage gap: confirm and, if needed, add minidump exception-stream parsing to
  `CrashAnalyzer` (Deliverable 2, 3, 9 — validated as a real, closeable gap against WinDbg's
  `!analyze -v`, not a case of chasing parity blindly).
- Add runtime-configuration reporting (GC mode, heap count, TieredCompilation) — cheap, high value,
  currently unowned by any analyzer (Deliverable 2).
- Verify the actual depth of `QueryEngine` (ad hoc object inspection) and `Analysis.Trend.Comparers`
  (snapshot diffing) before scoping any related capability as new work (Deliverable 9).

---

## Medium-term (P2)

- Dependency-injection scoped-service leak detection — highest-value missing capability from
  Deliverable 2, but real engineering effort (walking `IServiceProvider` internals); sequence
  after the P0/P1 infrastructure exists to build it on.
- EF Core–aware diagnostics and cache-health analysis (`IMemoryCache`/static caches) — both
  naturally reuse the P1 sampling framework and type-classification layer, so are cheaper once
  those land (Deliverable 2, 5).
- Native/unmanaged memory and COM interop (RCW/CCW) tracking (Deliverable 2, 9).
- Confirm whether container/satellite indexes are truly rebuilt per analyzer invocation or already
  cached across a session — open question from Deliverable 8 §2/consolidation item 3.
- Reporting-helper consolidation: collapse the resource-sampler quartet's near-identical
  `SectionBuilder`s, and confirm whether per-analyzer "top types" sections are redundant against
  the global `TypeSystemSectionBuilder` (Deliverable 4 §6, Deliverable 9 Better UX).
- Resolve `FinalizableObjectAnalyzer`'s scope ambiguity — confirm whether "has finalizer,
  undisposed" and "on the finalization queue" are being conflated (Deliverable 3, 6).
- Simplify the 4x analyzer-registration fan-out (sensible defaults for
  generator/comparer/section-builder types) before the analyzer count grows materially past 36
  (Deliverable 7, 9).

---

## Long-term (P3)

- ASP.NET-specific diagnostics, `System.Threading.Channels` support, reflection-cache growth
  detection, resurrection detection, native (non-managed) thread enumeration, general
  object-ownership / non-string duplicate-object detection — all real Deliverable 2 gaps, but
  lowest urgency and/or novel engineering (Deliverable 2, 9).
- Pinned-object/POH-specific reporting (Deliverable 2).
- A future interactive visualization layer for retention-path evidence — explicitly deferred, not
  rejected, by Deliverable 9: worth revisiting only once report evidence quality (P0 correctness
  track) is solid, so it complements rather than competes with that work.
- Deepen `QueryEngine` into a full ad hoc exploration capability, if Deliverable 9's verification
  step finds today's version shallow relative to WinDbg's manual exploration power.

---

## Success Criteria

Answering the review protocol's seven closing questions directly:

**1. Does every analyzer have a clearly defined owner and responsibility?**
Not today, but close after the fixes above. Deliverable 3 found clean, unambiguous ownership for
roughly two-thirds of the 36 analyzers. The rest fall into a small number of well-defined overlap
clusters (module/domain, leak/retention scoring, the handle trio, the thread quartet, the
resource-sampler quartet) rather than being scattered ambiguity — meaning the fix is scoped and
tractable, not a sign of pervasive architectural confusion.

**2. Are any analyzers redundant?**
No analyzer is wholly redundant — Deliverable 6 found zero removal candidates after deliberately
checking rather than assuming. Three pairs are duplicative enough to merge, and one
(`LeakCandidateAnalyzer`) needs a strategy replacement, but every one of the 36 maps to a real,
distinct diagnostic capability.

**3. Which analyzers should merge or split?**
Merge: `AppDomainAnalyzer` into `ModuleAnalyzer`, `RetentionAnalyzer` into `DominatorAnalyzer`,
`DependentHandleAnalyzer` into `GCHandleAnalyzer` (36 → 33 analyzers). No mandatory splits;
`CollectionAnalyzer`'s size is a scope-creep flag addressed by extracting shared infrastructure
(reflection cache) rather than splitting the analyzer itself, with a literal split left as a
conditional future option only if its scope keeps growing (Deliverable 6).

**4. Which platform capabilities are missing?**
Ranked by validated priority (Deliverable 2, filtered through Deliverable 9's "don't chase parity
blindly" test): DI-container leak detection, crash minidump-stream triage, runtime-configuration
reporting, EF Core diagnostics, cache health, native/COM interop, ASL-specific leak detection
(distinct from the legacy AppDomain framing being retired), POH reporting, ASP.NET diagnostics,
and lowest-priority: `System.Threading.Channels`, reflection-growth detection, resurrection
detection, native thread enumeration, general object-ownership/duplicate detection. Explicitly
excluded as non-goals: allocation call-stack hotspots and live ETW timelines (architecturally
impossible from a static dump) and a full interactive GUI (strategically premature).

**5. Which expensive operations should become shared infrastructure?**
In priority order (Deliverable 5, 8): the object-index scan itself (dispatcher — addresses 9 of
35 analyzers per the verified Correction above, not all of them), per-type statistics reduction,
root/static enumeration, the handle-table walk, the thread-stack walk, type classification,
reflection field-layout caching, and the typed-resource sampler.

**6. What architectural changes would most improve correctness, scalability, and maintainability?**
Scalability: the single-pass index dispatcher — high-leverage for the 9 index-scanning analyzers
it covers (verified breakdown above), though "nothing else matters at 10GB+ scale if this isn't
fixed" overstates it now that the multiplier is known to be ~9x, not ~26x, and 5 analyzers
(`EnumerateObjects()`-based) sit outside its reach entirely. Correctness: the inter-analyzer
result bus feeding a shared evidence/ranking/confidence engine, which turns 6
independently-scored leak signals into one credible answer — this is worth weighing as
co-equal with, not automatically subordinate to, the dispatcher now that the latter's blast
radius is verified smaller. Maintainability: enforcing the dependency direction from
Deliverable 7 (no analyzer depends on Pipeline or Reporting) and reducing the 4x registration
fan-out before the analyzer count grows further.

**7. If DumpDetective were redesigned today, what would its analyzer architecture look like?**
Roughly 33 analyzers (post-merge); of those, the 9 verified index-scanning analyzers (Correction
above) would each expose a per-object visitor callback consumed by one shared dispatcher instead
of independently streaming the index — the 5 `EnumerateObjects()`-based analyzers would need an
analogous but distinct live-heap fan-out mechanism, not this same dispatcher. A per-type statistics artifact and
per-object generation/segment classification computed once per run and handed to every analyzer,
rather than re-derived. A single canonical root/retention graph service (built on the existing
`Traversal` primitive) that every leak-adjacent analyzer depends on instead of implementing its own
walk. Leak-adjacent analyzers emit structured evidence into one evidence/ranking/confidence engine
that is the platform's sole scoring authority, rather than each computing and reporting its own
severity. Analyzer registration carries sensible defaults so adding a new analyzer doesn't
necessarily require four coordinated types. And a strictly enforced dependency direction — Core →
shared infra → analyzers → trend comparers → reporting → orchestration — with no exceptions of the
kind `HeapTopologyAnalyzer` currently represents. Notably, this is an evolution of the current
design, not a rewrite: every piece of it already exists in some form in today's codebase
(`Traversal`, `HeapAnalysisCache`, `TypeIndexBuilder`, `InsightEngine.FindResult<T>` as the
post-hoc-bus precedent, `ConfidenceSectionBuilder`) — the work is consolidation and enforcement,
not reinvention. (`Order` itself is not part of this list — confirmed 2026-07-21 to be
execution/report sequencing only, not a data channel between analyzers.)

# Phase 0 — Deliverable 10: Platform Roadmap

> Scope: **Deliverable 10**, the final deliverable, from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Consolidates [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) through
> [Deliverable 9](phase0-deliverable-9-industry-benchmark.md) into a roadmap, and closes by
> explicitly answering the review's seven Success Criteria questions.

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

- **Up to 26 of 36 analyzers independently perform a full heap-index scan**, with no shared
  single-pass dispatcher (Deliverable 4 §1, Deliverable 8 §1) — the single largest weakness in the
  platform.
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

1. **The ~26x heap-scan multiplier is the most direct threat to the project's own definition of
   done** ("works on 10GB+ dumps... reasonable runtime," CLAUDE.md). This is not a theoretical
   concern — it's a structural mismatch between the platform's stated performance goal and its
   current execution model.
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

Two independent tracks, neither blocking the other (Deliverable 5) — both must outrank every P1/P2
item below:

**Performance track**
- Single-pass index scan dispatcher (Deliverable 5 item 1, Deliverable 8 §1) — the highest-leverage
  change available; unblocks the 10GB+ dump performance goal.
- Per-type statistics computed once inside that same pass (Deliverable 5 item 2) — cheap once the
  dispatcher exists, removes a correctness risk (disagreeing "total bytes" numbers across reports).

**Correctness track**
- Inter-analyzer result bus (Deliverable 5 item 11) — confirm first whether `AnalysisContext`
  already supports this via the existing `Order` field before treating it as new work.
- Evidence builder (Deliverable 5 item 6) and replace `LeakCandidateAnalyzer`'s scanning strategy
  with an aggregation strategy over it (Deliverable 6) — contingent on the result bus.
- Confidence scoring wired to the existing `ConfidenceSectionBuilder` (Deliverable 5 item 9) —
  design together with the ranking engine, not after it.

**Both tracks**
- Fix the `HeapTopologyAnalyzer` → `Pipeline` dependency (Deliverable 7) — cheap now, and doing it
  before the dispatcher work establishes the dependency-direction discipline the dispatcher itself
  needs to respect.

---

## Near-term (P1)

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
In priority order (Deliverable 5, 8): the object-index scan itself (dispatcher), per-type
statistics reduction, root/static enumeration, the handle-table walk, the thread-stack walk, type
classification, reflection field-layout caching, and the typed-resource sampler.

**6. What architectural changes would most improve correctness, scalability, and maintainability?**
Scalability: the single-pass index dispatcher, by a wide margin — nothing else on this roadmap
matters at 10GB+ scale if this isn't fixed. Correctness: the inter-analyzer result bus feeding a
shared evidence/ranking/confidence engine, which turns 6 independently-scored leak signals into
one credible answer. Maintainability: enforcing the dependency direction from Deliverable 7 (no
analyzer depends on Pipeline or Reporting) and reducing the 4x registration fan-out before the
analyzer count grows further.

**7. If DumpDetective were redesigned today, what would its analyzer architecture look like?**
Roughly 33 analyzers (post-merge), each exposing a per-object visitor callback consumed by one
shared dispatcher instead of independently streaming the index. A per-type statistics artifact and
per-object generation/segment classification computed once per run and handed to every analyzer,
rather than re-derived. A single canonical root/retention graph service (built on the existing
`Traversal` primitive) that every leak-adjacent analyzer depends on instead of implementing its own
walk. Leak-adjacent analyzers emit structured evidence into one evidence/ranking/confidence engine
that is the platform's sole scoring authority, rather than each computing and reporting its own
severity. Analyzer registration carries sensible defaults so adding a new analyzer doesn't
necessarily require four coordinated types. And a strictly enforced dependency direction — Core →
shared infra → analyzers → trend comparers → reporting → orchestration — with no exceptions of the
kind `HeapTopologyAnalyzer` currently represents. Notably, this is an evolution of the current
design, not a rewrite: every piece of it already exists in some form in today's codebase (`Traversal`,
`HeapAnalysisCache`, `TypeIndexBuilder`, the `Order` field, `ConfidenceSectionBuilder`) — the work
is consolidation and enforcement, not reinvention.

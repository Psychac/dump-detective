# Phase 0 — Deliverable 9: Industry Benchmark

> Scope: **Deliverable 9 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Compares DumpDetective's overall platform architecture — not implementation details — against
> WinDbg + SOS, PerfView, Visual Studio Memory Usage, and JetBrains dotMemory. Per the doc's
> explicit instruction, this does **not** chase feature parity blindly — several capabilities
> those tools have are excluded from DumpDetective's roadmap on purpose, and that reasoning is
> stated explicitly rather than left implicit.

## The Four Tools, in One Line Each

- **WinDbg + SOS** — lowest-level ground truth, free, scriptable, manual, works on any dump but
  correlates nothing automatically.
- **PerfView** — ETW *trace*-based (not snapshot-based); the only tool of the four with
  call-stack-level allocation and time-series GC/CPU data.
- **Visual Studio Memory Usage** — IDE-integrated live/dump snapshot diffing with a visual
  path-to-root graph; GUI-first, Windows-only.
- **JetBrains dotMemory** — commercial profiler with automated "inspections" that rank likely
  problems and group retention paths; the closest industry analog to DumpDetective's core premise
  of automated, cross-cutting insight generation rather than manual exploration.

## Missing Capabilities

| Capability | Present in | Close this gap? | Why |
|---|---|---|---|
| Automated crash-triage from the minidump exception stream (`!analyze -v` equivalent) | WinDbg | **Yes** | Already flagged as unclear/partial for `CrashAnalyzer` in Deliverables 2, 3, 7 — this is a real, closeable gap, not an architectural mismatch |
| Native/unmanaged memory and COM RCW/CCW tracking | WinDbg, dotMemory | **Yes** | Already flagged as fully missing in Deliverable 2; a common real-world leak source no current analyzer touches |
| Ad hoc/interactive object inspection (browse arbitrary object/field by address) | WinDbg, dotMemory | **Verify first** | `architecture.md`'s `QueryEngine` suggests this may already exist in some form — confirm its depth before treating as a gap to build from scratch |
| Snapshot-to-snapshot diffing (survived objects, % growth ranking) | VS, dotMemory | **Verify first** | `Analysis.Trend.Comparers` (Deliverable 1/7) is the architectural equivalent — confirm it covers the same axes (survived-object tracking, ranked growth) VS/dotMemory expose before scoping new work |
| Interactive visual graph of retention paths | VS, dotMemory | **No — defer** | Presentation gap, not a data gap: `ReferenceChainAnalyzer` already computes the underlying path data (Deliverable 3/5). A future UI layer could render it; building one now would distract from the Phase 0 consolidation work |
| Call-stack-level allocation hotspot tracking | PerfView | **No — impossible** | See "Do Not Seek Parity" below |
| Live ETW event timeline / GC pause correlation over time | PerfView | **No — impossible** | Same reason |

## Better Investigation Workflows

DumpDetective's strongest differentiator is breadth-in-one-artifact: none of the four comparison
tools alone covers memory + GC + threads + locks + exceptions + leak candidates in a single pass.
Matching DumpDetective's stated 36-analyzer scope today would require running WinDbg extensions,
PerfView, *and* dotMemory together and manually reconciling their output. This is worth protecting
explicitly — the consolidation work recommended in Deliverables 4/5/8 should be framed as
*strengthening* this differentiator (making the single pass fast and correct), not diluting it by
distributing analysis across a WinDbg-style pile of independent, uncorrelated commands.

Two further advantages, both **conditional on verification**:

- **CLI + structured JSON output** enables CI/CD-embedded regression gating (e.g., "fail the build
  if leak-candidate confidence exceeds a threshold on a nightly load-test dump"). None of the four
  comparison tools are CI-first — WinDbg is scriptable but not structured-output-first; VS and
  dotMemory are GUI-first. This is a workflow the industry tools generally don't optimize for.
- **Automated trend comparison across dumps** via `Analysis.Trend.Comparers` could be a scheduled,
  unattended step, versus VS/dotMemory's manual "load two snapshots and click compare" workflow —
  a real advantage *if* the comparer pipeline is actually wired to run unattended end-to-end;
  worth confirming rather than assuming.

## Better Evidence

**This is currently DumpDetective's weakest category relative to the benchmark, and the review
should be direct about that.** dotMemory's automatic inspections and key-retention-path grouping
produce one consistent, ranked, explained answer per problem. Deliverables 3, 5, and 7 already
established that DumpDetective instead produces leak signals from up to 6 independently-scored
analyzers (`RetentionAnalyzer`, `LeakCandidateAnalyzer`, `DominatorAnalyzer`,
`StaticRootLeakDetector`, `EventLeakAnalyzer`, `TimerLeakAnalyzer`) with no unified confidence
model. Benchmarked against dotMemory specifically, this is not a minor inconsistency — it's the
gap between DumpDetective and matching the core "tell me what's actually wrong" value
proposition that the comparison tool is built around. This finding, arrived at independently in
Deliverable 5, is reinforced rather than superseded here: closing it (Deliverable 5 items 6/8/9/11
— evidence builder, ranking engine, confidence scoring, inter-analyzer result bus) should be read
as competitively necessary, not merely a code-quality nicety.

Where DumpDetective already matches or exceeds the benchmark: WinDbg's `!gcroot` returns one root
path per object, on request. `ReferenceChainAnalyzer` (once elevated to the canonical evidence
provider per Deliverable 6) is equivalent or better — multiple sample paths, structured output
usable in an automated report rather than requiring interactive re-querying.

## Better UX

DumpDetective today is CLI + JSON + structured reports (per CLAUDE.md's Output section) — no
visual graph, no interactive drill-down. Relative to VS's path-to-root graph view and dotMemory's
interactive object browser, this is a real UX gap. Per "do not seek feature parity blindly": a
full interactive GUI is very likely out of scope for what DumpDetective actually is — an automated
analyzer platform, not an interactive profiler — and building one now would be a strategic
distraction from the Phase 0 architecture work.

The recommended UX investment instead: report *quality and consistency*, which is achievable
without becoming a GUI tool and is already implied by work recommended elsewhere in this review —

- Consistent severity/confidence scoring across findings (Deliverable 5's evidence
  builder/confidence scoring) directly improves the "what do I do next" moment more than a graph
  would, for a tool whose primary interaction is "read the report."
- Consistent report-section shape across similar analyzers (fixing the duplicate `SectionBuilder`s
  identified in Deliverable 4 §6) is simultaneously an architecture fix and a UX fix — a case
  where the internal consolidation work and the external polish goal are the same work.

A well-organized static report with a clear, single confidence-ranked evidence chain can out-UX a
cluttered interactive tool for DumpDetective's actual use case (production incident triage,
frequently by someone who wasn't the app's original author) — that should be the UX bar, not
visual parity with a profiler.

## Better Extensibility

DumpDetective's `IAnalyzer` interface plus catalog-based registration (Deliverable 1/7) is a
genuinely strong extensibility story relative to the benchmark:

- **WinDbg** requires writing a native SOS/DbgEng extension DLL to add new analysis — high
  friction, different language/toolchain than the app being diagnosed.
- **PerfView** requires understanding its internal ETW event-processing model — higher friction
  than a clean managed interface.
- **dotMemory** has no user-facing extensibility model at all (closed commercial tool) —
  DumpDetective wins outright here.

However, Deliverable 7's finding tempers this: the 4x registration fan-out (`AnalyzerType` +
`FindingGeneratorType` + `TrendComparerType` + `AnalyzerSectionBuilderType` per module) means
adding one new analyzer today requires four coordinated types, not "just implement `IAnalyzer`."
That's real friction the interface's simplicity doesn't advertise. Benchmarked against the goal of
being meaningfully easier to extend than WinDbg/PerfView, this friction should be reduced (e.g.
sensible defaults for the generator/comparer/section-builder types) before the analyzer count grows
much past today's 36 — the extensibility advantage is real now, but it doesn't scale for free.

## Do Not Seek Feature Parity On

Stated explicitly, per the doc's instruction:

- **Call-stack-level allocation hotspot tracking** (PerfView) — requires ETW allocation events
  captured over a time window; architecturally impossible to derive from a single static dump, no
  matter how the analyzer layer is redesigned. Already correctly excluded in Deliverable 2's
  "Allocation hotspots" entry — reaffirmed here against the actual comparison tool that does this
  well, to close the loop.
- **Live ETW timeline / GC pause correlation over time** (PerfView) — same root cause: DumpDetective
  analyzes a point-in-time snapshot by design, not a trace.
- **Full interactive GUI object browser / graph visualization** (VS, dotMemory) — architecturally
  possible, strategically wrong to prioritize now; would compete with, not complement, the report
  quality work that actually closes the Evidence gap.
- **Live-attach debugging** (WinDbg, VS) — DumpDetective is a postmortem dump analyzer by design;
  live attach is a different product, not a missing feature of this one.

## Summary

DumpDetective's architectural bet — automated, cross-cutting, single-pass analysis of a static
dump, expressed as an extensible `IAnalyzer` catalog — is the right one, and closest in spirit to
dotMemory's automated-inspection philosophy rather than WinDbg's manual-command philosophy or
PerfView's trace-analysis philosophy. The benchmark validates the strategy but sharpens where
execution currently falls short of it: evidence consistency (Deliverable 5/7) is the one gap that
actually threatens the core value proposition, and closing it should outrank chasing any
capability unique to the other three tools.

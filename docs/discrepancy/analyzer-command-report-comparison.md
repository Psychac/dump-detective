# Per-Analyzer Report-Presentation Comparison

Scope: given the same (or similar) underlying analysis, how does each tool *present* it to the
reader? This is deliberately separate from
[analyzer-command-analysis-comparison.md](analyzer-command-analysis-comparison.md), which covers
what's actually computed — a pair can be ahead on one axis and behind on the other. The Dominator
and Leak-Candidate pairs in that doc are the clearest examples: this tool computes a more accurate
number in both cases, but presents it less clearly than the other tool does.

**Methodology**: every type/method named below was confirmed via direct source read or
`tokensave_search`/`tokensave_body` against the actual code graph — not assumed from docs. Two
findings below (§1 and §2) are **architectural**, meaning they were checked once against the shared
reporting primitives both tools use for *every* analyzer/command, rather than analyzer-by-analyzer —
that's why they apply uniformly rather than needing 33 separate confirmations. §3 covers what *is*
analyzer-specific and still needs a pair-by-pair pass.

## 1. Structured narrative explanation is a first-class primitive on their side; it isn't on ours

Confirmed by reading `IRenderSink.cs` directly: **`Explain(string? what, string? why, string[]?
bullets, string? impact, string? action)` is a method on the interface itself**
(`DumpDetective.Core/Interfaces/IRenderSink.cs:138`; the signature has **five** named parameters —
`impact` is distinct from `action` and easy to miss on a first read, confirmed by a direct call site
in `DeadlockReport.cs` that uses both: `impact` describes the consequence of the finding, `action`
is the concrete remediation step), implemented identically across every sink — `TextSink`,
`MarkdownSink`, `HtmlSink`, `JsonSink`, `BinSink`, `CaptureSink`, `TeeRenderSink` — and modeled as its
own serializable element type, `ReportExplain : ReportElement`
(`DumpDetective.Core/Models/ReportDoc.cs:112`), with `TypeDiscriminatorPropertyName = "type"`
polymorphic JSON serialization. This means every one of their ~66 commands can call
`sink.Explain(what: ..., why: ..., bullets: [...], impact: ..., action: ...)` and get identical
structured output across every output format, including their JSON/Bin archival formats — it is
baked into the reporting architecture, not something `MemoryLeakReport` happens to do more than
others.

This repo's equivalent, `SectionBlock` (`DumpDetective.Reporting/Models/AnalyzerDetailSection.cs:221`,
also `[JsonPolymorphic]`), has these concrete subtypes: `HeadingBlock`, `TextBlock`, `ListItemBlock`,
`PathBlock`, `DividerBlock`, `BlankBlock`, `CollapsibleSectionBeginBlock`/`EndBlock`, `ChartBlock`.
**There is no `ExplainBlock`/equivalent with named `what`/`why`/`bullets`/`impact`/`action` fields.**
Every section builder that wants to explain *why* a metric matters has to compose that manually out of
`TextBlock`s and `ListItemBlock`s (e.g. `DominatorSectionBuilder`'s single `T(...)` caveat lines,
`LeakAnalysisSectionBuilder`'s per-candidate `LeakExplainer.Explain` free-text string) — there's no
shared, structured "teach the reader what this means and what to do" primitive that every one of
this tool's 33 section builders can reach for the way the other tool's ~66 commands all can.

**This is the single highest-leverage report-side finding in this comparison** — it explains why
the other tool's reports consistently read as guided investigations (see §2) across many different
commands at once, rather than being a quirk of any one report. Adding an `ExplainBlock`
(`SectionBlock` subtype with `What`/`Why`/`Bullets`/`Action` fields, rendered consistently across
`TextCanonicalReportFormatter`/`MarkdownCanonicalReportFormatter`/`HtmlReportRenderer`/
`JsonCanonicalReportFormatter`) would be a one-time, shared-infrastructure change that every
existing and future section builder could adopt incrementally — a much better return than fixing
narrative text analyzer-by-analyzer.

## 2. Root-cause chain rendering: inline and visual on their side, absent on ours — but narrower than first stated

Already covered in depth in [architecture-comparison.md](architecture-comparison.md) §8 and
[dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md) Audit Area 8 for the
Dominator pair specifically, and in
[analyzer-command-analysis-comparison.md](analyzer-command-analysis-comparison.md) for the
Leak-Candidate pair. The short version, generalized: `MemoryLeakReport.RenderRootChains` renders an
inline, box-drawn (`┌─`/`│`/`└►`), deduplicated-by-shape retention chain terminating in an
explicitly labeled `ROOT` step, directly beneath the suspect type it explains. This tool's
equivalent chain data (`RootPathFinding`, `RootPathGroup`) exists and is structurally richer/more
serializable (a typed, JSON-polymorphic slot vs. their string-line-based `ChainStep`/`SampleChain`
records) — but it's confined to `GCRootIntelligenceSectionBuilder`'s own section, cross-referenced
by type name from other sections rather than inlined where the reader is already looking (Dominator
suspects, Leak Candidates). Confirmed: no other section builder in this codebase references
`RootPathGroups`/`RootPathFinding` besides `GCRootIntelligenceSectionBuilder` itself.

**Update from the `GCRootAnalyzer` vs. `gc-roots` deep dive**
([pairs/gc-root-analyzer-vs-gc-root-map.md](pairs/gc-root-analyzer-vs-gc-root-map.md)): this needs a
narrower framing than "they have this capability generally and reuse it; we have it and don't."
`MemoryLeakReport`'s chain data is built by `MemoryLeakAnalyzer.BuildChainBFS` — a private method
specific to that one analyzer, confirmed via the code graph to not be shared with their own
`GcRootsAnalyzer`/`GcRootMapAnalyzer` (the closer structural analog to this tool's
`GCRootAnalyzer`). **Both tools have the same "the good chain-rendering logic exists in exactly one
place and isn't a shared primitive" architecture gap** — theirs happens to live in the leak command,
ours happens to live in the GC-root analyzer. The gap that's real and asymmetric is narrower than
originally stated: it's that *their one place* (`memory-leak`) does inline box-drawn rendering with
dedup-by-shape, while *our one place* (`GCRootIntelligenceSectionBuilder`) does a clear, honest
table but not an inline chain diagram, *and* neither tool's version is reused by its other
commands/analyzers. The `ExplainBlock` recommendation in §1 and the "make chain rendering shared and
reusable" recommendation from the Dominator/Leak-Candidate audits both still stand — just note that
"copy what they already do everywhere" isn't accurate; it's "build the shared version neither tool
has yet, informed by how good their one instance of it looks."

## 3. Findings model: centralized/rankable (ours) vs. inline/contextual (theirs) — a real tradeoff, not a strict win either way

This one cuts the other direction and is worth stating precisely rather than folding into "they're
ahead on presentation":

- This tool's findings (`InsightFinding`, `FindingRecord`) are lifted out of each analyzer's own
  section into a **centralized, cross-analyzer bus** — `FindingGenerationPipeline` →
  `ExecutiveSummarySectionBuilder`/`InsightsSectionBuilder` — which is what makes the
  `HealthScorecardBuilder`/`TrendHealthScorecardBuilder` domain-severity rollup and the
  `TrendComparer`-based regression tracking possible at all. A finding raised by `WeakReferenceAnalyzer`
  and a finding raised by `EventLeakAnalyzer` are directly comparable and jointly rankable because
  they're both `InsightFinding`s in one list, ranked by one severity scale.
- The other tool's `sink.Alert(level, title, detail, advice)` calls are threaded directly into the
  narrative flow of each individual command's `Render` method, interleaved with tables and
  `Explain` blocks (confirmed: `AlertLevel` enum and `Alert` method both live on `IRenderSink`
  itself, called inline throughout `MemoryLeakReport.RenderFindings`/`RenderHeapSnapshot`, etc.) —
  this reads better within a single command's output, but there's no equivalent centralized,
  cross-command "all alerts, ranked, in one place" view the way this tool's Executive Summary
  provides across all 33 analyzers in one run.

Net: this tool's architecture is better suited to "one report, 33 analyzers, tell me what matters
most across all of them" (which is this tool's actual primary mode — one command, everything runs).
The other tool's architecture is better suited to "I ran this one specific command, walk me through
what it found" (which is closer to their actual primary mode — pick a specific verb/command per
question). Neither is strictly better; they're optimized for different primary interaction shapes.
Adopting `ExplainBlock` (§1) does **not** require giving up the centralized findings model — the two
are orthogonal; an `InsightFinding` can still exist for cross-analyzer ranking while the section
that raised it *also* gets a structured `ExplainBlock` for local narrative.

## 4. Charts: parity, not a gap

Checked because their `MemoryLeakReport` uses `sink.DonutChart`/`sink.StackedBar` prominently and it
would have been an easy false claim to make. This tool has an equivalent `ChartBlock`
(`AnalyzerDetailSection.cs:252`, built via `SectionBuilderBase.Chart(...)`) with its own JS renderer
(`report.renderers.charts.js`, `buildChartBlock`/`buildRankedBarChart`) already used across the HTML
formatter. **No gap here** — noting it explicitly so this doesn't get miscounted as a report-side
deficiency in a future pass.

## 6. Executive-summary/health-score participation: generic on our side, structurally excluded for most commands on theirs

Found while deep-diving the Lock-Graph/Deadlock and GC-Root pairs (full detail in each pair's file
under [pairs/](pairs/README.md)), but the mechanism is cross-cutting enough to state once here.

**This tool**: any `IFindingGenerator`'s `InsightFinding`s — regardless of which of the 33 analyzers
raised them — flow through one shared `FindingGenerationPipeline` into
`ExecutiveSummarySectionBuilder`/`InsightsSectionBuilder`/`HealthScorecardBuilder`. This is generic
by construction: a finding from `LockGraphFindingGenerator` and a finding from `DominatorFindingGenerator`
are jointly rankable in the same executive summary because the architecture doesn't special-case
which analyzer produced them.

**The other tool**: confirmed by tracing the actual call order in `AnalyzeCommand.Run` — `HealthScorer.Score(snapshot,
thresholds)` runs *before* `AnalyzeReport.RenderEmbeddedReports(...)`, and `HealthScorer.Score` takes
only a `DumpSnapshot` (the single-heap-walk snapshot object) as input, not the 34 embedded commands'
own results. `BuildFindingsBullets`/`BuildCrossMetricNarrative` (the closest things to an executive
summary on their side) likewise only read fields directly off that same `DumpSnapshot`. Structurally,
this means **most of their 34 embedded `analyze --full` sub-reports — confirmed for
`deadlock-detection`, and by the same call-order argument almost certainly true for `gc-roots`
(which doesn't even run in `--full` — `IncludeInFullAnalyze => false`) and any other command whose
data isn't already a field on `DumpSnapshot` — cannot appear in their top-level health
score/findings-bullets narrative at all**, no matter how severe that command's own findings are
within its own section. `memory-leak` is the interesting partial exception: it does run in `--full`
(`IncludeInFullAnalyze => true`) and its accumulation-pattern signals (Gen2%, string/byte-array/
collection counts) overlap with fields `DumpSnapshot`/`HealthScorer` likely already read directly —
but its *leak-specific* signals (per-type suspicion scores, root chains) still don't reach the
top-level rollup, only the generic heap-level thresholds do.

**Net**: this is a genuine, confirmed asymmetry in this tool's favor, architecturally — not because
this tool's engineering is better per se, but because the other tool's "one command, one report"
design point never needed a cross-command rollup the way a "one command, 33 analyzers" design does.
Worth stating precisely rather than as a vague "we have executive summary and they don't," since
they do have *something* in that space (`BuildFindingsBullets`) — it's just structurally scoped to
less than their own command surface.

## 7. Analyzer-by-analyzer report comparisons

§1, §2, and §6 above are architectural findings that apply across many analyzers/section builders at
once (confirmed by checking the shared primitives, not by reading all 66 report files). Four pairs
have now been deep-dived to the full template in [pairs/](pairs/README.md) — each confirms §1/§2/§6
pair-specifically rather than just inheriting the general claim, and each also covers presentation
dimensions not yet generalized here (severity/confidence model, actionability, machine-consumability,
HTML interactivity — see that folder's template). Remaining candidates, in priority order, are
tracked in [pairs/README.md](pairs/README.md) § Pending, not duplicated here.

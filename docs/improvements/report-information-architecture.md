# Report Information Architecture — Analysis & Proposal

**Scope:** the single-dump HTML/JSON report. Trend mode inherits the same spine, so the proposal applies there too, but the worked examples are single-dump.

**Companion docs:**
- [report-cross-analyzer-data-plane.md](report-cross-analyzer-data-plane.md) — the *data* plane: many analyzers measuring the same entities and each publishing its own top-N table. **Read that one first** — it is the more fundamental problem, and several issues raised below dissolve once it is fixed.
- [report-display-vision.md](report-display-vision.md) — *visual* presentation (charts, cards, CSS).

This doc covers the *findings* plane: what gets grouped with what once diagnoses exist. Renderer-agnostic throughout.

**Source of truth:** this was written by reading the code, not the specs under `docs/ReportStructure/`. Where the spec docs and the code disagree, the code is described.

---

## 1. What the report actually is today

### 1.1 The pipeline

```
AnalysisPipeline
  └─> AnalyzerRunResult[]          (33 analyzer modules; each carries a typed
                                    XxxDomainResult + InsightFinding[])
      └─> InsightEngine            (31 cross-analyzer Detect* rules over the
                                    typed domain results → more InsightFindings)
          └─> ReportSerializer.Serialize
              ├─ BuildAnalyzerSections   (1 section per analyzer, via 33 IAnalyzerSectionBuilder)
              ├─ BuildSpecSections       (4 global IReportSectionBuilder)
              ├─ ApplySectionMetadata    (SectionIdDomainMap: analyzer → domain + section id)
              ├─ NormalizeSectionContractSlots (promote LeadFinding / Provenance)
              ├─ ApplyDomainOrdering
              ├─ MapAllFindings          (InsightFinding → FindingRecord)
              ├─ BuildDomainSections     (group sections + findings into 9 domains)
              ├─ BuildCrossDomainInsights
              ├─ BuildCorrelationEvents
              ├─ HealthScorecardBuilder.Build
              ├─ ExecutiveSummaryProjector.Build
              │    ├─ ExplainableScoringEngine.ComputeScores  (3 headline scores)
              │    └─ ActionPriorityService.BuildTopActions   (ranked action queue)
              └─ BuildAppendix
                  └─> IReportFormatter (HTML / JSON / Markdown / Text)
```

Key files: [ReportSerializer.cs](../../src/DumpDetective.Reporting/Services/ReportSerializer.cs), [ReportSectionAssembler.cs](../../src/DumpDetective.Reporting/Services/ReportSectionAssembler.cs), [ReportDomainProjector.cs](../../src/DumpDetective.Reporting/Services/ReportDomainProjector.cs), [ReportCorrelationBuilder.cs](../../src/DumpDetective.Reporting/Services/ReportCorrelationBuilder.cs), [InsightEngine.cs](../../src/DumpDetective.Analysis/Insight/InsightEngine.cs).

### 1.2 The render order the reader sees

From [report.main.js:82-194](../../src/DumpDetective.Reporting/Templates/report.main.js#L82-L194):

| # | Block | Source |
|---|---|---|
| 1 | Header — dump identity + KPI tiles | `buildHeader`, `ExecutiveSummaryRecord` key-metric fields |
| 2 | Health scorecard — 9 domain tiles | `HealthScorecardBuilder` |
| 3 | Executive summary — 3 scores, triage cards | `ExecutiveSummaryProjector` + `ExecutiveSummarySectionBuilder` |
| 4 | Action queue (right rail in v2) | `ActionPriorityService.BuildTopActions` |
| 5 | Forensics rail | `buildForensicsRailPanel` |
| 6 | Global search + filter bar | client-side |
| 7 | **Domains** → 9 domains, each holding its analyzer sections | `BuildDomainSections` |
| 8 | Cross-domain insights | `BuildCrossDomainInsights` (InsightEngine output) |
| 9 | Incident context | `AnalysisIncidentContext` |
| 10 | Appendix — run summary, memory diagnostics, known limitations | `BuildAppendix` |

The nine domains (`SectionIdDomainMap.DomainsInOrder`): Leaks, Memory, GC, TypeSystem, Threads, Async, Exceptions, Runtime, Infrastructure.

So the user's mental model — *metadata → health → exec summary → sections → appendix* — is accurate. The structure is sound as a **container**. The problem is what goes in the containers.

---

## 2. Diagnosis: why it reads as data-heavy

The instinct in the prompt is correct, and it is not a presentation problem. It is a **data-model** problem with a specific, identifiable root cause.

### 2.1 Root cause: findings have no structured subject

[`InsightFinding`](../../src/DumpDetective.Core/Models/InsightFinding.cs) is the atom of the entire report:

```csharp
public sealed record InsightFinding(
    string Analyzer, string Category, FindingSeverity Severity,
    string Title, string Evidence, string Recommendation,
    IReadOnlyList<string> Tags,
    string? Fingerprint = null, double? MetricValue = null, string? MetricUnit = null,
    double? ConfidenceScore = null, IReadOnlyList<string>? Caveats = null)
```

There is **no field for what the finding is about**. No type name, no method table, no object address, no OS thread id, no module, no handle kind, no segment. `Evidence` is a prose blob; `Tags` is an unconstrained string list.

Every consolidation problem in the report descends from this. Two analyzers can both be talking about `System.Byte[]` and nothing in the model knows it.

### 2.2 Consequence — correlation is substring matching

[`ReportCorrelationBuilder.ExtractCorrelationSignalKeys`](../../src/DumpDetective.Reporting/Services/ReportCorrelationBuilder.cs#L304-L353) builds join keys from:

- tags longer than 3 chars, minus a denylist of ten generic ones (`memory`, `gc`, `leak`, `heap`, …)
- `metric:` prefixed tags
- **ten hard-coded substring probes** against `Title + Evidence + Recommendation`: `deadlock`, `thread pool`, `finalizer`, `gc handle`, `pinned`, `retention`, `fragmentation`, `connection pool`, `timeout`, `latency`

A cluster is emitted when ≥2 findings share a key and span ≥2 domains; output is capped at 8 events. The rendered result is prose of the form:

> **Shared signal across managed heap and garbage collection: fragmentation**
> Why linked: managed heap and garbage collection findings share fragmentation across 3 findings.

That sentence tells the reader that three findings contain the word "fragmentation." It does not tell them that a specific 400 MB `byte[]` population, pinned by 1,200 async socket handles, is what is fragmenting the LOH. The mechanism — the only thing a dump analyst actually wants — is absent by construction, because the input had no mechanism in it.

"Conflict" detection has the same shape: `severityConflict = (maxSeverity - minSeverity) >= 2`. That is a disagreement about *labels*, not about *measurements*. The genuinely interesting conflict — LeakCandidateAnalyzer claims type X retains 2.1 GB while DominatorAnalyzer computes 340 MB for the same type — is invisible, because neither number is attached to a comparable subject.

### 2.3 Consequence — the evidence-ref plumbing is inert

The JSON schema has `EvidenceRef(Analyzer, MetricKey, Addresses, ArtifactPath, SnapshotIndex)`. In practice [`ReportFindingMapper`](../../src/DumpDetective.Reporting/Services/ReportFindingMapper.cs#L46-L64) hardcodes `Addresses: null` at both construction sites, and `MetricKey` is only non-null if the analyzer happened to emit a `metric:`-prefixed tag. So a finding can never point at the objects it is about. The reader cannot go from "1.4 GB retained by static field" to the actual root, inside the report.

### 2.4 The report is organised by producer, not by subject

Domains are not a second axis — `SectionIdDomainMap` is a 1:1 analyzer→domain mapping, so "domains" is the same analyzer axis at lower resolution. There is exactly one organising principle in the whole report: *which code produced this*.

Concretely: a reader asking "why is this process at 9 GB?" must visit and mentally join

- A1 Leak Candidates — top types by suspicion score
- A2 Memory — top types by shallow size
- A3 Dominator — top types by retained size
- A7 String — top duplicate strings, by owning type
- B1 GC Generation — Gen2 by type
- B3 Heap Topology — type distribution across segments
- B4 LOH Fragmentation — large object types
- C3 Collections — oversized/wasteful collections by type
- C4 Arrays — array waste by element type

Nine tables, each a top-N of *the same type population*, sorted nine different ways, with no cross-links. That is the "each analyzer seems to be showing its own thing" feeling, precisely. The information is not redundant — each column is genuinely different — but the *row identity* is shared and never exploited.

### 2.5 Three unrelated consolidation systems, three unrelated confidences

| System | Clusters by | Emits | Confidence source |
|---|---|---|---|
| `InsightEngine` (31 rules) | typed domain-result fields | `InsightFinding` tagged `cross-analyzer` | per-rule literal or severity default |
| `ReportCorrelationBuilder` | shared tag / metric / keyword strings | `CorrelationEventRecord` | literal `0.9` if ≥3 domains else `0.7` |
| `ActionPriorityService` | `category\|title\|action-text` normalised string | `RankedActionRecord` | 5-term weighted composite |

None of the three knows about the other two. The same underlying reality can therefore appear as an InsightEngine finding, a correlation event, and a ranked action — each with a *different* confidence number, all rendered on the same page. This is a direct driver of the "data-heavy" feeling: the reader has to work out that three things are one thing.

### 2.6 Confidence is largely circular

`InsightFinding.ConfidenceScore` defaults to a function of severity: Critical → 0.9, Warning → 0.7, else 0.5. Most analyzers do not override it. So "confidence 0.90" overwhelmingly means "an analyzer author wrote `FindingSeverity.Critical`."

That value is then consumed by `ActionPriorityService.ComputeConfidence` as `baseConfidence` (weight 0.45) and folded into `ConfidenceWeight` in the priority score, alongside `SeverityWeight` (50/30/10). Severity is thus counted twice, once directly and once laundered through confidence. `ExplainableScoringEngine` scores are likewise computed from severity-labelled findings, so the three headline numbers (leak / GC pressure / thread contention) are also downstream of the same labels.

The scores look quantitative and are not. Worse, they are non-orthogonal: a single leak finding lifts both LeakLikelihood and GcPressure.

### 2.7 Four renderings of the same finding

A Critical finding from, say, `EventLeakAnalyzer` currently appears:

1. as a triage card in the Executive Summary (`ExecutiveSummarySectionBuilder` re-lists top-5 Critical + top-5 Warning + top-3 recommendations verbatim)
2. in the Action Queue right rail (`ActionPriorityService`)
3. as the `LeadFinding` of the D4 section inside the Threads domain
4. possibly again as a Cross-Domain Insight, if InsightEngine's `DetectEventLeakPattern` also fired

Four copies of the same sentence, three of them with different confidence decoration. None of them says "and here is the delegate target, and the object holding it."

### 2.8 The best content is rendered last

`InsightsSectionBuilder.SortOrder = 1900`, and `report.main.js` appends cross-domain insights *after* all nine domains. InsightEngine is the one component in the system that reasons over typed results from multiple analyzers — the only place where real mechanism-level statements are produced (`DetectBoxingGCCorrelation`, `DetectAllocationPressureCrossCorrelation`, `DetectGCRootLargeRetention`, `DetectDataTableLifecyclePattern`, …). It is buried below several thousand rows of per-analyzer tables.

### 2.9 Smaller structural issues

- **Health scorecard is max-severity-per-domain.** One Critical among six GC analyzers paints the whole GC domain red, with no weighting by whether it is relevant to *this* dump's problem. Nine tiles that are mostly red carry no signal.
- **Tool health is mixed into subject findings.** `MapAllFindings` emits `analyzer-failure:` and `finding-generator-error:` records as Warning-severity findings in the same list as real diagnoses. They also become stub sections. These are report-integrity facts, not process facts.
- **Known limitations is a static 13-entry string list** hardcoded in `BuildAppendix`, regardless of which analyzers ran or which findings actually depend on the approximation. The caveat "retained size is bounded BFS, not a true dominator tree" belongs *next to the retained-size number*, not in an appendix the reader will not reach.
- **Sampling/capping is invisible at the point of use.** Many analyzers cap or sample (`StringAnalyzer` sampled unique patterns, `EventLeakFastScanner`, `RootPathFinder` depth 20). `SectionProvenance` has a `CappingNotes` slot but `NormalizeSectionContractSlots` never populates it for cross-cutting sections (the `cappingNotes` list at line 252 is built empty and never appended to). A number derived from a 50k sample of 3.2M strings renders identically to a fully measured number.
- **No causal or temporal ordering.** Correlation events are typed `co-move` or `conflict`. There is no way to express "the finalizer backlog is a *consequence* of the blocked finalizer thread," which is the single most common structural fact in a real dump.

---

## 3. The reframe

> A dump report should be organised around **the things in the process**, with analyzers as evidence contributors — not organised around the analyzers, with things as row labels.

The analyzer-per-section layout is right for *verification* ("show me everything the LOH analyzer measured") and wrong for *diagnosis* ("what is wrong and why"). The fix is not to delete the analyzer sections — the prompt is right that no data should be discarded — but to stop making them the primary axis.

### 3.1 The Evidence Spine

Introduce a small closed set of canonical entity keys that every analyzer stamps onto every finding, and ideally onto notable table rows:

| Kind | Key form | Example |
|---|---|---|
| `Type` | normalized type name (+ MT when stable) | `System.Byte[]`, `MyApp.SessionCache` |
| `Thread` | OS thread id | `thread:0x1a4c` |
| `Module` | simple name + MVID | `MyApp.Data,{guid}` |
| `Root` | root kind + owner | `static:MyApp.Cache.s_entries` |
| `Handle` | handle kind | `handle:Pinned` |
| `Region` | segment/generation | `region:LOH`, `region:Gen2` |
| `Object` | address | `obj:0x00007ff8_1234` |

```csharp
public enum SubjectKind { Type, Thread, Module, Root, Handle, Region, Object }

public readonly record struct EvidenceSubject(SubjectKind Kind, string Key, string? Display = null);

public sealed record InsightFinding(
    ...,
    IReadOnlyList<EvidenceSubject>? Subjects = null);
```

The analyzers already hold every one of these values at the moment they emit the finding — `StringAnalyzer` knows the owning type, `GCHandleAnalyzer` knows the handle kind and target type, `ThreadAnalyzer` knows the OS id, `StaticRootLeakDetector` knows the field. Today they render them into a prose string and throw the structure away. Stamping is mostly mechanical.

`Subjects` is nullable with a default, so this is an additive, non-breaking change to a public record. Findings without subjects behave exactly as today.

### 3.2 What the spine unlocks

Once findings carry subjects, everything downstream can join on identity instead of text:

- **`SubjectIndex`** in Reporting: `subject → (findings, sections, key metrics, table rows)`. Built once, in one pass, from the already-materialised finding list. No heap access, no extra dump work.
- **Correlation on identity.** `ReportCorrelationBuilder`'s keyword bridges become a fallback, not the mechanism. Two findings about `MyApp.SessionCache` correlate because they are about `MyApp.SessionCache`.
- **Value conflicts, not label conflicts.** With a shared subject and comparable `MetricValue`/`MetricUnit`, "LeakCandidate: 2.1 GB retained vs Dominator: 340 MB retained for the same type" becomes detectable and is worth a Critical-adjacent callout — it tells the analyst the retention estimate is unreliable *for this specific type*.
- **Real click-through.** `EvidenceRef.Addresses` can finally be populated; the report can link a headline number to the objects behind it.

---

## 4. Proposed layering

Five layers, in reading order. Layers 3 and 4 are essentially today's report, demoted.

### Layer 0 — Verdict

One paragraph and one confidence, above everything. Not three scores.

> **Verdict:** managed heap is 8.7 GB, 71% of it Gen2, dominated by `MyApp.SessionCache` retained from a static field on `MyApp.Startup`. The cache has no eviction path. Confidence: high — three independent measurements agree within 8%.

Emitted only when an InsightEngine rule (or a new `VerdictBuilder` over the case files) can name a **mechanism**. When it cannot, say so explicitly — "no single dominant cause; three independent pressure sources, see case files" — rather than falling back on the three synthetic scores. That honesty is more useful than a fabricated 73/100.

The three headline scores stay, but move to the KPI strip as what they are: coarse gauges, not a diagnosis.

### Layer 1 — Case files (the substantive change)

Three to seven **case files**, each a correlated cluster keyed on a shared subject, assembled from every analyzer that touched it. Fixed anatomy:

```
CASE 1 — MyApp.SessionCache retains 4.2 GB                     [Critical] [conf: high]

  SUBJECT      Type MyApp.SessionCache · 1,204 instances · Gen2

  MECHANISM    static MyApp.Startup.s_container
                 └─ ServiceProvider._realizedServices (Dictionary)
                      └─ MyApp.SessionCache._entries (ConcurrentDictionary, 1.2M entries)
               ← from ReferenceChainAnalyzer / StaticRootLeakDetector

  MAGNITUDE    Retained 4.2 GB (48% of managed heap)     [Dominator, measured]
               Shallow    212 MB                          [Memory, measured]
               Gen2       99.4% of instances              [GCGeneration, measured]

  CORROBORATION
    Analyzer            Signal                          Value       Agrees?
    LeakCandidate       leak suspicion rank #1          score 0.94  ✓
    Dominator           retained size                   4.2 GB      ✓
    StaticRoot          rooted by static field          yes         ✓
    String              duplicate strings owned         1.1 GB      ✓ (subset)
    Collection          oversized ConcurrentDictionary  1.2M items  ✓
    GCGeneration        Gen2 concentration              99.4%       ✓

  CONTRADICTING        none

  COVERAGE             retained size from bounded BFS (depth 20) — lower bound
                       string ownership from 50k sample of 3.2M strings

  ACTION               Add eviction / TTL to SessionCache._entries.
                       Validation: retained size for this type should drop below 500 MB.
```

Every element of this already exists somewhere in the current report — scattered across A1, A3, A6, A7, C3, B1 and the appendix. The case file does not add data; it puts one subject's data in one place, and makes agreement and disagreement explicit.

`ContradictingEvidence` is deliberately a required, always-rendered slot. "None" is an informative answer. A case file where three analyzers agree and one disagrees is exactly the case the analyst must look at first, and today that state is unrepresentable.

**Construction:** a `CaseFileBuilder` in Reporting that clusters findings by subject-key overlap (connected components over the subject graph, capped), scores each cluster by `distinct-analyzer-count × max-severity × magnitude-share`, and takes the top N. It supersedes `ReportCorrelationBuilder` for the primary path; keyword bridging survives as a weak-signal fallback for findings that carry no subjects.

### Layer 2 — Subject dossiers

For subjects that appear in ≥3 analyzers but do not warrant a case file. One entry per subject, all analyzer columns merged into a single row set.

`System.Byte[]` today appears in the A2 top-types table, the B4 LOH table, the B7 pinned-handle table, the C4 array-waste table, and the C5 boxing table — five tables in four domains. As a dossier it is one block with five columns.

This is where the "don't discard data" constraint is honoured: the dossier is *denser* than the current layout, not sparser.

### Layer 3 — Domain sections (today's sections, demoted)

Unchanged content, unchanged builders, unchanged JSON. Collapsed by default, framed as "all measurements, by subsystem" — the verification layer. Each section gains a header strip of chips linking to the case files and dossiers its findings participate in, so the two axes cross-reference.

Findings that are the canonical content of a case file render here as a one-line reference chip, not a full repeat. That single rule removes most of the current duplication without losing anything.

### Layer 4 — Report integrity appendix

Split cleanly from subject findings:

- analyzer run summary (as today)
- **coverage report** — for each analyzer: scanned/total, sampled or complete, caps hit. Currently only partly available via `SectionProvenance` and never aggregated.
- **limitations that actually applied** — derived from which analyzers ran and which findings carry approximation caveats, replacing the static 13-string list. A limitation about `ClrThread.StackBase` should not appear if ThreadAnalyzer was skipped.
- analyzer failures and finding-generator errors, moved out of the finding stream

---

## 5. Cross-cutting fixes worth doing regardless

These stand on their own even if the case-file work is deferred.

**5.1 Retire severity-derived confidence.** Replace `ConfidenceScore`'s severity default with an evidence-based computation:

| Term | Basis |
|---|---|
| independence | count of distinct analyzers whose measurements support the claim |
| directness | measured vs estimated vs heuristic (analyzer declares this) |
| coverage | fraction of the relevant population actually scanned |
| consistency | spread between independent measurements of the same quantity |

One confidence, computed in one place, consumed by case files, actions, and correlation alike. Removes the double-counting in `ActionPriorityService` and makes the number mean something.

**5.2 Make sampling visible at point of use.** Any metric derived from a sample or a cap renders with a marker and the sample size, inline. `SectionProvenance.CappingNotes` already exists as a slot; populate it (today `NormalizeSectionContractSlots` builds an empty `cappingNotes` list at line 252 and never adds to it) and surface it next to the number rather than in a collapsed footer.

**5.3 Add a causal relation to correlation.** `CorrelationEventRecord.EventType` is `co-move | conflict`. Add `causes` / `caused-by` and let InsightEngine rules assert it where the mechanism is known — blocked finalizer thread → finalizer queue backlog → Gen2 growth is a chain the engine can already detect but cannot express. A three-node causal chain communicates more than three separate Critical findings.

**5.4 Weight the health scorecard by magnitude, not just max severity.** A domain tile should reflect "how much of this dump's problem lives here," which the subject index makes computable (share of retained bytes / blocked threads attributable to that domain's subjects).

**5.5 Promote InsightEngine output above the domain sections.** Independent of everything else: move cross-domain insights ahead of Layer 3 in `report.main.js`, and lower `InsightsSectionBuilder.SortOrder` below the per-analyzer range. The best content in the report is currently last.

---

## 6. Phased plan

Ordered so that value arrives before the schema changes.

### Phase A — Subject index from existing data *(no model changes)*

- `SubjectExtractor` in Reporting: recover type names, thread ids, and module names from existing `Title` / `Evidence` / `Tags` by pattern (type-name shapes are highly regular). Deliberately lossy — it exists to prove the join is valuable and to seed the UI.
- Build `SubjectIndex`; render a "related evidence" chip row on each section, linking sections that share subjects.
- **Exit criterion:** on a real dump, do the chips connect the sections a human analyst would connect? If yes, Phase B is justified.

### Phase B — First-class subjects *(additive model change)*

- Add `EvidenceSubject` and `InsightFinding.Subjects` (nullable, default null — non-breaking).
- Stamp subjects in the ~10 highest-value analyzers first: LeakCandidate, Dominator, StaticRoot, ReferenceChain, GCRoot, Memory, GCGeneration, String, Collection, EventLeak.
- Retire the extractor's guesses for stamped analyzers; keep it as fallback for the rest.
- Populate `EvidenceRef.Addresses` where the analyzer has addresses.

### Phase C — Case files

- `CaseFileBuilder`; `CaseFile` record; new top-level document slot alongside `CorrelationEvents`.
- Value-conflict detection across same-subject metrics.
- Renderer for the case-file anatomy; case files render between exec summary and domains.
- Domain sections switch to reference chips for findings owned by a case file.

### Phase D — Dossiers and demotion

- Subject dossiers for ≥3-analyzer subjects.
- Domain sections collapsed by default with cross-reference chips.
- Verdict block replaces the re-listed triage cards in the exec summary.

### Phase E — Confidence, coverage, causality

- Unified evidence-based confidence; `ActionPriorityService` consumes it instead of recomputing.
- Coverage report; derived limitations replacing the static list.
- `causes` / `caused-by` correlation type; causal chains in case files.

Phases A–B are prerequisites; C–E are independently shippable.

---

## 7. What not to change

- **InsightEngine's architecture is right.** Stateless rules over typed domain results, no heap access, is exactly the correct shape for cross-analyzer reasoning. It is underexposed and under-fed (no subjects to reason over), not misdesigned. Case files should be built *on* it, not beside it.
- **The analyzer/section-builder/finding-generator triad and `AnalyzerFeatureModule` registration** are clean and extensible. Nothing here requires touching that contract.
- **The domain taxonomy** (`SectionIdDomainMap`, 9 domains) stays as the verification-layer organisation. It is a good secondary axis; it is only a bad primary one.
- **Streaming and memory discipline.** Everything proposed operates on the already-materialised finding list and typed domain results — bounded, small, post-heap-scan. No new heap passes, no new per-object allocation on hot paths.
- **JSON schema compatibility.** `Subjects`, `CaseFiles`, and coverage records are additive. `SchemaVersion` bumps to `2.2`; existing consumers keep working.

---

## 8. Summary

| | Today | Proposed |
|---|---|---|
| Primary axis | producing analyzer | subject under investigation |
| Join key between analyzers | substring match on 10 keywords | canonical entity key |
| Cross-analyzer output | "shared signal: fragmentation" | case file with mechanism, magnitude, corroboration, contradictions |
| Confidence | 3 unrelated numbers, mostly severity restated | 1 number from independence, directness, coverage, consistency |
| Disagreement between analyzers | invisible (only severity-label gaps) | first-class, always rendered |
| Duplication | headline finding appears up to 4× | one canonical home, references elsewhere |
| Detail data | 9 domains × N tables, all top-level | unchanged content, demoted to verification layer |
| Caveats | static 13-line appendix list | attached to the number they qualify |

The prompt's instinct is right on both counts: the report is data-heavy, and consolidating correlated signal from multiple sources is the fix. The reason it has not happened is not that nobody wrote the correlation code — `ReportCorrelationBuilder`, `InsightEngine`, and `ActionPriorityService` are all attempts at it. It is that all three are trying to correlate records that carry no identity, so they are reduced to matching prose. Give findings a subject, and the consolidation the report needs becomes a straightforward join.

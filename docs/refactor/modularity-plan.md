# Modular Multi-Source Diagnostics Platform — Architecture & Migration Plan

Status: draft, not started. Supersedes the dump-only modularity draft and reworks
[../improvements/unified-dump-trace-architecture.md](../improvements/unified-dump-trace-architecture.md)
into the modularity plan rather than treating them as separate efforts.

**Scope warning up front.** This describes a platform, not a refactor. Executed fully it is a
multi-quarter program. § 8 gives a much smaller path to the same user-visible outcome; read it
before committing to anything here.

---

## 1. The premise

The long-term intent is one application that analyzes **dumps, traces, and both together**,
producing individual and combined reports. That requirement is not a feature to bolt on — it
invalidates the assumption the current architecture is built on.

Today, "a dump" is the subject of analysis. The unified-architecture doc proposes adding trace as a
second parallel pipeline fused at a normalized signal layer, routed by a mode enum
(`SingleDump | MultiDump | TraceOnly | Combined`). That's a reasonable minimal-disruption plan, and
it names its own weakness: mode explosion. Four modes now; add a gcdump source and routing becomes
combinatorial, each combination needing an orchestrator.

**The reframe that avoids this: a dump is not special.** It is one *evidence artifact* about a
process under investigation. So is a trace, a gcdump, an ETW log, a GC log. The application
analyzes a **session** — an ordered set of artifacts about one process, possibly across time.

| Today's "mode" | Under the session model |
|---|---|
| Single dump | Session with 1 dump artifact |
| Multi-dump trend | Session with N dumps at N time anchors |
| Trace-only | Session with 1 trace artifact |
| Dump + trace | Session with 2 artifacts |
| Dump + trace + gcdump | Session with 3 artifacts — **no new code path** |

There is no mode enum, no `CombinedOrchestrationService`, and no dump-vs-trace analyzer
duplication. There is one orchestrator that asks: *what capabilities do these artifacts provide,
which analyzers are satisfiable, and what can be correlated?*

Modularity and multi-source turn out to be the same problem. A system properly decomposed around
capabilities rather than around dumps is automatically able to accept new sources.

---

## 2. Three ideas the whole design rests on

**Capabilities, not source types.** An analyzer declares `[RequiresCapability("heap.generations")]`
and `[OptionalCapability("trace.gc-events")]` — never "I am a dump analyzer." One
`GcPressureAnalyzer` then serves dump-only (heap composition), trace-only (measured pauses), and
combined (both, strongest) sessions at *graded fidelity*, reporting which data it actually got.
Adding a source lights up existing analyzers for free. Detail:
[modularity/source-model.md § 3](modularity/source-model.md).

**Entity identity is the real hard problem.** Cross-source correlation is a join, and joins need
keys. A dump knows types by `MethodTable`; a trace knows them by name. They join on a canonicalized
`EntityRef.JoinKey`, with an explicit `MatchFidelity` per entity kind — exact for simple types,
low for lambdas whose compiler-generated ordinals shift between builds. Fidelity **caps** the
confidence of anything derived from that join. Getting this wrong produces confident false
correlations, which is worse than no correlation at all. Detail:
[modularity/source-model.md § 4](modularity/source-model.md).

**Observations, not domain results.** Analyzers emit typed, entity-anchored, time-extented,
evidence-bearing facts. Findings are *synthesized* from observations rather than hand-authored per
analyzer. This is what makes fusion possible — and it has a large incidental payoff: **trend
analysis falls out for free**, since trend is just synthesis over observations sharing
`(type, subjects)` and differing in time. Roughly 30 bespoke `IAnalyzerTrendComparer`
implementations collapse to one generic differ. Detail:
[modularity/observation-and-correlation-model.md](modularity/observation-and-correlation-model.md).

---

## 3. Target layering

```
Sinks            file / streaming / export        consume session report schema
   ▲
Synthesis        observations → findings → cross-source correlation → scoring
   ▲
Plugins          domain packages, capability-declared, source-agnostic
   ▲
Orchestration    session → capability resolution → DAG → execution
   ▲
Sources          ClrDump │ NetTrace │ (gcdump…)   each implements IArtifactSource
   ▲
Platform         ingest SPI, columnar/interned disk index, observation store, timeline
   ▲
SDK + Schema     IAnalyzer, EntityRef, Observation, Capability, TimeAnchor + wire schemas
```

Every boundary is drawn where independent versioning or independent failure actually matters. A
boundary that only exists for organizational tidiness isn't on this diagram.

---

## 4. Phases

| Phase | Goal | Depends on |
|---|---|---|
| [0 — Foundation & de-dump-ification audit](modularity/phase-0-foundation.md) | Contract inventory, capability map, dump-assumption catalog, conformance harness | — |
| [1 — Source-neutral contracts & SDK](modularity/phase-1-contracts-sdk.md) | `EntityRef`, `Observation`, `Capability`, `TimeAnchor`, session schema v3 | 0 |
| [2 — Artifact ingest & index platform](modularity/phase-2-artifact-platform.md) | `IArtifactSource` SPI; extract source-agnostic columnar/intern storage; dump becomes source #1 | 1 |
| [3 — Capability-driven plugins](modularity/phase-3-plugin-packaging.md) | Domain packages, attribute discovery, capability declarations; kill the hardcoded catalog | 2 |
| [4 — Session orchestration DAG](modularity/phase-4-session-orchestration.md) | One capability-driven orchestrator; **modes never get built** | 3 |
| [5 — Observations & synthesis](modularity/phase-5-observations-synthesis.md) | Analyzers emit observations; synthesis rules replace finding generators; trend comparers collapse | 4 |
| [6 — Trace source](modularity/phase-6-trace-source.md) | `.nettrace` streaming ingest, trace index, first trace analyzers | 2, 5 |
| [7 — Cross-source correlation](modularity/phase-7-cross-source-correlation.md) | Entity+time joined observations, correlation rules, capped confidence | 6 |
| [8 — Sinks & unified UI](modularity/phase-8-sinks-and-ui.md) | `IReportSink`, unconditional `report.json`, timeline/entity-pivot UI | 1 (parts land early) |
| [9 — Isolation & distribution](modularity/phase-9-isolation-distribution.md) | Out-of-process plugins, distributed storage — **speculative** | 2, 3 |

Supporting design docs (read before the phases):
[source-model.md](modularity/source-model.md) ·
[observation-and-correlation-model.md](modularity/observation-and-correlation-model.md)

### Can dump analysis migrate fully before trace starts?

Yes — Phases 0–5 are entirely dump-only, and that's the intended sequencing. At the end of Phase 5
the platform is source-neutral but still analyzes only dumps: `IArtifactSource` exists with exactly
one implementation, waiting.

This is safe in a way most of the plan isn't, because **expected output is exactly known** — every
finding produced today must still be produced, unchanged, through the new path. Phases 0–5 each
carry a golden-equality gate.

It's also independently valuable if trace never ships: the hardcoded catalog dies (Phase 3), ~30
trend comparers collapse to one differ (Phase 5), the fixed pipeline goes away (Phase 4), findings
gain full observation lineage, and `report.json` unblocks a UI (Phase 8). None of that needs a
second source.

Two hazards come with the dump-first ordering:

- **Single-implementation abstractions.** Designing `IArtifactSource`, `EntityRef`, and the
  capability vocabulary against only dumps is how a "general" abstraction ends up shaped exactly
  like its one implementation. Phase 2's stub-second-source exit criterion exists for this and
  should be defended against being cut for time.
- **The riskiest unknown sits behind the biggest investment.** Phases 0–5 are the bulk of the
  effort, and they complete *before* anyone learns whether cross-source entity join actually works.
  If dump-side and trace-side names don't canonicalize to the same key, correlation is worthless
  no matter how well those phases went.

**Recommended fix — pull the entity-join spike into Phase 1.** Don't wait for Phase 6 (or § 8
step 4) to measure join rates. A throwaway probe that extracts method/type names from a `.nettrace`
and diffs them against ClrMD-side names from a dump of the same process is days of work, needs no
trace *source*, and does double duty: it's the go/no-go signal for the whole multi-source thesis,
**and** it tells `EntityCanonicalizer` — built in Phase 1 regardless — what it actually has to
handle. Without it, the canonicalization rules in
[source-model.md § 4](modularity/source-model.md) rest on assumptions about how each source formats
names, which is precisely the thing that's expensive to get wrong.

### Why trace comes at Phase 6, not earlier

Two reasons, both about de-risking. Phase 2's extraction of the columnar/interning/container
machinery means trace ingest inherits a storage layer already proven on 25 GB heaps instead of
reinventing bounded-memory indexing. And Phase 5 validates the observation model against dump-only
sessions where the expected output is *exactly known* (every finding today must still be produced,
unchanged) — so when correlation later misbehaves, it's attributable to correlation rather than to
an unproven substrate underneath it.

The cost of that ordering is real: no trace value ships until quite late. § 8 is the answer if
that's unacceptable.

---

## 4a. Relationship to the analyzer-pipeline / LeadFinding audit

[analyzer-pipeline-stages-and-leadfinding-dedup.md](analyzer-pipeline-stages-and-leadfinding-dedup.md)
audits judgment duplication across the current 4-stage pipeline. It is **not a competing plan** —
it is the dump-only, near-term expression of the same conclusion this plan reaches at Phase 5, and
parts of it are prerequisites here rather than consequences.

### Where the two agree

The audit's recommended boundary — *Analyzer emits pure raw facts → one "Insight" stage owns all
judgment → assembly → render* — is structurally identical to Phase 5 (analyzers emit observations;
synthesis rules own severity/banding/selection; `IFindingGenerator` and `InsightEngine` both
retire). Two independent analyses converging on "there must be exactly one judgment stage" is
reasonable evidence the conclusion is right.

### Where this plan was wrong, now corrected

- **Phase 5 previously kept `AnalyzerDomainResult` "for presentation"**, with section builders
  still reading it independently — preserving precisely the stage-2/stage-3 split the audit shows
  already produces wrong `LeadFinding` output in 6 of 8 builders. The former open question ("should
  detail sections derive from observations too?") *is* the audit's core bug. Resolved: yes. See
  [phase-5](modularity/phase-5-observations-synthesis.md).
- **Stage-1 purity (audit Smell A) is a prerequisite, not a byproduct.** If domain results still
  carry baked composite judgment (`MemoryPressureScore`, `HealthScore`, `SuspicionScore`,
  `GCPressureLevel`, `SeverityScore`, and `LeakCandidateRecord.Severity` — literally
  `InsightFinding`'s own output type) when analyzers begin emitting observations, judgment exists in
  two places again and the migration bakes in the drift it was meant to remove. Added to Phase 0 as
  an audit item and to Phase 5 as gating work.
- **Smell B (pre-curated `Top*` lists) converges with this plan's storage model.** "Collapse N
  capped lists into one complete raw table" is answered by: the raw per-entity table lives in the
  **disk-backed index**, observations reference it via `EvidenceRef`, and selection/ranking happens
  in synthesis or render. That also answers the audit's own open question about bounded memory —
  a complete uncapped table is safe precisely because it's disk-backed, which is what the platform
  already does for heap objects. Sequencing still defers to
  [analysis-profile-removal-plan.md](analysis-profile-removal-plan.md) § 11.

### A contradiction the audit exposed in the observation model

The audit's litmus test — *"if a field requires a hand-picked constant or weight to compute
(`/35.0`, `*0.30`, a threshold cutoff), it doesn't belong in stage 1"* — applies to `Observation`
too, and the model as originally written failed it: an observation typed `gc.pressure` carrying a
`Confidence` is judgment emitted by the analyzer.

Resolved in [observation-and-correlation-model.md § 2a](modularity/observation-and-correlation-model.md):
measures stay raw, observation types are factual characterizations rather than severity claims, and
observation `Confidence` means *measurement* confidence only. All weighting, banding, thresholding
and severity move to synthesis.

This is strictly better for multi-source, not just for purity: a `MemoryPressureScore` computed
inside the analyzer can never be improved by trace data, because the analyzer never sees it. The
same score computed in synthesis sharpens automatically when `trace.gc-events` becomes available —
which is the entire graded-fidelity premise of § 2.

### What should not wait for this plan

**The audit's P0 fixes — Hang, Lock Graph, Finalizable Object, Segment Reservation — should be
fixed now.** They are live correctness bugs (the report's `LeadFinding` can show weaker severity
than the analyzer actually computed), the fix is subtractive, and `NormalizeSectionContractSlots`
already contains the derivation path that replaces the deleted logic. Blocking a correctness fix
behind a multi-quarter platform migration would be the wrong call. The same applies to P1
(Crash/Exception, Async Task) and the confidence-band consolidation.

Doing them first also *reduces* Phase 5's work: every builder that stops constructing
`SectionLeadFinding` inline is one less judgment site to migrate later.

### Also carried forward into this plan

- `ExplainableScoringEngine` (Reporting layer) is a **fourth** scoring location beyond the
  stage-1/stage-2/stage-3 sites — it must be reconciled during Phase 5, not left as a surviving
  independent judgment path.
- The three near-identical confidence-band ladders (`SectionBuilderBase`, `ReportSectionAssembler`,
  `LeakAnalysisSectionBuilder`) collapse into `ConfidenceBreakdown` (Phase 5), but should be
  consolidated *now* per the audit rather than waiting.

---

## 5. What this deletes

- The `SingleDump | MultiDump | TraceOnly | Combined` mode enum — never built
- `SingleDumpOrchestrationService`, `TrendOrchestrationService`, and the proposed
  `TraceOrchestrationService` / `CombinedOrchestrationService`
- `DefaultAnalyzerFeatureModuleCatalog` (hardcoded 30-analyzer list)
- `IAnalysisStage` / `StagedPipelineRunner` fixed pipeline
- ~30 bespoke `IAnalyzerTrendComparer` implementations → one generic differ + a handful of
  justified exceptions
- Per-analyzer `IFindingGenerator` → synthesis rules
- `SingleDumpReportDocument` / `TrendReportDocument` polymorphism → one session report
- The dump/trace analyzer duplication a parallel-pipeline design would force

---

## 6. Where the risk actually is

| Risk | Why it's the one to watch | Mitigation |
|---|---|---|
| **Entity join quality** | If dump-side and trace-side names don't reliably canonicalize to the same key, correlation is worthless regardless of engineering quality | Build a cross-source test corpus (same process, dump + trace) and **measure join rates early in Phase 6** — this is a genuine go/no-go signal for the whole thesis, and it's cheap to obtain |
| **Plausible false correlations** | A correlation engine always finds *something*; a confident wrong finding costs a user a day of chasing a phantom | Confidence caps, negative-control test (different processes → zero correlations), conflict findings, confidence floor, under-claiming narrative wording |
| **Observation volume** | Model's biggest unvalidated assumption — trace analyzers could emit millions | Disk-backed `ObservationStore` from Phase 2; observations are *conclusions*, not per-object records |
| **Phase 4 execution rewrite** | Replaces execution for everything; trend has subtle per-dump sequencing semantics that a naive graph rewrite breaks silently | Strict migration order with output-equality verification at each step |
| **Concurrent large-artifact loading** | The DAG makes artifact parallelism look free. It is not — concurrent multi-GB dump loads have OOM-crashed machines here before | Artifact parallelism **off by default**, explicit opt-in + memory budget check, encoded as a test not a comment |
| **Phase 1 gets rushed** | It ships no user-visible value, so it's the phase most likely to be shortchanged — and the identity model is the hardest thing to retrofit | Treat `EntityCanonicalizer` as its own reviewed, test-heavy deliverable |

---

## 7. Preserved constraints

Non-negotiable regardless of restructuring, and they apply to traces exactly as to heaps (a trace
can be larger than a dump):

- Streaming, single-pass ingest; never materialize a full heap or full event stream
- Disk-backed indices with `ArrayPool` buffers and interning
- `EntityRef`/`Observation` are **observation-layer** types — thousands of instances, never
  allocated per heap object or per trace event. Indices use interned integer IDs internally;
  `EntityRef` materializes only at observation boundaries. This constraint must not be relaxed
- Analyzer failures scoped and non-fatal
- Real-artifact tests run one at a time, foreground, never in parallel

---

## 8. If the full program is too much — the minimum viable unified path

The fastest route to "dumps + traces + combined reports" while keeping the architecture honest,
skipping the parts that are refactor rather than capability:

1. **Phase 1, identity + capability + observation contracts only.** Skip the full SDK extraction;
   just add the new types. This is the irreducible core — without `EntityRef` there is no
   correlation.
2. **Phase 2, storage extraction only.** Pull out the columnar/intern/container primitives so trace
   ingest can reuse them. Skip the `Sources.ClrDump` reorganization; leave dump code where it is
   behind a thin `IArtifactSource` adapter.
3. **Phase 6 trace ingest + 2–3 analyzers** (CPU hotspot, contention). Real new value.
4. **Cross-source join measurement.** Before building more: measure entity join rates on a real
   dump+trace pair. Go/no-go.
5. **Phase 7, two correlation recipes** (leak-with-allocation-site, contention-with-duration) —
   enough to prove the thesis and deliver findings neither source produces alone.
6. **`report.json` unconditional** (from Phase 8) so a UI has a contract.

Defer entirely: the plugin split (Phase 3), the DAG rewrite (Phase 4), the trend-comparer collapse
(Phase 5), isolation (Phase 9). Those are *architecture* wins; the above is the *capability* win.
The ordering above deliberately buys the go/no-go measurement at step 4 before the largest
investments.

The tradeoff is honest: skipping Phases 3–5 means the mode-explosion problem comes back, since
without the session DAG something still has to route dump vs. trace vs. combined. Accept an interim
router, with the explicit understanding it's technical debt the deferred phases are meant to pay
off — not a permanent design.

---

## 9. Open questions

- **Multi-process sessions** — a distributed hang spans processes; the model assumes one
  `PrimaryProcess`. Extending means process identity joins every `EntityRef` key.
- **Live targets as artifacts** — attaching to a running process is "an artifact that keeps
  producing capabilities." Not designed for.
- ~~**Does `AnalyzerDomainResult` survive?**~~ **Resolved** (§ 4a): detail sections derive from
  observations, not from domain results. Leaving section builders reading domain results
  independently is the exact duplication the analyzer-pipeline audit shows is already producing
  wrong output. Domain results survive only as a transitional presentation payload during Phase 5,
  carrying no judgment fields.
- **Declarative vs. code synthesis rules** — leaning hybrid (code rules, declarative matching).
- **Capability & observation-type vocabulary governance** — shared namespaces across plugins;
  needs registries with versioning discipline or they fragment.
- **Static report UI vs. live query UI** — the former is a Phase 8 deliverable; the latter needs a
  long-running host exposing capability query surfaces, which Phase 4 makes possible but does not
  scope.

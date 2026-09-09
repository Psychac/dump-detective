# Modular Multi-Source Diagnostics Platform — Architecture & Migration Plan

Status: draft; entity-join spike resolved 2026-09-08 (go, § 8 step 4). **§ 8's minimum-viable path
adopted as the chosen plan, 2026-09-08** (see [§ 10 point 7](#10-external-review-2026-09-08--where-this-can-be-questioned)):
build the new trace/correlation capability first, refactor the existing dump pipeline (Phases 3–5,
9) later. Phase 1 not yet started.
Supersedes the dump-only modularity draft and reworks
[../improvements/unified-dump-trace-architecture.md](../improvements/unified-dump-trace-architecture.md)
into the modularity plan rather than treating them as separate efforts.

**Scope warning up front.** This describes a platform, not a refactor. Executed fully it is a
multi-quarter program. **§ 8 is the chosen path, not just a fallback** — it gives a much smaller
route to the same user-visible outcome without first re-platforming the ~30 analyzers and
orchestration that already ship and work today. Read § 8 first; the full program below it is the
long-term direction Phases 3–5/9 pay off into once the new capability has proven itself, not the
immediate plan.

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
| [6a — Trace ingest](modularity/phase-6-trace-source.md#phase-6a--trace-ingest) | `.nettrace` streaming ingest, trace index, entity-resolution corpus | 2 |
| [6b — Trace-fed analyzers](modularity/phase-6-trace-source.md#phase-6b--trace-fed-analyzers) | First trace analyzers (needs 6a, 1); optional-capability wiring on existing dump analyzers (needs 6a, 5 — deferred under § 8) | 6a, 1 (+5 for the deferred sub-item) |
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

**A second, equally cheap spike belonged alongside it: verify `TraceEvent`/`EventPipeEventSource`
before Phase 6 commits to it.** ~~[phase-6-trace-source.md](modularity/phase-6-trace-source.md)
flagged its ingest library as "unverified in this session"~~ — **resolved 2026-09-08, go, with a
design correction.** Ran `tools/TraceEventSpike` against a real 54.9 MB ETW capture: MIT-licensed,
and the raw event-callback reader (`ETWTraceEventSource`/`EventPipeEventSource`) streams with flat
7 MB working-set delta across a 1.5M-event pass, independent of trace size — genuine bounded-memory
streaming. The correction: `TraceLog.OpenOrConvert` — the API `tools/EntityJoinSpike` happened to
use — is a different, non-streaming tool that measured 4.2× the source size in working set and
2.73× on disk to build its random-access index, and must not be the API Phase 6's bulk ingest is
built on. Full write-up in
[phase-1-contracts-sdk.md § TraceEvent dependency spike](modularity/phase-1-contracts-sdk.md#traceevent-dependency-spike--measured-2026-09-08),
correction folded into [phase-6-trace-source.md](modularity/phase-6-trace-source.md#ingest).

### Why trace comes at Phase 6, not earlier

Two reasons, both about de-risking. Phase 2's extraction of the columnar/interning/container
machinery means trace ingest inherits a storage layer already proven on 25 GB heaps instead of
reinventing bounded-memory indexing. And Phase 5 validates the observation model against dump-only
sessions where the expected output is *exactly known* (every finding today must still be produced,
unchanged) — so when correlation later misbehaves, it's attributable to correlation rather than to
an unproven substrate underneath it.

The cost of that ordering is real for *analyzer* value — no trace-fed finding ships until quite
late. ~~§ 8 is the answer if that's unacceptable.~~ **Partially mitigated 2026-09-08:**
[phase-6-trace-source.md](modularity/phase-6-trace-source.md) is now split into 6a (ingest +
entity-resolution corpus, needs only Phase 1/2) and 6b (analyzer wiring, still gated on Phase 5) —
see [§ 10 point 5](#10-external-review-2026-09-08--where-this-can-be-questioned). That lets the
highest-risk unknown in the whole trace effort (do dump and trace entity refs actually join?) get
validated in parallel with Phase 5 instead of after it, which is the earliest this plan can produce
a real go/no-go signal on the multi-source thesis. It does not remove the ordering cost for
*findings* — 6b, and therefore any trace-fed finding, still waits on Phase 5. § 8 remains the answer
if even that residual cost is unacceptable.

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
  already does for heap objects. What this was sequenced behind is done: the AnalysisProfile removal
  plan it deferred to is complete and its doc retired from the tree (see git history, commit
  `ad37513b`) — nearly every analyzer's `Top*`-list migration closed GREEN in that audit's §9, so
  this phase inherits a largely-finished migration rather than a pending one.

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

## 4b. Relationship to the report vision doc

[ReportSystemVision.md](../ReportStructure/ReportSystemVision.md) is the from-scratch specification
for the report this plan eventually feeds. It's a consumer of this plan, not an alternative to it —
its session model is this plan's session model (its § 3.2), and its observation store is explicitly
aligned to [observation-and-correlation-model.md](modularity/observation-and-correlation-model.md)
(its § 5.1), restated there "because the report is its consumer and depends on the purity rule."

Where it disagrees is sequencing. This plan schedules the report last (Phase 8); its § 21 open
question 1 argues the opposite — the report should lead, because it's the only consumer that makes
the observation model's value visible to anyone outside the team. The disagreement is narrower than
it looks, and the vision doc's own § 19 minimum-viable path already shows why: M1 (claims), M2
(entities), and M4 (payload/virtualized tables) are marked as depending on nothing, meaning they can
be built now, as a thin adapter over today's `AnalyzerDomainResult` / `InsightFinding` — they don't
need Phase 5's observations to exist. This lines up with what
[phase-8](modularity/phase-8-sinks-and-ui.md) already says about `IReportSink` and unconditional
`report.json`: those land "right after Phase 1," not behind Phase 5.

What genuinely can't move earlier is anything claiming full observation lineage — a claim citing the
observations that support it, confidence split into measurement vs. inference, cross-analyzer
synthesis without a bespoke `InsightEngine` per analyzer. The vision doc's own mapping (§ 19, "§3
sessions, §5 observations → modularity Phases 1, 5") already says this. Building the claim graph's
`derivedFrom`/`support`/`counter` fields against pre-Phase-5 domain results would mean re-deriving
them once observations land — the same two-independent-passes-over-the-same-facts failure mode
[§4a](#4a-relationship-to-the-analyzer-pipeline--leadfinding-audit) diagnoses, one layer up.

So: neither doc needs to be resequenced. The report can start now (M1–M4, against today's data); it
just can't claim full lineage until Phase 5 exists to back it.

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

## 8. The minimum viable unified path — ADOPTED as the chosen plan, 2026-09-08

**Decision** (see [§ 10 point 7](#10-external-review-2026-09-08--where-this-can-be-questioned)):
build new — the identity/capability/observation contracts, trace ingest, trace analyzers,
correlation — right from the start, but don't refactor what already ships and works
(Phases 3–5, 9) until the new capability has proven itself. The reasoning: Phases 3–5 aren't
greenfield code, they're a re-platform of the ~30 analyzers and orchestration already in
production use (Phase 5 gates itself on byte-identical golden-file equality against every existing
finding precisely because it's touching live behavior, not building new). "Do it right from the
start" is a much easier bar for code that doesn't exist yet than for a refactor of code real output
already depends on — and for effectively solo-maintained work, front-loading that refactor ahead of
any validated new value is the riskier bet, not the safer one.

The fastest route to "dumps + traces + combined reports" while keeping the architecture honest,
skipping the parts that are refactor rather than capability:

1. **Phase 1, identity + capability + observation contracts only.** Skip the full SDK extraction;
   just add the new types. This is the irreducible core — without `EntityRef` there is no
   correlation. **Do the TraceEvent dependency spike (Phase 1 step 3a) first** — it's the one
   remaining unverified assumption gating step 3 below, and it's cheap to check before anything
   else here is built.
2. **Phase 2, storage extraction only.** Pull out the columnar/intern/container primitives so trace
   ingest can reuse them. Skip the `Sources.ClrDump` reorganization; leave dump code where it is
   behind a thin `IArtifactSource` adapter.
3. **Phase 6a (trace ingest) + Phase 6b's new-analyzer track — 2–3 analyzers** (CPU hotspot,
   contention). Real new value. Excludes 6b's other sub-item (optional-capability wiring on
   *existing* dump analyzers) — that genuinely needs those analyzers migrated to observations, i.e.
   real Phase 5 work, so it's deferred along with Phase 5 itself, not part of this path.
   **Done, 2026-09-09**: `GcPauseAnalyzer`, `ContentionAnalyzer`, and `CpuHotspotAnalyzer` all
   shipped — CPU hotspot via a leaf-frame-only scope cut (resolves the sample's own instruction
   pointer against `trace.methods`'s address ranges, skipping the `trace.stacks` call-tree work
   named and deferred when `trace.methods` shipped, so exclusive-only, not inclusive) — plus the
   interim router this section already names below, now built rather than only accepted as debt.
   See [phase-6-trace-source.md § Phase 6b](modularity/phase-6-trace-source.md#the-interim-router--shipped-2026-09-09).
4. ~~**Cross-source join measurement.** Before building more: measure entity join rates on a real
   dump+trace pair. Go/no-go.~~ **Resolved 2026-09-08 — go.** See below.
5. **Phase 7, two correlation recipes** (leak-with-allocation-site, contention-with-duration) —
   enough to prove the thesis and deliver findings neither source produces alone.
6. **`report.json` unconditional** (from Phase 8) so a UI has a contract. **Done, 2026-09-09, for
   trace sessions** — `TraceSessionReport`/`TraceReportWriter`, written every run regardless of
   `--output`. Deliberately not Phase 8's full schema v3 (`sources[]`/`timeline`/
   `capabilityReport`) — that needs Phase 4's session model and Phase 5's synthesis engine, neither
   in scope here. See
   [phase-6-trace-source.md § report.json](modularity/phase-6-trace-source.md#reportjson--8-step-6--shipped-2026-09-09).
   Dump-side `report.json` (today conditional on `--output`) unchanged — out of scope for this
   step, which was scoped to what § 8's already-shipped trace work could support.

Defer entirely: the plugin split (Phase 3), the DAG rewrite (Phase 4), the trend-comparer collapse
(Phase 5), isolation (Phase 9). Those are *architecture* wins; the above is the *capability* win.
The ordering above deliberately buys the go/no-go measurement at step 4 before the largest
investments.

The tradeoff is honest: skipping Phases 3–5 means the mode-explosion problem comes back, since
without the session DAG something still has to route dump vs. trace vs. combined. Accept an interim
router, with the explicit understanding it's technical debt the deferred phases are meant to pay
off — not a permanent design. This is the debt the decision above knowingly takes on. **Built
2026-09-09** — `DumpAnalysisService`'s extension-sniffed routing to `TraceOrchestrationService`, see
the phase-6b cross-reference above. Trace-only only; a combined dump+trace session still isn't
wired.

### Entity-join spike — measured, 2026-09-08

Ran `tools/EntityJoinSpike` (see line 141 above) against two real dump+ETL pairs, both
`w3wp.exe` / `BALLOADTESTEXAPIS`, joining ClrMD heap-live type names against TraceEvent
method-declaring-type names for the same process.

**Pair 1 — April 30, PID 10160.** Dump at 12:42 PM, ETL captured 12:48 PM (`/MaxCollectSec:60`).
8,180 distinct dump types, 1,537 distinct trace types. Exact-string join: 562 matched — 6.9% of
dump types, 36.6% of trace types. The dump-only 93% is dominated by pure-data BCL types
(`System.String`, arrays, `RuntimeType`, resource-manager internals) that structurally can't have
a trace-side match — they own no executing methods — so this isn't evidence against joinability,
just a denominator effect. The matched set includes real app/framework types (EF `Edm.*`, ASP.NET
pipeline, WCF, DevExpress) — exactly what a leak-with-allocation-site recipe would key off.

**Pair 2 — May 8, PID 8044.** Dump at 5:35 PM, ETL captured over the same session
(`/MaxCollectSec:1200`). 6,082 distinct dump types, 2,928 distinct trace types. Exact-string join:
968 matched — 15.9% of dump types, 33.1% of trace types.

**Compiler-generated subset (lambdas/closures/state machines), the specific worry about ordinals
shifting between builds:**

| | Pair 1 (60s trace) | Pair 2 (~20min trace) |
|---|---|---|
| Dump types that are compiler-generated | 790 / 8,180 | 454 / 6,082 |
| Exact-string match within that subset | 10.8% (85/790) | 35.0% (159/454) |
| + ordinal-stripping canonicalization | 22.0% (174/790) | 39.9% (181/454) |
| Uplift from canonicalization | +11.3 points | +4.8 points |

Before trusting Pair 2 as a genuine cross-build data point, verified it actually was one: PDB Guid
of `Excellon.FW5.Data.dll` differs between the two dumps (`411d277e-...` vs `2ff84ead-...`, plus a
1,728,512 vs 1,744,896 byte size difference), confirming Pair 1 and Pair 2 are different builds,
not the same build re-captured. (Checked with a second throwaway probe,
`tools/ModuleTimestampProbe`, comparing `ClrModule.Pdb` across dumps — cheap, no heap walk.)

**Conclusion:** canonicalization measurably recovers real matches across a confirmed rebuild
(+5 to +11 points on the compiler-generated subset, including genuine app-level closures like
`Excellon.FW5.Stores.DbStore+<>c__DisplayClass13_0`) — the mechanism has real substance, not zero.
But it's a second-order effect: the dominant swing between the two pairs (10.8% → 35.0% exact
match) tracks trace **duration/coverage** (60s vs ~20min), not build drift — a lot of the
remaining unmatched compiler-generated types are `<>c` lambda-cache singletons that simply never
appeared on a sampled call stack in the shorter window, regardless of naming. Cross-build ordinal
instability is real but smaller than the coverage-window effect, and it's exactly the class of risk
`MatchFidelity` ([source-model.md § 4](modularity/source-model.md)) was designed to discount rather
than something that blocks Phase 1.

**Decision: go.** Proceed to Phase 1 (identity + capability + observation contracts). Cross-build
drift stays a tracked, capped-fidelity risk rather than an open blocker.

**Scope of this "go," stated precisely:** the measured rates above are aggregate join rates across
all types, most of which nobody will ever ask to correlate. They are sufficient evidence that the
canonicalization mechanism has real substance and that Phase 1 is worth building. They are **not**
evidence that any specific recipe — `leak-with-allocation-site` in particular — will reliably join
the one type an investigator actually cares about, especially against a short trace capture. That
question is recipe-level, not aggregate, and it is exactly what Phase 7's negative-control test and
precision/recall measurement (see
[phase-7-cross-source-correlation.md](modularity/phase-7-cross-source-correlation.md)) are for. Do
not read this section as a guarantee that correlation recipes will fire reliably — only that the
identity layer they depend on is worth building.

**Corpus diversity — accepted as a residual, documented risk, 2026-09-08.** Both pairs are the same
app family and the same host shape (`w3wp.exe`/`BALLOADTESTEXAPIS`, WCF/EF-heavy). [§10 point
3](#10-external-review-2026-09-08--where-this-can-be-questioned) recommended widening the corpus
with a non-WCF/EF shape (ASP.NET Core self-hosted, a plain console host) before committing to
Phases 6–7. That corpus isn't obtainable — no such sample is available to capture from. Considered
and rejected: authoring a synthetic self-hosted app to manufacture the missing shape; not pursued
today, so this remains unvalidated rather than closed. Decision: proceed on the existing two-sample
evidence anyway, since it's still real production data showing the join mechanism has substance,
and explicitly carry the gap forward rather than treat it as resolved. **Concretely, this means the
"go" above is scoped to WCF/EF-on-w3wp.exe; whether canonicalization holds up on a different host
shape (Kestrel self-hosted, a plain console host, minimal-API style code without WCF/EF's
distinctive type-naming patterns) is unknown**, and Phase 6/7 work should watch for this the first
time a non-WCF/EF customer dump becomes available, rather than assuming the aggregate rates above
generalize.

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
- ~~**Capability & observation-type vocabulary governance** — shared namespaces across plugins;
  needs registries with versioning discipline or they fragment.~~ **Resolved** (see
  [§10 point 4](#10-external-review-2026-09-08--where-this-can-be-questioned)):
  [phase-1-contracts-sdk.md](modularity/phase-1-contracts-sdk.md) already specifies checked-in,
  versioned `capability-registry.json`/`observation-type-registry.json` plus a CI-enforced
  registry-conformance architecture test, landing in Phase 1.
- **Static report UI vs. live query UI** — the former is a Phase 8 deliverable; the latter needs a
  long-running host exposing capability query surfaces, which Phase 4 makes possible but does not
  scope.

---

## 10. External review (2026-09-08) — where this can be questioned

An independent architect's plan for "modularize + improve reporting + dumps/traces/combined,"
worked out before reading this document, converged with it point for point (session model,
capability declarations, observation substrate, entity identity with fidelity caps, judgment moved
to synthesis, capability-resolved DAG, domain-not-source packaging). That convergence is a
reasonable signal the shape is right. The pushback below is what's left after agreeing with the
shape.

1. **Execution is falling behind planning — RESOLVED 2026-09-08.** All six audited `LeadFinding`
   builders (P0: Hang, Lock Graph, Finalizable Object, Segment Reservation; P1: Crash/Exception,
   Async Task) no longer construct `SectionLeadFinding` inline; each now derives it solely from its
   `IFindingGenerator` via `ReportSectionAssembler.NormalizeSectionContractSlots`, per the
   [P0](analyzer-pipeline-stages-and-leadfinding-dedup.md#p0-fix-plan--hang-lock-graph-finalizable-object-segment-reservation)/[P1](analyzer-pipeline-stages-and-leadfinding-dedup.md#p1-fix-plan--crashexception-async-task)
   fix plans. Three of the six (Finalizable Object, Segment Reservation, Crash/Exception) needed a
   generator-side port first so no builder-only signal or judgment was silently dropped; the other
   three were pure deletions. The confidence-band consolidation this document's own audit flagged as
   safe to do now is also done: `SectionBuilderBase.SymbolForScore` is `internal` and shared by
   `ReportSectionAssembler` and `LeakAnalysisSectionBuilder`, which no longer carry their own copy
   of the ladder. Build clean, 303 tests pass. This item is closed — nothing left in it that's safe
   to do ahead of the Phase 5 migration.
2. **Report-vision sequencing — RESOLVED 2026-09-08.** ~~Still framed as an open disagreement when
   it's mostly decided.~~ [§4b](#4b-relationship-to-the-report-vision-doc) and `ReportSystemVision.md`
   § 21 (now updated to match) both conclude M1 (claims)/M2 (entities)/M4 (payload) can be built now
   as thin adapters over today's `AnalyzerDomainResult`/`InsightFinding`, not gated on Phase 5.
   Decision: start with M1 (claims) first, since M2/M4 are pure-data/rendering moves with no
   adapter-drift risk, while M1 is the one §19 places an explicit bound on ("no new judgment
   invented inside the adapter") — worth proving out on the smallest slice before M2/M4 follow. M1
   scoping/kickoff is the next concrete action, not a backlog item competing with further design
   work.
3. **The entity-join "go" decision rests on a thin corpus for the size of the bet — ACCEPTED AS
   RESIDUAL RISK 2026-09-08, not resolved.** Two samples, same app family, same host
   (`w3wp.exe`/`BALLOADTESTEXAPIS`, WCF/EF-heavy) — good evidence canonicalization has real
   substance (as § 8 already states precisely), but Phases 6–7 are the largest-effort phases in the
   plan. Recommended widening the corpus (a non-WCF/EF shape — ASP.NET Core self-hosted, a plain
   console host) before committing engineering months to trace ingest, not after. No such sample is
   obtainable; a synthetic stand-in was considered and not pursued. Decision: proceed on the
   existing corpus, with the gap explicitly carried forward rather than closed — see § 8's
   entity-join spike write-up, now updated with this caveat, for where it's tracked.
4. **Capability/observation-type vocabulary governance — RESOLVED 2026-09-08, already better than
   recommended.** ~~Is an open question but is the same failure mode this plan exists to kill, one
   layer up~~ — ~30 independently-drifted trend comparers and 6 divergent `SectionLeadFinding`
   builders (fixed in [§10 point 1](#10-external-review-2026-09-08--where-this-can-be-questioned))
   are exactly "uncoordinated parallel judgment," and an ungoverned `ObservationType` string
   namespace across plugins is structurally the same risk. On checking, this document's own
   [§ 9](#9-open-questions) and [observation-and-correlation-model.md § 7](modularity/observation-and-correlation-model.md#7-open-questions)
   still phrased it as an unresolved open question, but [phase-1-contracts-sdk.md](modularity/phase-1-contracts-sdk.md)
   already specifies the concrete answer: checked-in, versioned `capability-registry.json` /
   `observation-type-registry.json`, validated at build time via an "SDK-boundary and
   registry-conformance" architecture test (migration step 7, exit criterion). That's both pieces of
   the recommendation — a real registry *and* a CI-enforced conformance check — landing in Phase 1,
   earlier than the "by Phase 3" ask. The only gap was documentation drift: §9 and the correlation
   model's §7 hadn't been updated to point at Phase 1's answer. Both now marked resolved with a
   cross-reference; no design or code change was needed here, since the plan already had this right.
5. **Phase 6's dependency on all of Phase 5 is broader than the ingest half needs — RESOLVED
   2026-09-08.** ~~Streaming `.nettrace` ingest into columnar sections only needs the Phase 1/2
   substrate (`Observation`, `IObservationSink`, columnar storage) — not the full 30-analyzer
   migration. Only the optional-capability wiring on *existing* dump analyzers needs Phase 5 done.
   Recommend splitting Phase 6 into 6a (ingest, parallelizable with Phase 5) and 6b (analyzer
   wiring, gated on Phase 5) to shorten the "no trace value ships until quite late" critical path
   § 4's own cost accounting names.~~ [phase-6-trace-source.md](modularity/phase-6-trace-source.md)
   is now split exactly this way: **6a** (ingest, index sections, and the cross-source
   entity-resolution corpus — depends only on Phase 2) and **6b** (trace-fed analyzers plus
   optional-capability wiring on existing dump analyzers — depends on 6a and Phase 5). Moving entity
   resolution into 6a is a further improvement beyond the original recommendation: it's the
   highest-risk unverified assumption in the whole trace effort (do dump/trace entity refs actually
   join?), and 6a now produces that go/no-go signal in parallel with Phase 5 rather than after it.
   [§ 4](#4-phases) updated to reflect the reduced (but not eliminated — 6b findings still wait on
   Phase 5) critical-path cost.
6. **Phase 0's test-coverage exit criterion — RESOLVED 2026-09-08.** ~~Bounds by domain count, not
   scenario diversity, and it is the sole safety net for the two highest-behavioral-risk phases (4
   and 5). Given this codebase's own history of a regex-drift regression slipping past existing
   tests in the same session it was introduced, recommend tightening the criterion to cover each
   analyzer's distinct branches/severity tiers, not one snapshot per domain.~~
   [phase-0-foundation.md](modularity/phase-0-foundation.md) item 5 and its exit criterion are now
   tightened exactly this way: each analyzer domain's distinct severity tiers and decision branches
   must each have a covering characterization test, not just ≥ 1 snapshot per domain, with the
   `AsyncStateMachineAnalyzer` regex-drift regression cited as precedent for why domain-count alone
   isn't a sufficient bar. Risk/effort section updated to flag this as real per-analyzer analysis
   work, not a mechanical snapshot-and-move, since it's the only safety net Phases 4–5 have.
7. **§ 8's minimum-viable path is honest that it re-accepts an interim router as deliberate
   debt — DECIDED 2026-09-08.** ~~Worth an explicit business decision (full program vs. minimum
   viable) before more design time goes into either path; not something architecture alone should
   decide.~~ **Decision: minimum-viable path (§ 8), adopted.** Build the new trace/correlation
   capability first; refactor the existing dump pipeline (Phases 3–5, 9) later, once the new
   capability has proven itself. Reasoning: Phases 3–5 re-platform ~30 analyzers and orchestration
   already shipping and working today, not greenfield code — Phase 5 gates itself on byte-identical
   golden-file equality against every existing finding precisely because it's touching live
   behavior. For effectively solo-maintained work, front-loading that refactor ahead of any
   validated new value is the riskier bet. The interim-router debt is knowingly accepted, not
   ignored — see § 8's updated framing and
   [phase-6-trace-source.md](modularity/phase-6-trace-source.md)'s note (in the 6a/6b split at the
   top of that doc) on the related risk of building trace analyzers on an observation model Phase 5
   hasn't yet validated. Status line and § 8 heading updated to reflect this is now the chosen plan,
   not a fallback.
8. **No phase doc owns retyping analyzers from `Core`'s `IAnalyzer`/`AnalysisContext` to the SDK's
   — IDENTIFIED 2026-09-09, not resolved.** Found while scoping out
   [phase-1-contracts-sdk.md](modularity/phase-1-contracts-sdk.md)'s deferred "full SDK extraction"
   item (`Analysis/IAnalyzer.cs`, `AnalysisContext.cs`, the capability attributes,
   `Presentation/IAnalyzerSectionBuilder.cs`). That item isn't a file move — the real
   `src/DumpDetective.Core/Models/AnalysisContext.cs` carries a dated `// Intentional boundary
   decision (Phase 7): Core remains dump-runtime-aware` comment and directly exposes `ClrRuntime`,
   which an SDK type (zero `PackageReference`s, enforced by
   `SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl`) cannot. Tracing the real dependency chain
   this implies surfaces a gap none of Phases 2–5 currently claim:
   - **Phase 2 migration step 3** (still deferred, no target design written) is the closest owner of
     "replace `IHeapAnalysisCache` with capability-scoped query surfaces," but that interface is
     bigger and more ClrMD-coupled than its one-line mention suggests: 18 members
     (`src/DumpDetective.Core/Abstractions/IHeapAnalysisCache.cs`), nearly every one taking `ClrHeap`
     or `ClrThread` directly as a parameter (root/static-field lookups, stack-frame-owner
     resolution, type statistics, four separate provider accessors for reverse/forward-reference,
     reachability, and dominator-tree queries, thread retention, global size buckets, distinct
     method tables). None of it can be exposed through an SDK-typed surface as-is.
   - **Phase 1 itself** would then design the new capability-scoped `AnalysisContext`/`IAnalyzer` —
     but only *after* Phase 2 step 3's surfaces exist, since `AnalysisContext`'s shape is derived
     from what capability query interfaces are available to resolve, not designed independently.
   - **The step nobody owns:** actually retyping each analyzer's `: IAnalyzer` and `AnalysisContext
     context` parameter to the new SDK types. Grepped: **35 files implement `IAnalyzer` today**
     (`src/DumpDetective.Analysis/Analyzers/*.cs`), and **all 35 reference `context.Heap` or
     `context.Runtime` directly** — not filtered through `IHeapAnalysisCache` alone, meaning the
     coupling this retyping has to unwind is closer to universal than partial. Phase 3's migration
     steps only add capability *attributes* for discovery on top of whatever `IAnalyzer` an analyzer
     already implements — they never mention changing the interface or context type itself. Phase 5
     assumes analyzers already have a context capable of producing `Observation`s via
     `IObservationSink` by the time its work begins, but doesn't say how they got one. **This
     retyping — 35 analyzers, near-universal direct `ClrHeap`/`ClrRuntime` coupling, needing the
     same byte-identical-output discipline Phase 5 already applies to its own migration — currently
     has no phase, no migration steps, and no exit criterion anywhere in this plan.** Whether it
     belongs inside Phase 2 (immediately after step 3), as a new step in Phase 3, or as a
     precondition folded into Phase 5's own work item 1 is an open sequencing question this document
     doesn't answer yet. Not resolved here — flagged so it isn't silently discovered mid-refactor the
     way § 10 point 1's `SectionLeadFinding` drift was.
     **Sequencing answered 2026-09-09**, with a further finding that makes the bet bigger than this
     point estimated: [phase-1-full-extraction-retyping-plan.md](modularity/phase-1-full-extraction-retyping-plan.md).
     24 of the 35 analyzers do field-level object introspection (`ClrType.Fields`-equivalent reads),
     not just coarse heap enumeration — a full source-neutral object/field model was considered and
     rejected as speculative (no real second consumer needs it), so the plan splits capability
     surfaces into a Tier 1 (SDK-safe coarse enumeration, ships now, zero behavior change) and a
     Tier 2 (`dump.object-fields`, stays dump-only) instead. The retyping step itself gets a pilot
     analyzer, per-batch characterization gates, and lands as its own track running alongside
     Phase 3 rather than sequentially before or after it — the two are similar in size and largely
     touch the same 35 files, so doing them separately would mean touching each file twice.

---

## 11. Lessons from a sibling implementation (`d:\POC\Rohit_DumpDetective`)

A second, independently-built tool in this project's lineage already ships trace ingest, dump+trace
correlation, a plugin system, and a multi-format report pipeline in production. It is real ground
truth, not another plan document, and reading its code (not just its docs) changed two load-bearing
judgment calls in this plan and validated several others. Full detail is folded into the relevant
phase docs; this section is the index.

**Checked, and one initially-drafted "change" was reverted after checking our own data:**

- **The trace-ingest API choice was briefly reopened, then confirmed as originally decided.** [Phase
  1's TraceEvent
  spike](modularity/phase-1-contracts-sdk.md#cross-checked-against-a-sibling-implementation-rohit_dumpdetective--and-against-our-own-data)
  measured a 2.73× disk / 4.2× working-set cost for `TraceLog.OpenOrConvert` on a 54.9 MB sample. The
  sibling ships `TraceLog.OpenOrConvert` as its only ingest path and a code comment there cites a
  27,687 MB → 13,966 MB (~0.5×, shrinking) conversion, which an earlier pass through this plan
  treated as reason to reopen the "never `TraceLog`" decision. That was a mistake, caught by spot-checking
  this project's own data rather than trusting a number in someone else's comment: a real 912.1 MB
  capture already on disk (`D:\Dumps\08-05\etls\HighCPU_11.etl`) converts to a 2090.1 MB `.etlx` —
  **2.29× growth**, the same direction as the original 54.9 MB sample, not the sibling's number.
  Two real, same-direction measurements from this project's own data at two different scales beat
  one unverified figure from a log-parsing code comment. **[Phase
  6](modularity/phase-6-trace-source.md#ingest)'s original decision stands**: build `IndexAsync` on
  the raw event-callback reader, not `TraceLog`. What the sibling's code still legitimately adds:
  `TraceLog` resolves stacks/symbols/method-names for free during conversion, and the raw-callback
  path must now explicitly budget for that resolution as real engineering work, which this plan
  previously assumed away. Still open: every measurement on both sides so far is `.etl` (ETW); a
  real `.nettrace` (EventPipe) sample hasn't been measured.
- **Phase 7 should ship a cheap, ad hoc correlation milestone before the full entity-join
  machinery, not after.** The sibling's 26 correlation rules are hand-written per-rule threshold
  comparisons with no entity-join machinery at all — and only 1 of its 16 cross-source rules does
  anything resembling an entity join (a plain string-set match). [Phase
  7](modularity/phase-7-cross-source-correlation.md#cross-checked-against-a-sibling-implementation--a-cheaper-path-ships-real-value-first)
  now proposes **Phase 7a**: ship signal-level correlation rules as soon as Phase 6's trace analyzers
  exist, in parallel with (not gated behind) the full `EntityRef`/`ConfidenceBreakdown` work, which
  then needs to land only for the minority of recipes that actually require an entity join. This is
  a concrete shape for this plan's own [§ 8](#8-if-the-full-program-is-too-much--the-minimum-viable-unified-path)
  minimum-viable-path argument.

**Confirmed (no change, but worth citing as evidence):**

- Its 26 rules being hand-tuned, independently-drifting `score += 15` arithmetic is a second,
  independent occurrence of the exact failure mode this plan's Phase 5 and the analyzer-pipeline
  audit exist to kill (see [§ 10 point 4](#10-external-review-2026-09-08--where-this-can-be-questioned)) — good
  evidence `ConfidenceBreakdown`'s named, versioned scoring is worth building, not over-engineering.
- Its `FrameInterner` (stack-frame string interning, `Dictionary<string,string>`, truncate-then-intern
  for long compiler-generated names) is close to identical to this plan's own stack-interning design
  in [phase-6-trace-source.md § Index sections](modularity/phase-6-trace-source.md#index-sections) —
  direct validation, no change needed.
- Its `TraceEventDispatcher` (one pass over `trace.Events`, N `ITraceEventConsumer`s, a `WantsEvent`
  filter evaluated once per unique event name so uninterested consumers pay nothing per occurrence)
  is the same "one pass, many consumers" discipline this project already applies to heap scanning —
  worth copying into Phase 6's ingest design explicitly, now added there.
- Its plugin system needs **four separate participation interfaces**
  (`ICommand`, `ITracePlugin`, `ITraceSubAnalyzer`, `ITraceDumpCorrelationRule`) because plugin
  discovery is per-orchestrator rather than capability-declared — a real instance of the interface
  proliferation this plan's capability-attribute model ([source-model.md §
  3](modularity/source-model.md)) is designed to prevent. Supports keeping that design over a
  simpler interface-per-mode approach, even though the simpler approach is what shipped first there.
- Its report pipeline requires an 11-step, 10-file checklist to add one new visual element type
  (`ReportDoc.cs` → `CoreJsonContext.cs` → `IRenderSink.cs` → `HtmlSink.cs`/`.css` → `CaptureSink.cs`
  → `BinSink.cs` → `JsonSink.cs` → `MarkdownSink.cs` → `TextSink.cs` → `ReportDocReplay.cs`, with an
  explicit warning that skipping any step "silently" breaks a format) — concrete, previously-abstract
  validation that `ReportSystemVision.md`'s closed, shape-based widget vocabulary is solving a real,
  already-occurred pain point, not a hypothetical one. Noted in
  [ReportSystemVision.md](../ReportStructure/ReportSystemVision.md#appendix-e--cross-checked-against-a-sibling-implementation).
- Its `dd-thresholds.json` externalizes ~19 named scoring thresholds into a config file the scoring
  engine reads (`ThresholdConfig` → `ScoringThresholds`/`TrendThresholds`), falling back silently to
  compiled defaults when absent — a cheap pattern worth adopting *now*, independent of the Phase 5
  migration, as a small step toward removing the hand-picked constants [§ 10 point
  1](#10-external-review-2026-09-08--where-this-can-be-questioned) originally flagged in
  `HangSectionBuilder` and its siblings — the P0 fix moved those constants into the
  `IFindingGenerator`s rather than removing them, so this externalization opportunity still stands.
- Its `ReportDoc`/`ReportDocReplay`/`ReportDiffer` achieve report replay and diff (`render`, `diff`
  commands) entirely by walking a polymorphic report-element tree and matching chapters/sections/rows
  by name or key column — no observation model, no entity join, no typed measure semantics. It is
  more fragile than this plan's Phase 5 design (string-keyed matching, no confidence, no measure-type
  awareness) but delivers real trend/diff value far more cheaply. Worth weighing as a cheaper interim
  step if Phase 5's full migration timeline becomes a concern — not a replacement for it.

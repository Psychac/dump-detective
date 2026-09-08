# Diagnostic Report System — Vision & Full Specification

## Status

Vision + specification. Nothing here is implemented, and adopting it whole is a multi-quarter
program. [§19](#19-minimum-viable-path) gives the subset worth building regardless.

Third sibling to [ReportFormatCleanSlate.md](ReportFormatCleanSlate.md) (what the report contains)
and [ReportTemplateCleanSlate.md](ReportTemplateCleanSlate.md) (how it is built and styled). Those
two critique the report that exists. This one answers a different question: *given the data
DumpDetective already computes, and no obligation to preserve anything, what should the reporting
system be?* Where it disagrees with the siblings,
[§20](#20-where-this-disagrees-with-the-sibling-docs) says so explicitly rather than quietly
diverging.

It also assumes, and argues for, the session/observation model in
[../refactor/modularity-plan.md](../refactor/modularity-plan.md). That plan schedules the report
last (Phase 8). [§21](#21-risks-assumptions-and-open-questions) argues the opposite: the report
should lead, because it is the only consumer that makes the observation model's value visible.

Measured facts cited throughout come from
[ReportFormatCleanSlate.md Appendix A](ReportFormatCleanSlate.md#appendix-a--measured-baseline) —
a 3.7 MB HTML report from a 3.35 GB crash dump, 30.9 MB of inflated payload, 37 sections,
9 domains, 36 analyzers, 175,158 table rows, 15.7 KB of prose.

## Reading guide

| Part | Sections | This is the… |
|---|---|---|
| I — Position | [1](#1-what-the-report-is-for)–[2](#2-the-six-things-id-do-differently) | the argument; read this even if you read nothing else |
| II — Content model | [3](#3-the-session-model)–[8](#8-baselines-and-expectation) | **format spec, logical layer** — session, entity, observation, claim, coverage |
| III — Presentation | [9](#9-information-architecture)–[12](#12-interaction-specification) | **design spec + component spec + interaction spec** |
| IV — Wire format | [13](#13-the-artifact-set)–[16](#16-performance-and-scale-contract) | **format spec, physical layer** — envelope, columns, schema, budgets |
| V — Quality | [17](#17-contracts-as-enforceable-gates)–[21](#21-risks-assumptions-and-open-questions) | gates, anti-goals, the path, the disagreements, the doubts |
| Appendices | [A](#appendix-a--one-claim-end-to-end)–[D](#appendix-d--the-trace-delta) | worked examples and the widget catalog |

---

# Part I — Position

## 1. What the report is for

### 1.1 The job

Not "present the analysis results." The job is:

> **Convert a multi-gigabyte binary artifact into a decision, and make that decision defensible to
> someone who did not run the tool.**

In practice a reader arrives with one of three decisions to make, in this order:

1. **Is something wrong, and how urgent is it?** — minutes, often during an incident.
2. **What specifically is causing it?** — an hour, usually after the incident.
3. **How do I convince my team, and what do I change?** — a meeting, a postmortem, a code review
   comment, a ticket.

Everything in this spec exists to serve one of those three, and anything that serves none of them
is deleted. The current report's 15.7 KB of prose across 37 sections serves none of them well: it
narrates what an analyzer did rather than what the reader should conclude.

### 1.2 The reader — answering the question both siblings left open

[ReportFormatCleanSlate.md §11 question 5](ReportFormatCleanSlate.md#11-open-questions) asks who
the default reader is and answers "both, badly."
[ReportTemplateCleanSlate.md §8.3](ReportTemplateCleanSlate.md#83-mode-classes) notes the shipped
`reading-mode-incident` / `reading-mode-forensics` toggle is a partial, presentation-only answer.

**My answer: there is one reader in three states, not two readers.** The on-call engineer and the
specialist are usually the same person forty minutes apart. The state changes *during a single
sitting*, and it changes in both directions — you go deep on a lead, it dies, you come back up.

That has a hard consequence: **the transition between depths must be continuous, not a mode
toggle.** A mode is chosen once, at the moment the reader knows least about what they need. A
drill-down is chosen continuously, with full knowledge. So:

- Reading depth is expressed as **navigation into a structure**, never as a global mode.
- The default view is the shallowest one, always, for everyone.
- Every shallow element is a **door** to its deeper form; nothing is a dead end.
- Nothing is hidden by depth — it is *not yet requested*. The distinction matters, because a reader
  who cannot verify the summary will not trust the summary.

`reading-mode-*` and `report-density-*` are therefore deleted as *content* mechanisms. Density
survives as a pure comfort preference ([§11.4](#114-type-numerics-and-magnitude)).

### 1.3 The three ways a generated report fails

Design pressure comes from these three, and later sections name which one they attack.

| Failure | What it looks like | Attacked by |
|---|---|---|
| **F1 — Unfounded confidence** | "Leak Likelihood: 78/100", produced by weights nobody can see, over a heap the tool only partly scanned. The reader either over-trusts it or dismisses the whole tool. | Claim graph ([§6](#6-the-claim-graph)), coverage ([§7](#7-coverage-and-negative-space)), split confidence ([§6.4](#64-confidence-is-two-numbers-not-one)) |
| **F2 — Undifferentiated dump** | 68,576 rows under a heading that says "Top". 1,821,559 cells shipped, a few thousand read. Nothing decided what mattered. | Claim-first IA ([§9](#9-information-architecture)), evidence-shape widgets ([§10](#10-evidence-shapes-and-the-widget-vocabulary)), disclosure without capping ([§5.4](#54-full-fidelity-store-progressive-disclosure)) |
| **F3 — Unfalsifiable claims** | "Possible memory leak in `DataColumn`." Nothing states what would disprove it, what evidence was against it, or what to capture next. The reader cannot act, only believe. | Counter-evidence and falsification on every claim ([§6.3](#63-a-claim-carries-its-own-refutation)), next-capture ([§6.5](#65-every-claim-names-its-next-capture)) |

F3 is the one nobody builds for, and it is why generated diagnostics get dismissed by senior
engineers. **A claim that cannot be wrong cannot be trusted.**

### 1.4 What "if you had the data" actually changes

The premise of the question is that data is the constraint. It is not. The constraint is
**structure**. DumpDetective already computes, today, across ~35 analyzers in 9 domains:

| Already computed | Currently surfaced as |
|---|---|
| Exact retained bytes per type, from a real dominator tree | one column in one table |
| A full disk-backed reverse-reference index — parent lookup for any object | internal; drives A5 root paths |
| Per-thread retention attribution (`RootStackThreadAttribution`, `IThreadRetentionProvider`) | **computed and wired to nothing** |
| Root chains carrying *field names* (`RootFieldName`, `LastHopFieldName`) | a string in a table cell |
| Event-subscriber graph — publisher, subscriber types, per-instance detail | 3.9 MB of card JSON, 2,975 instances |
| Thread stack signature clusters | a section |
| GC handle table by kind; weak-reference liveness | two sections |
| Segment/heap topology, LOH/POH fragmentation, segment reservation | four sections |
| Module set, assembly versions, JIT footprint | two sections |
| Async state-machine suspend-state distribution, `async void` detection | a table |
| WCF channel states with remote endpoints, SQL pools, transactions, HTTP objects | seven sections |
| Per-analyzer duration, objects scanned, cache hit/miss, skip reason | an appendix nobody opens |

Every one of those is *a fact about an entity at a time*. The report stores them as *rows inside an
analyzer's section* — which is exactly why the type dossier, the thread dossier, the timeline,
cross-source correlation, and honest trend are each impossible today without new bespoke code.

**So the answer to "what would you do with the data" is: stop filing it under the analyzer that
found it.** That single change is worth more than every other idea in this document, and
[§4](#4-the-entity-model) specifies it.

---

## 2. The six things I'd do differently

Each is stated against what the siblings propose, so the delta is visible.

### T1 — The document is a claim graph, not a section list

*Instead of:* a list of sections, each with a mandatory `lead` object of hand-shaped prose
([ReportFormatCleanSlate §1.1](ReportFormatCleanSlate.md#11-mandatory-lead)).

*This:* the report is a directed graph — `Observation → Signal → Claim → Verdict`, plus `Action` —
and prose is **rendered from** claims, never authored beside them. A lead can drift from its
widget; a claim *is* the widget's reason to exist. The graph is the JSON, the HTML, the Markdown
and the test fixture, so all four agree by construction rather than by review.
([§6](#6-the-claim-graph))

### T2 — Entity-first, analyzer-last

*Instead of:* analyzer sections stay primary, with a type dossier added as a pivot
([ReportFormatCleanSlate §3](ReportFormatCleanSlate.md#3-entities-and-pivot-views)).

*This:* the **entity is the primary index**; the analyzer is provenance metadata. `A1`…`H7` stop
being places and become **saved queries** over the observation store —
`provenance.analyzer = LeakCandidateAnalyzer`. Both views still exist; which one is *primary*
decides what is cheap and what is bespoke, and investigation is entity-shaped.
([§4](#4-the-entity-model), [§9.3](#93-surface-3--dossier))

### T3 — One temporal spine; "trend mode" does not exist

*Instead of:* a single-dump format plus a multi-dump section that replaces `PerDumpDocuments` with
three long tables ([ReportFormatCleanSlate §5](ReportFormatCleanSlate.md#5-multi-dump)).

*This:* a **session** is an ordered set of artifacts on a time axis. One dump is a session with one
instant; five dumps is five instants; a trace is an interval; dump + trace is both. There is no
mode, no `$kind`, no second renderer and no trend document type — the same widgets read a series of
length 1 or length N. ([§3](#3-the-session-model))

### T4 — Uncertainty and coverage are first-class, and separated

*Instead of:* one `Confidence` double on a finding, plus caveats, plus an appendix of analyzer run
statuses.

*This:* **measurement** confidence (was the scan complete, was a capability degraded, was a buffer
truncated) is tracked separately from **inference** confidence (how strongly this evidence supports
this conclusion), because they degrade for different reasons and demand different responses.
Coverage is a published structure, and **"not measured" is a distinct value from zero and from
unknown**, all the way down to the rendered cell.
([§6.4](#64-confidence-is-two-numbers-not-one), [§7](#7-coverage-and-negative-space))

### T5 — The report answers questions it wasn't asked

*Instead of:* an expression filter over tables plus a `dd.query()` escape hatch
([ReportFormatCleanSlate §4](ReportFormatCleanSlate.md#4-interaction)).

*This:* **every view is a query, and the URL is the query.** The rendered narrative is one query
result among many; the filter box, the console API and the deep link are one engine; a shared link
and a test fixture are the same string. This is what makes an uncapped report *navigable* rather
than merely large. ([§12.1](#121-the-query-algebra))

### T6 — Comparison is the unit of judgment

*Neither sibling addresses this.*

No number in this domain is meaningful alone. "8.2 GB managed heap" is unremarkable for a cache
tier and fatal for a request handler. Every headline measure therefore renders **with a
reference** — prior snapshot, per-hour-of-uptime rate, position in the internal distribution, or a
user-declared budget — or with an explicit *"no reference available."* A number without a reference
is an unfinished sentence. ([§8](#8-baselines-and-expectation))

---

# Part II — Content model

> This part is the **logical format spec**. It defines five structures — session, entity,
> observation, claim, coverage — and one policy, baselines. Everything in Part III renders these;
> everything in Part IV serializes them.

## 3. The session model

### 3.1 Structure

```
Session
├─ SessionId, capture window, subject process identity
├─ Artifact[]            one per dump / trace / gcdump / log
│   ├─ ArtifactId, kind, path, hash, size
│   ├─ TimeAnchor        instant or interval (below)
│   ├─ Capability[]      what this artifact can answer
│   └─ IndexStats        objects, types, threads, segments; index build cost
├─ Timeline              the alignment of artifacts, plus alignment confidence
├─ CoverageReport        §7
├─ EntityTable           §4
├─ ObservationStore      §5
└─ ClaimGraph            §6
```

The **subject process identity** is what makes a session coherent: same machine, same process
lifetime, or an explicitly-declared "these are the same service across restarts" (which caps
comparison confidence, because a restart resets uptime and the heap).

### 3.2 Every current and future mode is a degenerate case

| Today | Under the session model | New code paths |
|---|---|---|
| Single dump | 1 artifact, 1 instant | — |
| Multi-dump trend | N artifacts, N instants | none |
| Trace only | 1 artifact, 1 interval | none |
| Dump + trace | 2 artifacts, 1 instant inside 1 interval | none |
| Dump + trace + gcdump | 3 artifacts | none |
| Two dumps from different restarts | 2 artifacts, `restartBoundary` between them | none — confidence caps handle it |

This is the same argument as
[modularity-plan §1](../refactor/modularity-plan.md), applied to the report rather than the
orchestrator. The reason to do it *in the report first* is that the report is where mode explosion
is most visible and most expensive: today `$kind === 'trend'` branches through the renderer,
`TrendReportDocument` carries a parallel `TrendAnalyzerSections` **and** `PerDumpDocuments`, and
roughly 30 `IAnalyzerTrendComparer` implementations exist because there is no generic way to diff
two analyzer results.

### 3.3 The time model — the biggest thing both siblings miss

Neither sibling doc has a time model. A dump is treated as timeless, which is false and costly.

A dump is a **point observation of a process with history**, and it carries far more temporal
information than the report uses:

| Temporal signal in a dump | What it enables |
|---|---|
| Capture timestamp | ordering, wall-clock alignment with traces and logs |
| Process uptime | *rate* normalization — the single highest-value derived quantity ([§8](#8-baselines-and-expectation)) |
| Cumulative GC counts per generation | average collection interval; whether gen2 is being collected at all |
| Finalizer queue depth vs. finalizer thread state | whether the queue is draining or stuck |
| Thread creation order / thread pool growth | thread leak direction from a *single* dump |
| Exception records with timestamps (crash dumps) | the moment of failure relative to the state observed |
| Timer due-times and periods | scheduled work that will fire but has not |
| Async state-machine suspend state distribution | how long work has been parked, in aggregate |

**Consequence:** rate-of-change claims do not require multiple dumps. "This process has allocated
X bytes over Y hours of uptime, and Z bytes of it are in gen2" is available from one artifact, and
it is more useful than the absolute number. A second dump makes rates *measured* rather than
*averaged*, which raises confidence — it does not unlock the category.

```
TimeAnchor
├─ kind:        Instant | Interval
├─ captured:    UTC
├─ uptime:      Duration?           null when unavailable
├─ interval:    (start, end)?        traces only
└─ fidelity:    Exact | Derived | Declared | Unknown
```

`fidelity: Declared` covers the case where the only timestamp is the one in the filename — which is
how the measured baseline's dumps are actually ordered today. That is legitimate, and it must be
*labelled*, because a mis-ordered session silently inverts every trend conclusion.

> **Assumption to verify before building on §3.3:** which of these ClrMD 4 actually exposes, and at
> what cost. Uptime, GC counts and exception records are near-certain; per-thread creation time is
> not. This spec does not assume availability — [§7](#7-coverage-and-negative-space) makes absence
> expressible, so an unavailable signal degrades one claim instead of breaking the model.

### 3.4 Timeline alignment and its confidence

For a multi-artifact session, alignment is a computed result with its own provenance, not an
assumption:

- Same-process, monotonic clocks → `Exact`.
- Filename timestamps only → `Declared`, and the report says so.
- Restart between artifacts → alignment holds for wall clock but **not** for heap continuity;
  heap-delta claims across the boundary are suppressed rather than degraded, because they are not
  less certain, they are meaningless.

Alignment fidelity **caps** the confidence of every claim derived across artifacts. This mirrors
the `MatchFidelity` rule for entity joins ([§4.3](#43-join-keys-and-match-fidelity)) and it is the
same principle: *a derived conclusion cannot be more certain than the weakest join it rests on.*

---

## 4. The entity model

### 4.1 Entity kinds

The report's primary index. Each entity has a stable id within the session and appears exactly
once in the entity table.

| Kind | Identity | Notes |
|---|---|---|
| `type` | canonical type name | `MethodTable` is per-artifact, so it is an *attribute*, not the identity — this is what lets two dumps join |
| `method` | declaring type + signature | needed for traces; harmless for dumps |
| `module` | name + MVID/version | version conflicts are an entity-level fact |
| `thread` | OS id + managed id | OS id is reused across restarts, so identity is `(artifact, osId)` with a session-level join |
| `object` | `(artifact, address)` | **never joins across artifacts** — addresses are not stable. Fidelity `None` |
| `root` | root kind + location (static field, stack frame, handle) | the durable identity that objects lack |
| `handle` | `(artifact, handleAddress)` + kind | |
| `segment` | `(artifact, base)` + generation/kind | |
| `appdomain` | id + friendly name | |
| `stringValue` | the string content itself | makes duplicate-string analysis entity-shaped |
| `external` | scheme + authority | SQL server, HTTP host, WCF endpoint — the boundary the process talks to |

`external` is the entity kind that surprises people and pays off most: it is what lets the report
say *"of the 14 faulted WCF channels, 12 point at one endpoint"* without a bespoke analyzer for
that question.

### 4.2 What this replaces

- The **string pool** ([ReportStringPool.cs](../../src/DumpDetective.Reporting/Formatters/ReportStringPool.cs)),
  which is a compression device with no semantics. The entity table is a *semantic* index that
  happens to deduplicate. The measured baseline's 131,568 unique type names repeated across
  1,022,984 cells (~9.9 MB) collapse as a side effect, not as the goal.
- Type names embedded in **hop chains** — 71,944 hop strings in one report become entity id arrays.
- The per-analyzer, per-section repetition of the same type name across nine sections.

### 4.3 Join keys and match fidelity

Cross-artifact and cross-source joins need keys, and getting them wrong produces confidently wrong
correlations — worse than no correlation.

```
EntityRef
├─ kind, id
├─ joinKey:   canonicalized string used for cross-artifact matching
└─ fidelity:  Exact | High | Low | None
```

| Case | Fidelity | Why |
|---|---|---|
| `System.Data.DataColumn` in two dumps | `Exact` | canonical name is stable |
| Generic instantiation with identical arguments | `Exact` | after canonicalization |
| Compiler-generated closure `<>c__DisplayClass7_0` | `Low` | the ordinal shifts between builds |
| Async state machine `<Foo>d__12` | `Low` | same reason; see the P2-4 regex-drift history |
| Anonymous type | `Low` | structural name, order-dependent |
| `object` at address `0x7ff8…` in two dumps | `None` | joining is a bug, not a low-confidence match |
| Thread by OS id across a restart | `None` | ids are recycled |

**Fidelity caps derived confidence.** A claim joined on `Low`-fidelity entities cannot be rendered
above `Warning`, and its confidence breakdown names the join as the limiting factor. This is not a
nicety: the single fastest way to destroy trust in a diagnostic tool is a confident claim about two
things that were never the same thing.

### 4.4 Attributes vs. observations

An entity carries **only identity and immutable attributes** (name, MVID, kind, OS id). Everything
measured — counts, bytes, generation split, retained size, subscription counts — is an
**observation**, because it varies per artifact and per time. The line matters: if measurements
live on the entity, the entity table becomes per-artifact and the join disappears.

---

## 5. The observation store

### 5.1 The record

Aligned with [../refactor/modularity/observation-and-correlation-model.md](../refactor/modularity/observation-and-correlation-model.md);
restated here because the report is its consumer and depends on the purity rule.

```
Observation
├─ id
├─ type:        open vocabulary — "type.retained-bytes", "thread.wait-state",
│               "gc.generation-composition", "channel.state", "cpu.sample-count"
├─ subjects:    EntityRef[]        one for a per-type fact; two for a relation
├─ when:        TimeAnchor
├─ measures:    { name -> Measure }
├─ provenance:  artifact, analyzer, capabilitiesUsed, fidelity
├─ confidence:  measurement confidence only — 0..1
└─ evidence:    EvidenceRef[]      addresses, index offsets, artifact regions
```

### 5.2 Observations carry no judgment

The litmus test from
[../refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md](../refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md):
*if a field needs a hand-picked constant to compute, it does not belong here.*

| Field | Rule |
|---|---|
| `measures` | raw only. `gen2Ratio = 0.81`, `retainedBytes = 8.0e8`, `subscriberCount = 4211`. Never a weighted composite, never a score normalized against a magic constant |
| `type` | a factual characterization: `gc.generation-composition`, **not** `gc.pressure-high` |
| `confidence` | measurement only — degraded capability, partial scan, truncated buffer. Never severity confidence |
| `subjects` / `when` / `provenance` | facts by construction |

So `MemoryPressureScore`, `GcPressureLevel` banding, `HealthScore`, `SuspicionScore` and
`LeakCandidateRecord.Severity` all move out of analyzers and into synthesis
([§6](#6-the-claim-graph)). This is a report requirement, not just hygiene: **a composite computed
inside an analyzer is frozen at the fidelity that analyzer happened to have**, so it cannot improve
when a second artifact or a trace arrives. Computed in synthesis, the same claim gets stronger for
free.

### 5.3 Measure semantics drive everything downstream

```
Measure { value, unit, semantics }

unit:       Bytes | Count | Milliseconds | Ratio | Fraction | Address | Percent | None
semantics:  Absolute | Rate | Ratio | Duration | Count | Cumulative | Distribution
```

`semantics` is not decoration. It determines, mechanically:

| Consumer | Uses semantics to decide |
|---|---|
| Diffing | absolute delta vs. rate change vs. ratio-point change ("up 12 points" ≠ "up 12%") |
| Aggregation | sum is valid for `Absolute`/`Count`, invalid for `Ratio`, needs weighting for `Fraction` |
| Formatting | `Bytes` → binary magnitudes; `Fraction` → percent; `Cumulative` → show the derived rate too |
| Chart selection | `Distribution` → histogram; `Rate` → line; `Absolute` composition → stacked bar |
| Interpolation | `Cumulative` may be interpolated between snapshots; `Absolute` may not |

**This is what kills the presentation-in-data problem at the root.** The measured baseline ships
259,887 `"No"`, 136,425 `"N/A"`, 66,946 `"Yes"` and 52,975 em-dash cells because C# formats for the
text renderer and every other renderer inherits the artifact. With typed measures there is nothing
to inherit: the value is a number, a bool or null, and each presenter formats it. One canonical
model, N presenters — the rule the current `CompactHeader` already states as intent and the pipeline
does not honor.

### 5.4 Full fidelity store, progressive disclosure

Two rules that are easy to confuse and must not be:

1. **The store keeps everything.** All 68,576 finalizer-queue entries, all 175,158 rows, every
   observation. No top-N, no sampling, no cap. This is settled project direction and this spec does
   not reopen it. The prior report contract required the opposite — *"every traversal, scan and
   ranking must be capped by explicit top-N"* — which is one reason those specs were deleted
   ([README.md](README.md)).
2. **Rendering discloses progressively.** The default view of a widget shows what is needed to
   reach the conclusion; the rest is one interaction away and already in the file.

The difference between those and *capping* is that capping loses data and lies about it. Disclosure
loses nothing: the count is honest, the sort is over the full set, the filter searches the full set,
and export writes the full set. [§10.2](#102-rendering-large-sets-without-capping) specifies how a
widget renders 68,576 rows without capping and without hanging.

### 5.5 Cardinality and where observations live

At the measured scale, per-entity observations across 35 analyzers reach millions of records —
larger than the report should embed. So:

| Tier | What | Where |
|---|---|---|
| **Embedded projection** | every observation referenced by any claim, plus per-entity headline measures for every entity that appears in any view | in the report file, columnar ([§14](#14-envelope-and-segmentation)) |
| **Side-car** | the complete observation store | `report.observations.*` next to the report, referenced by URI; the HTML degrades gracefully when it is absent |
| **Re-derivable** | anything reconstructible from the dump plus the on-disk indices | not stored; the report records the *query* that regenerates it |

Tier 3 is why `AnalyzerArtifact` (filename + instructions) exists today and should be generalized:
some evidence is legitimately too big to ship, and the honest form is a reproducible recipe, not a
truncated sample.

---

## 6. The claim graph

The central structure, and the part I would build first. It attacks all three failure modes in
[§1.3](#13-the-three-ways-a-generated-report-fails).

### 6.1 Node kinds

```
Observation   raw measurement, no judgment                            §5
     │  synthesis rule (id + version)
     ▼
Signal        a thresholded/derived statement about observations
              "gen2 fraction 0.81 exceeds the 0.60 band"
     │  synthesis rule (id + version)
     ▼
Claim         a judged statement with severity, support, refutation   §6.2
     │  ranking rule (id + version)
     ▼
Verdict       the report's single overall position                    §6.6
     │
     ▼
Action        what to do, ranked, with validation steps               §6.7
```

Every edge names the rule that produced it and that rule's version. That is what makes *"why did
this score 72 when last month's scored 61"* answerable, and it generalizes the
`ScoringModelVersion` / `ActionScoringModelVersion` fields that already exist.

`Signal` is a separate node kind rather than an implementation detail because it is the reusable
layer: several claims cite the same signal, trend compares signals across snapshots, and a signal
that fires with no claim attached is a **rule-coverage bug** a test can catch.

### 6.2 The claim record

```
Claim
├─ id, fingerprint                    fingerprint stable across runs — trend identity, dedup
├─ subjects:      EntityRef[]         what this is about
├─ when:          TemporalExtent      instant, interval, or across-session
├─ assertion:     structured          predicate + subject + measure + reference  (§6.8)
├─ severity:      Critical | Warning | Info | OK | Unknown
├─ support:       ObservationId[] + why each supports
├─ counter:       ObservationId[] + why each weakens          ← §6.3
├─ falsifiedBy:   FalsificationTest[]                         ← §6.3
├─ coverage:      CoverageRef[]       what was and wasn't measured  (§7)
├─ confidence:    { measurement, inference, limitingFactors[] }     (§6.4)
├─ nextCapture:   CaptureRecipe[]     how to raise confidence        (§6.5)
├─ derivedFrom:   RuleRef[]           full lineage with rule versions
├─ relations:     supports / contradicts / duplicates / explains other claims
└─ actions:       ActionId[]
```

`assertion` being **structured** rather than a string is the pivot on which the whole design turns.
Prose is generated from it — for HTML, Markdown, text, a CLI one-liner, a Slack message, a commit
comment — so no two renderers can disagree, and a test can assert *the claim*, not a sentence.
This is the concrete difference from the sibling's mandatory `lead`, whose `what` / `abnormal` /
`action` are author-written strings that can drift from their rows.

### 6.3 A claim carries its own refutation

The novel requirement, and non-negotiable. Every claim answers:

- **What argues against this?** Every synthesis rule must enumerate the evidence that *weakens* its
  own conclusion, and populate `counter` when it is present. A leak claim about a type with a huge
  gen2 population must carry, as counter-evidence, the fact that the type is also present in gen0
  in proportion (consistent with churn, not retention), or that the dominant root is a legitimate
  cache with a bounded size.
- **What would prove this wrong?** A concrete, executable test:

```json
{
  "falsifiedBy": [
    { "test": "capture a second dump ≥30 min later; if DataColumn gen2 bytes do not grow, this is a plateau, not a leak",
      "kind": "capture" },
    { "test": "!gcroot on any of the cited addresses returning a Gen0/stack-only root would contradict static retention",
      "kind": "windbg", "command": "!gcroot 0x000001f2a4c81230" },
    { "test": "if DataSet.Clear() is called on the owning DataSet at request end, retention is expected and bounded by pool size",
      "kind": "code-inspection" }
  ]
}
```

Three payoffs, in order of importance:

1. **It converts skeptics.** A senior engineer reading "here is what would disprove me" engages;
   the same engineer reading a bare score dismisses.
2. **It makes rules honest.** A rule author who must write the falsification test discovers when
   they do not actually have a claim — this is the single best quality filter available, and it
   costs nothing at runtime.
3. **It is testable.** A claim with an empty `falsifiedBy` fails CI ([§17](#17-contracts-as-enforceable-gates)).

**Contradictions are output, not bugs.** When two claims contradict — LeakCandidate says
`DataColumn` is retained by a static root, GCRoot says the dominant root is a stack frame — the
report says so, prominently, at reduced confidence for both. Hiding it produces a report that is
wrong half the time and confident always. Surfacing it produces a report that points a human at
exactly the interesting place. Rendering: [§10.1](#101-the-shape-taxonomy) `dd-claim` conflict
state.

### 6.4 Confidence is two numbers, not one

| | Degrades because | Reader's correct response |
|---|---|---|
| **Measurement confidence** | analyzer skipped, capability missing, scan truncated, sampling partial, join fidelity `Low`, alignment `Declared` | *get better data* — re-capture with more of the process, a full dump instead of a mini-dump, a trace |
| **Inference confidence** | heuristic threshold near the boundary, few corroborating signals, known false-positive pattern, counter-evidence present | *use judgment* — the data is fine, the conclusion is a guess |

Collapsing these into one number destroys the distinction the reader needs most, because the two
have opposite remedies. `limitingFactors[]` names the specific cause for each, and the UI surfaces
it inline rather than as a bare percentage
([ReportTemplateCleanSlate](ReportTemplateCleanSlate.md) has the styling primitives for this;
[phase-8-sinks-and-ui.md](../refactor/modularity/phase-8-sinks-and-ui.md) calls it "confidence
transparency").

Both are computed by named, versioned rules over structured inputs. Neither is a magic constant a
reader has to take on faith, and both have a visible breakdown — which is what
`ScoreBreakdown` / `ScoreContributor` already gesture at for the three executive scores and should
generalize to every claim.

### 6.5 Every claim names its next capture

```
CaptureRecipe
├─ what:        "a second full dump, ≥30 minutes later, under comparable load"
├─ why:         "converts an averaged rate into a measured one; would raise
                 inference confidence from 0.55 to ~0.9"
├─ how:         concrete command / procedure
└─ raises:      which confidence dimension, and by roughly how much
```

This closes the loop that makes a diagnostic tool useful over time: a report that ends in "here is
what to collect next, and what it would settle" turns one-shot analysis into an investigation. It
is also the honest response to low measurement confidence — better than a hedged sentence.

### 6.6 The verdict

Exactly one per session. Not a score: a **position**, with a stated basis.

```
Verdict
├─ headline:      structured assertion, rendered as one sentence
├─ severity
├─ basis:         ClaimId[]     the claims that determine it
├─ dissent:       ClaimId[]     claims that point elsewhere       ← honesty
├─ coverage:      overall, with the largest gap named
└─ confidence:    both dimensions, with limiting factors
```

The verdict is derived, so it cannot contradict its claims — a class of bug the current
`ExecutiveSummaryRecord` (three independent 0–100 scores computed separately from the findings
they summarize) is structurally open to.

**Under load it stays honest.** No cap: if 40 claims are Critical, the verdict says *"40 critical
claims across 6 domains; the top three by retained bytes are…"* and groups the rest by mechanism.
It never silently shows 20 of 40.

### 6.7 Actions

`Action` largely exists today (`RankedActionRecord` with `ActionPriorityFactors` and
`ActionConfidenceRecord`) and is the strongest part of the current model. Changes: it hangs off
claims rather than off findings, it inherits their confidence rather than computing its own, its
`Validation` becomes a first-class `CaptureRecipe`, and its `Factors` become a rule with a version
rather than six weights.

### 6.8 The structured assertion

```
Assertion
├─ predicate:  Retains | Grows | Blocks | Leaks | Exceeds | Fragments | Faults |
│              Stalls | Duplicates | Conflicts | Missing | Normal
├─ subject:    EntityRef
├─ measure:    Measure
├─ reference:  Reference?     §8 — prior snapshot, rate, distribution, budget, or null
└─ qualifiers: mechanism, location, scope
```

A closed predicate vocabulary is deliberate. It forces rule authors to say what *kind* of statement
they are making, it makes claims groupable and comparable across domains ("show me everything that
`Grows`"), and it makes prose generation mechanical. `Normal` is in the list because
[§7.2](#72-negative-results-are-published) requires positive statements of absence.

---

## 7. Coverage and negative space

Attacks **F1**. The rule:

> **A report that cannot state its coverage cannot state a negative.**

Today, absence of a finding is indistinguishable from absence of analysis. The appendix has
`AnalyzerRunStatusRecord` with status, duration, findings and a skip reason — the right data, filed
where nobody looks and not linked to any claim.

### 7.1 The coverage record

```
Coverage (per capability × artifact)
├─ capability:  "heap.objects", "heap.generations", "threads.stacks", "handles.table"
├─ status:      Complete | Degraded | Partial | Skipped | Failed | Unavailable
├─ scope:       what was covered — "all 4 heaps, 12.1M objects"
├─ boundary:    where it stopped, and why — "BFS depth limit 20 reached on 312 of 14,003 paths"
├─ cause:       the specific missing capability or the specific error
└─ consequence: which claims are weakened, and which questions cannot be answered at all
```

`boundary` is the field that does not exist today and matters most. "Depth limit 20 reached on 312
paths" tells the reader exactly which conclusions are provisional. A blanket "results may be
incomplete" tells them nothing, and trains them to ignore caveats.

`consequence` inverts the appendix: instead of the reader deducing impact from a status table, the
report states it, and every affected claim links back.

### 7.2 Negative results are published

"We looked for X and did not find it" is information, and it is most of what a healthy report
should contain. It becomes a `Claim` with `severity: OK`, `predicate: Normal`, full support and
coverage — indistinguishable in structure from a critical claim, and equally citable.

Concretely, on a healthy process the report says *"No deadlock cycles among 214 threads; the lock
graph was fully traversed"* rather than omitting the lock section. The reader learns the tool
looked. This directly answers the sibling's `abnormal: null` idea
([ReportFormatCleanSlate §1.1](ReportFormatCleanSlate.md#11-mandatory-lead)) but as a first-class
node instead of a null field, so it can be ranked, cited, trended and tested.

### 7.3 Unknown is a value

Three distinct states, distinct all the way to the pixel:

| State | Data | Rendered | Meaning |
|---|---|---|---|
| Zero | `0` | `0` | measured, and it is zero |
| Unknown | `null` + `reason: not-measured` | `—` with a hoverable reason, muted | we did not measure it |
| Indeterminate | `null` + `reason: ambiguous` | `?` with the ambiguity | we measured and cannot decide |

The measured baseline's 52,975 em-dash cells are a *deliberate* placeholder, which is the right
instinct implemented in the wrong layer — as a presentation string inside the data, indistinguishable
from a real value and unfilterable. Here the reason travels with the null, so the UI can render it,
the filter can select on it (`coverage:unknown`), and a gate can require it.

---

## 8. Baselines and expectation

Implements **T6**. Four reference kinds, in descending strength:

| Reference | Available when | Example rendering |
|---|---|---|
| **Prior snapshot** | session has ≥2 artifacts | `8.2 GB (+1.4 GB / +21% vs. 14:02)` |
| **Rate-normalized** | uptime known — *available from one dump* | `8.2 GB over 9.4 h uptime = 0.87 GB/h` |
| **Internal distribution** | always | `12.4% of managed bytes; rank 1 of 131,568 types; p99.99 by instance count` |
| **Declared budget** | user supplies expectations | `8.2 GB against a 4 GB budget — 205%` |

"Always" in the internal-distribution row holds for **per-entity** measures — there is a natural
population to rank against (types by retained bytes, threads by wait time). It does not hold for
**whole-process scalars** with no intra-dump population — total thread count, total managed heap
bytes, finalizer queue depth as a single number. Those fall back to rate-normalization only if the
measure is cumulative and uptime is known; absent both, a single-dump run can legitimately render
"no reference available" for exactly this class of measure, which no worked example here shows.
See [§21 open question 8](#21-risks-assumptions-and-open-questions).

### 8.1 Declared expectation

The one that needs new input: a small file the user supplies alongside the dump.

```json
{
  "process": "OrderService",
  "expect": {
    "managedHeapBytes":   { "max": "4gb" },
    "gen2Fraction":       { "max": 0.5 },
    "threadCount":        { "max": 200 },
    "finalizerQueue":     { "max": 1000 },
    "types": { "System.Data.DataColumn": { "instances": { "max": 50000 } } }
  }
}
```

Cheap to build, and it changes the report's voice from *"here is a large number"* to *"this exceeds
what you said to expect"* — the difference between a measurement and a verdict. Absent the file,
the report uses the other three reference kinds and says which one it used. It never silently
compares against a built-in constant, because a built-in constant is a guess about someone else's
service.

### 8.2 Leak rate as the flagship derived claim

Given a metric series over a session:

- least-squares fit of bytes vs. time, with **R²** as fit quality
- extrapolation to the declared budget (or to observed process limits), with an error band
- rendered as: *"`DataColumn` retained bytes grow 12.4 MB/h, linear, R² = 0.98; at this rate the
  4 GB budget is exhausted in ~6.2 h (95% band: 4.8–8.9 h)."*

This is the reason to capture multiple dumps at all, and in this model it is not a "trend feature":
it is one synthesis rule over an observation series, so it works at N=2 with low confidence, at N=5
with high confidence, and — with a trace supplying allocation rates — without a second dump at all.
Same rule, graded fidelity.

**Do not report a fit without R², and do not extrapolate without a band.** A confident straight
line through two points is exactly the F1 failure.

---

# Part III — Presentation

> This part is the **design spec**, the **component spec** and the **interaction spec**. It renders
> Part II and nothing else: no view may contain information that is not a node in the content model.

## 9. Information architecture

### 9.1 Four surfaces, increasing depth

The reading-depth model from [§1.2](#12-the-reader--answering-the-question-both-siblings-left-open),
made structural. Depth increases left to right; every element links rightward.

```
VERDICT  ──────►  BOARD  ──────►  DOSSIER  ──────►  EVIDENCE
one screen        all claims      one entity        the store
always shown      ranked          everything        queryable
                  filterable      about it          exportable
```

| Surface | Contains | Never contains | Answers |
|---|---|---|---|
| **Verdict** | the position, its basis, its dissent, coverage headline, top actions, the session timeline | tables, per-type detail, analyzer names | "is something wrong, how urgent" |
| **Board** | every claim as a card — assertion, severity, both confidences, support count, counter count | raw rows | "what does the tool think, and how sure" |
| **Dossier** | one entity: every observation from every analyzer and artifact, its relations, its claims, its series over time | other entities except as links | "what is going on with *this*" |
| **Evidence** | the observation store, full-fidelity, as tables/charts driven by a query | opinions | "show me the actual data" |

The current report has one and a half of these: a header that mixes verdict and board, and 37
analyzer sections that mix dossier and evidence. The **Dossier surface is entirely missing**, and it
is the one investigation actually needs.

### 9.2 Surface 1 — Verdict

One screen, no scrolling for the essentials. Contents, in fixed order:

1. **Provenance strip** — artifact(s), capture time(s), uptime, size, hash, tool version, elapsed.
   First, not last: a reader must know what they are looking at before they read a conclusion.
2. **The verdict sentence**, generated from the structured assertion.
3. **Coverage headline** — *"33 of 36 analyses complete; thread stacks unavailable (mini-dump);
   2 analyses degraded"* — with the largest gap named and linked.
4. **Basis** — the claims that produced the verdict, as compact cards.
5. **Dissent** — claims pointing elsewhere. Present even when empty, stating "none."
6. **Actions** — ranked, each with its validation and its claim link.
7. **Session timeline** — always rendered, even for one artifact, where it is a single tick with
   uptime context. Rendering it unconditionally is what keeps single/trend one renderer, and it
   makes "you only gave me one point in time" visible at a glance.

Anti-requirements, stated because the current header violates each: no scorecard grid of nine
domain tiles the reader must interpret; no three independent 0–100 scores without their
contributors; no prose paragraph summarizing what the sections below contain.

### 9.3 Surface 3 — Dossier

The highest-value new surface. For a `type` entity, everything the existing analyzers already know,
in one place — see [Appendix B](#appendix-b--a-type-dossier-from-todays-analyzers) for the full
worked inventory. Structure:

```
┌ ENTITY HEADER ────────────────────────────────────────────────┐
│ System.Data.DataColumn        type · 3 artifacts · MT 0x7ff8…  │
│ [claims: 3]  [observations: 47]  [relations: 12]               │
├ CLAIMS ───────────────────────────────────────────────────────┤
│ cards, ranked                                                  │
├ MEASURES ─────────────────────────────────────────────────────┤
│ every measure, with reference and series if N>1                │
├ STRUCTURE ────────────────────────────────────────────────────┤
│ retention: dominators, roots, chains, per-thread attribution   │
├ RELATIONS ────────────────────────────────────────────────────┤
│ retains / retained-by / subscribes-to / allocated-by           │
├ COVERAGE ─────────────────────────────────────────────────────┤
│ what was not measured about this entity, and why               │
└────────────────────────────────────────────────────────────────┘
```

Thread and root dossiers are the same layout over different observations — and the thread dossier
finally consumes `RootStackThreadAttribution` / `IThreadRetentionProvider`, which is computed today
and rendered nowhere.

### 9.4 Navigation is five orthogonal axes, all queries

`entity` · `time` · `domain` · `severity` · `provenance`

Any view is a point or region in that space, expressed as a query
([§12.1](#121-the-query-algebra)). The analyzer-section TOC that organizes the report today becomes
one saved query per section — preserved for continuity, demoted from structure to bookmark. Stable
ids (`A1`…`H7`, `SectionIdDomainMap`) survive as query aliases so existing links and postmortems
keep resolving.

### 9.5 Citation and anchoring

Every node has a stable, human-typable address:

```
#claim/leak-datacolumn-static
#entity/type/System.Data.DataColumn
#entity/type/System.Data.DataColumn/retention
#obs/type.retained-bytes@a0/System.Data.DataColumn
#q/type:*DataColumn severity>=warning
```

These go in postmortems, tickets and chat. Requirements: stable across re-renders of the same
artifact; **not** stable across artifacts (a different dump is a different session, and a link that
silently resolves to a different object is worse than a broken one); a broken anchor is a build
failure.

---

## 10. Evidence shapes and the widget vocabulary

### 10.1 The shape taxonomy

A widget exists because a **shape of evidence** exists, not because an analyzer wants a place to
put something. Ten shapes:

| Shape | The question it answers | Widget |
|---|---|---|
| **Scalar** | how much / how many, vs. a reference | `dd-measure` |
| **Composition** | what is it made of | `dd-composition` |
| **Ranking** | which are the biggest | `dd-table` |
| **Distribution** | how is it spread | `dd-distribution` |
| **Correlation** | do these two move together | `dd-scatter` |
| **Sequence** | what happened in what order | `dd-sequence` |
| **Graph** | what points at what | `dd-graph` |
| **Series** | how does it change over time | `dd-series` |
| **Entity** | everything about one thing | `dd-entity` |
| **Assertion** | what does the tool think | `dd-claim` |

Plus three non-widget chrome modules — `shell`, `query`, `nav` — which the sibling template doc
correctly identifies as being mixed into widget files today.

Ten widgets replace the 12 typed slots + 18-case `SectionBlock` union
([AnalyzerDetailSection.cs](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs))
plus their bespoke JS renderers plus the second server-side renderer. Adding an analyzer requires
zero new display code, because a new analyzer emits observations of an existing *shape*.

This is a different basis from the sibling's list
([ReportFormatCleanSlate §2](ReportFormatCleanSlate.md#2-one-renderer-one-closed-widget-vocabulary)),
which renames the eleven types that exist. Deriving from shape rather than from history is what
makes the set closed *and* stable when traces arrive: a CPU flame graph is `Composition` over
`Sequence`, not a twelfth widget.

### 10.2 Rendering large sets without capping

How `dd-table` shows 68,576 rows honestly. This is the mechanism that makes
[§5.4](#54-full-fidelity-store-progressive-disclosure) real rather than aspirational.

| Concern | Mechanism |
|---|---|
| Payload | column store, dictionary-encoded, in a lazily-inflated segment ([§14](#14-envelope-and-segmentation)) |
| Initial render | virtualized viewport — DOM holds ~50 rows, scrollbar reflects 68,576 |
| Sort | over the full column, in a worker, on the packed array — not on DOM nodes |
| Filter | over the full column; the result count is always the true count |
| Aggregate | histogram/percentiles computed from the full column, so the summary is not a summary *of the visible rows* |
| Title | generated: *"All 68,576 finalizer-queue entries, sorted by retained bytes"* — the count is bound to the data and cannot drift |
| Export | full set, CSV/JSON, from the same column store |
| Progressive default | opens showing the rows the claim cites, plus context; "show all" is one click and no fetch |

Nothing here is capped. The reader who wants row 40,000 gets row 40,000.

### 10.3 Component specification format

Every widget is specified and built as a **quad**:

```
widgets/table/table.js       build(spec, ctx) → HTMLElement   no globals, no DOM outside its subtree
widgets/table/table.css      .dd-table + parts                semantic tokens only
widgets/table/table.spec.md  the contract below
widgets/table/fixtures/*.json                                 feeds tests and the gallery
```

Each `.spec.md` must state: data contract; required and optional props; **all seven states**
(loading, empty, unknown, partial, truncated-by-boundary, error, conflict); interactions; keyboard
model; ARIA contract; print behavior; performance envelope (max rows/nodes before degradation, and
what degradation looks like); and what the widget must **not** be used for.

The seven-state requirement is load-bearing. Today `unknown` and `empty` are indistinguishable, and
`truncated-by-boundary` — the BFS-depth-limit case — has no representation at all, so a partial
graph renders exactly like a complete one.

### 10.4 The widgets

**`dd-measure`** — Scalar. A number with unit, magnitude formatting, and its reference
([§8](#8-baselines-and-expectation)). Renders the delta or rate inline. Refuses to render without
either a reference or an explicit "no reference." Copyable exact value. This is the atom the
verdict, the board and the dossier all reuse; today the equivalent (`KeyMetrics`, `MetricBlock`) is
three shapes in two renderers.

**`dd-composition`** — Composition. Stacked bar by default; treemap only when there are >20 parts
*and* the reader has drilled in. Always shows the residual ("other: 3.1%") as a real part, never
drops it. Sums are validated against the whole at build time; a composition that does not sum is a
gate failure, not a rounding note.

**`dd-table`** — Ranking. [§10.2](#102-rendering-large-sets-without-capping). Typed columns from
measure semantics, generated title, per-column stats in the header (min/median/p99/max), row
citation targets, column-level unknown counts.

**`dd-distribution`** — Distribution. Histogram with real axes, log scale allowed on the count axis
and labelled when used, percentile markers, outlier callouts. Replaces the "sparkline with no axis"
pattern, which conveys shape without magnitude and is worse than no chart.

**`dd-scatter`** — Correlation. Two measures over a shared entity set — retained bytes vs. instance
count, gen2 fraction vs. size, subscriber count vs. lifetime. Not present in any form today, and
it is the fastest way to spot the type that is *unlike its peers*, which is what leak-hunting is.

**`dd-sequence`** — Sequence. Ordered steps with per-step attributes: stack frames (with framework
folding and a meta strip), root→object hop chains (with the field names already computed), async
continuation chains, exception inner-chains. One widget for what is currently
`NamedStackTrace` + `StackFrameBlock` + `RootOwnedSubgraph` + `TypeSampleTrace`.

**`dd-graph`** — Graph. Dominator trees, lock-wait graphs, event publisher→subscriber graphs,
reference subgraphs. Collapsible tree by default (today's `TreeWidget`, which is genuinely good and
survives intact); force-directed only where the data is not a tree, and never as the default view.
Must render `truncated-by-boundary` explicitly — the depth-20 BFS limit is a *visible* edge, drawn
differently, not an invisible one.

**`dd-series`** — Series. A measure over the session timeline. Renders identically for N=1 (a
single point with its reference band, honestly showing there is no trend) and N=8. Fit lines with
R² where a leak-rate rule applies. **This is the widget that removes trend-vs-single as a concept.**

**`dd-entity`** — Entity. The dossier body ([§9.3](#93-surface-3--dossier)) and the compact entity
chip used everywhere an entity is mentioned. Absorbs `LeakCandidateCard`, `EventLeakGroupCard`,
`EventLeakInstanceCard` — the consolidation the sibling correctly identifies as load-bearing (6.5 MB
of one measured report is instance cards).

**`dd-claim`** — Assertion. Renders a claim: assertion sentence, severity, both confidences with
limiting factors, support and counter counts, falsification tests, next-capture, lineage drill,
actions. Has a **conflict state** for contradicting claims ([§6.3](#63-a-claim-carries-its-own-refutation)).
Absorbs `LeadFinding`, `ConfidenceBandBlock`, `InterpretationBlock`, `NextStepsBlock`.

Supporting primitives, not widgets: `dd-code` (copyable monospace — paths, SOS commands, queries),
`dd-entity-chip`, `dd-coverage-badge`, `dd-reference` (the `+21% vs 14:02` fragment).

### 10.5 What I would refuse to build

| Refused | Why |
|---|---|
| Pie / donut charts | angle comparison is the worst-performing visual encoding; `dd-composition` does it better in every case |
| Gauges, speedometers, "health meters" | maximal ink for one number, and they imply a calibrated scale that does not exist |
| Treemap as a hero visual | area comparison across non-adjacent rectangles is unreliable; fine as a drill-in for a composition with many parts |
| Sparkline without axis or reference | shape without magnitude; actively misleading on log-distributed data, which heap data always is |
| Word clouds, 3D anything, animated transitions between unrelated states | decoration |
| A "top N" widget concept | capping is not a display mode; see [§5.4](#54-full-fidelity-store-progressive-disclosure) |
| A second renderer for any output format | Markdown/text render the same claim graph; drift is guaranteed otherwise |
| Free-text prose authored beside data | every sentence is generated from a claim, or it does not ship |

---

## 11. Visual design specification

### 11.1 Principle

**An instrument panel, not a dashboard.** The reference points are a logic analyzer, a flight
data display and a well-set financial table: dense, aligned, monochrome until something *means*
something. Color is a data encoding and is spent only on severity and categorical series.

Four rules, in priority order when they conflict:

1. **Alignment before decoration.** Digits align, units align, columns align. In a document whose
   content is columns of byte counts, misalignment is a legibility bug.
2. **Density is a feature.** The reader is scanning for an outlier among thousands. Whitespace that
   pushes the fourth row below the fold costs more than it gains.
3. **Color means something or is absent.** No colored headers, no brand gradients, no severity-tinted
   backgrounds on non-severity content.
4. **Every visual difference encodes a real difference.** If two things look different, a reader
   will infer they *are* different. The measured baseline's 954 color literals guarantee accidental
   differences.

### 11.2 Tokens

Three layers, one rule — literals only in primitives. The sibling
[ReportTemplateCleanSlate §2](ReportTemplateCleanSlate.md#2-design-system) specifies this correctly
and in detail; it is adopted here unchanged rather than restated. The additions this spec requires:

```css
--dd-severity-unknown-*      /* the missing sixth step — §7.3 */
--dd-coverage-partial-*      /* boundary/truncation encoding */
--dd-reference-better-*      /* direction of change, distinct from severity */
--dd-reference-worse-*
--dd-confidence-*            /* a scale, not a color */
```

Direction-of-change must be encoded separately from severity: a *decrease* in retained bytes is
"better" and never green-as-in-OK, because in a trend view green-for-improvement next to
green-for-healthy is unreadable.

### 11.3 Severity and confidence

Severity: six steps — `critical`, `warning`, `info`, `ok`, `unknown`, `neutral`. `unknown` is not
optional ([§7.3](#73-unknown-is-a-value)). Each is a triple (`bg`, `border`, `fg`), contrast ≥ 4.5:1
in both schemes, verified by test.

Triple encoding, always: **shape + label + color**, so severity survives grayscale printing,
projector washout and color vision deficiency. Severity is a data attribute
(`data-severity="critical"`), never a composed class name — which keeps the stylesheet statically
analysable, per the sibling's §2.3.

Confidence is rendered as a **discrete scale with its limiting factor named**, not a percentage bar.
"0.62" tells the reader nothing; "Medium — thread stacks unavailable" tells them what to do.

### 11.4 Type, numerics and magnitude

- One UI stack, one mono stack, six-step size scale.
- `font-variant-numeric: tabular-nums` on **every** numeric cell, metric and axis label.
- Magnitude rule: display uses binary magnitudes to 3 significant figures (`1.44 GB`); the exact
  value is always available on hover and on copy. Never round in the data — only in the display.
- Units are rendered in a distinct weight and column-aligned so a column of `GB`/`MB` scans.
- Address formatting is one canonical form (`0x` + lowercase hex, no leading-zero padding), because
  addresses get pasted into WinDbg.
- Density (comfortable/compact) is a token override and a *comfort* preference only. It never
  changes what is shown — that is the [§1.2](#12-the-reader--answering-the-question-both-siblings-left-open)
  rule. Note the current `report-density-*` classes own 25 CSS rules and are set by no JavaScript at
  all.

### 11.5 Color scheme

`color-scheme: light dark` with every semantic token defined once via `light-dark()`. Dark mode then
costs zero duplicated rule blocks — versus the current stylesheet, which has **no**
`prefers-color-scheme` rule and 954 literals that would each need a second home.

**Design dark-first, ship system-default.** These reports are read on incident bridges at 3 a.m.;
a palette that only works in light and is then inverted looks like an inversion. Designing dark
first and deriving light produces a better dark rendering and an equally good light one.

### 11.6 Charts

- Zero baselines on bar charts, always. Truncated axes on a length encoding are a lie.
- Log scale permitted on count axes (heap data is log-distributed), **always labelled**, never on a
  length encoding.
- Direct labelling in preference to legends; legends only when >4 series.
- One categorical palette, contrast-validated in both schemes, ordered so the first three are
  distinguishable under the common color vision deficiencies.
- Every axis carries units. Every chart is keyboard-navigable and exposes a table equivalent — the
  table *is* the accessible version, and it is the same data, not a summary.
- No chart without a `dd-measure` stating the headline number. The chart shows shape; the number
  states magnitude.
- **Hand-built inline SVG only; no external chart library.** The report must open from `file://`
  with no network and no dependency it did not ship. This is a constraint the current renderer
  already honors and the one piece of its chart design worth carrying forward verbatim — every
  chart primitive here is buildable in SVG, and a library would trade self-containment for
  convenience.
- Chart colors reference tokens, never literals, so dark mode and high contrast work without a
  second code path in the chart builders.

### 11.7 Motion, print, layout

- Motion only where it preserves object constancy (expand/collapse, sort reorder). Never for
  entrance or emphasis. `prefers-reduced-motion` handled at the token layer
  (`--dd-motion-duration: 0ms`), not per-rule.
- **Print is a first-class output**, because these get pasted into postmortems: sections
  auto-expand, chrome drops, page breaks per domain, links footnoted with their targets, provenance
  and coverage on page 1, claim fingerprints printed so a printed page is still citable.
  **"Auto-expand" is bounded, and deliberately not the same operation as "show all."** It expands
  every claim and the evidence rows that claim cites — the rows already selected in
  [§5.4](#54-full-fidelity-store-progressive-disclosure)'s progressive default — never a full
  68,576-row evidence table, which would mean materializing every row into the DOM at once and
  defeats the entire virtualization contract in [§10.2](#102-rendering-large-sets-without-capping)
  and the browser-heap budget in [§16.2](#162-delivery-side). A reader who wants the full table on
  paper uses the explicit CSV/JSON export ([§12.3](#123-selection-diff-and-handoff)), not the print
  button — the same distinction [§5.4](#54-full-fidelity-store-progressive-disclosure) draws between
  disclosure and capping applies here: nothing is capped, but print discloses what a claim needs, not
  everything that exists.
- Layout: one grid; three named breakpoints (`--bp-compact/regular/wide`) against 13 today; widgets
  use container queries because a widget belongs to its container's width, not the viewport's.
- Focus management on lazy inflate: expanding a segment moves focus to its heading.

---

## 12. Interaction specification

### 12.1 The query algebra

Every view is a query; the URL is the query. One engine serves the filter box, the console API, the
deep link, the saved section bookmark and the test fixture.

**This is the target shape, not the v1 scope.** Per [§21 open question 2](#21-risks-assumptions-and-open-questions),
this is the most speculative piece of the whole spec, by the doc's own admission — nobody has typed
a query into this report yet, because the report doesn't exist yet. What ships first is the
defensible smaller version named there: structured filter chips plus `dd.query()` over a plain
predicate object, with the URL carrying that object as JSON. The full grammar below is the direction
that design should be able to grow into without a rewrite — it is not something to build against
zero usage evidence. Build the parser only once real usage shows readers hitting the filter chips'
ceiling.

```ebnf
query      = clause { WS clause } [ pipeline ] ;
clause     = entity | measure | temporal | provenance | coverage | claim | group ;

entity     = "type:" pattern | "thread:" pattern | "module:" pattern
           | "root:" rootkind | "endpoint:" pattern ;
measure    = name op value ;                  (* bytes>10mb  gen2>0.8  subscribers>=100 *)
temporal   = "at:" snapshot | "between:" s ".." s | "growing" | "stable" | "new" | "resolved" ;
provenance = "analyzer:" name | "artifact:" id | "capability:" name ;
coverage   = "coverage:" ( "complete" | "partial" | "unknown" | "skipped" ) ;
claim      = "severity" op sev | "cited-by:" claimid | "confidence" op num
           | "contradicted" | "falsifiable" ;
group      = "(" query ")" | query "or" query | "not" clause ;
pipeline   = "|" verb { "|" verb } ;
verb       = "sort" field [dir] | "group" field | "limit" n | "as" widget ;
```

Worked examples:

```
type:*Channel bytes>10mb gen2>0.8 root:static
thread:* coverage:partial | group waitReason
type:* growing between:0..4 | sort rate desc | as series
severity>=warning contradicted
analyzer:EventLeakAnalyzer confidence<0.6 | as claim
cited-by:leak-datacolumn-static | as table
```

Notes:

- `limit` exists in the *pipeline* — it is a user's explicit request for fewer rows, which is the
  only legitimate cap. Producers may never emit one.
- `as` selects the widget, which is how the same query renders as a table, a series or a set of
  claim cards. The widget is a view of the result, not a property of the data.
- `contradicted` and `falsifiable` are queryable because [§6.3](#63-a-claim-carries-its-own-refutation)
  makes them structural. "Show me everything the tool is unsure about" is one query.

`dd.query(...)` in the console returns the same result object the UI renders, so a power user is
never fighting the UI's ceiling — the escape hatch and the product are the same thing.

### 12.2 Drill-down invariants

Enforced by test, not by convention:

1. Every rendered number resolves to its observations.
2. Every observation resolves to its artifact, analyzer, capabilities used and evidence refs.
3. Every claim resolves to its support, its counter-evidence, its rules with versions, and its
   coverage.
4. Every entity mention resolves to its dossier.
5. Every claim exposes "what would disprove this."
6. The inverse holds: a row shows which claims cite it; an observation shows which claims it
   supports *and* which it weakens.

Invariant 6 is what makes the evidence surface useful rather than a data dump — a reader scrolling
raw rows can see which ones the tool actually reasoned from.

### 12.3 Selection, diff and handoff

- **Selection is global state.** Selecting an entity anywhere highlights it everywhere — in a
  series, a graph, a table, a claim. This is what turns four surfaces into one workspace.
- **Diff** is a query with two snapshot anchors rendered by the same widgets. Not a mode. Works for
  any pair in a session, including a dump against a trace interval when both carry the measure.
- **SOS/WinDbg handoff** per claim: the exact `!dumpheap -mt … / !gcroot … / !do …` sequence that
  reproduces the claim, copyable in one action, with the addresses from the claim's evidence. This
  converts skeptics faster than any chart, and it is also the falsification test in executable form.
- **Export**: current query → CSV/JSON; whole report → `report.json`; a claim → Markdown with
  citations, ready to paste into a postmortem.
- **Copy-to-share is redacted by default.** Anything copied for pasting elsewhere — a claim, a
  summary, a link — omits the dump file path and any host/account identifiers carried in
  provenance. The full-fidelity export is a separate, explicit action. The report is routinely
  pasted into tickets and chat, so the low-friction path must be the safe one.
- Rejected: importing WinDbg output back into the report. It sounds symmetrical, it needs a parser
  per command per runtime version, and the value is small next to the maintenance.

### 12.4 Keyboard and events

Complete keyboard model, documented in one place and implemented in one module:

| Key | Action |
|---|---|
| `/` | focus query input |
| `j` / `k` | next / previous item in the current list |
| `Enter` | open the focused item one level deeper |
| `Escape` | back up one level |
| `g` then `v` / `b` / `d` / `e` | go to Verdict / Board / Dossier / Evidence |
| `?` | keyboard help |
| `y` | copy the current view's link (which is the current query) |

Implementation: one delegated listener per event type at the shell root, dispatching on
`data-action`. Lazily-inflated content needs no re-binding — the current model re-wires after every
dynamic insert, which is precisely where interaction bugs come from. Today the keyboard model is
asserted by string-matching JavaScript source inside the bundle.

---

# Part IV — Wire format and delivery

> This part is the **physical format spec**. Part II defines the structures; this defines how they
> are serialized, segmented, versioned and budgeted.

## 13. The artifact set

| Artifact | Always? | Contents |
|---|---|---|
| `report.json` | **yes** | the canonical, versioned, complete document |
| `report.html` | on request | the viewer + an embedded copy of `report.json` (segmented) |
| `report.observations.*` | when the store exceeds the embed budget | the full observation store, side-car |
| `report.md`, `report.txt` | on request | renderings of the same claim graph |

Two rules that eliminate whole classes of bug:

1. **`report.json` is written unconditionally**, every run, regardless of `--format`. It is the
   thing every future UI, script and regression test can rely on existing. (Same conclusion as
   [phase-8-sinks-and-ui.md](../refactor/modularity/phase-8-sinks-and-ui.md), reached from the
   report side.)
2. **HTML is a packaging of the JSON plus a viewer. No content exists in the HTML that is not in the
   JSON.** This kills the two-renderer problem permanently — there is no server-side
   `ReportHtmlShared` to keep visually in sync, no pre-render path, and HTML correctness is testable
   by asserting on the JSON. Markdown and text are renderings of the same graph, so the em-dashes
   and `"N/A"` strings that leaked from the text formatter into the HTML data cannot recur.

Formatters become **sinks** — "do something with this report" rather than "turn this into a
string" — so a file writer, a websocket and an HTTP export are peers.

## 14. Envelope and segmentation

### 14.1 Shell + segments

```
report.html
├─ <style>  tokens + widgets                            ≤ 20 KB min
├─ <script> viewer bundle, one ESM→IIFE build           ≤ 80 KB min
└─ <script type="application/json" id="dd-manifest">    segment table with ids, kinds, sizes
   <script id="dd-seg-core">      base64(gzip(...))     verdict, claims, coverage, timeline, actions
   <script id="dd-seg-entities">  base64(gzip(...))     entity table
   <script id="dd-seg-obs-head">  base64(gzip(...))     headline measures per entity
   <script id="dd-seg-t-{id}">    base64(gzip(...))     one per large table
   <script id="dd-seg-e-{id}">    base64(gzip(...))     one per heavy entity payload
```

`DecompressionStream` already decodes the single blob today; this is the same call per segment. The
manifest ships sizes so the UI can tell the reader what it is about to inflate — a 40 MB segment
should announce itself, not freeze the tab.

Segment boundaries are **derived from the editorial structure**, not chosen for compression: `core`
is what the verdict and board need; everything a claim cites is in `core` or in a segment named by
that claim. This is the [§5.4](#54-full-fidelity-store-progressive-disclosure) disclosure decision
made concrete, and it is why the container falls out of the content model rather than the reverse.

### 14.2 Column store

Tables and observation projections are column-major, dictionary-encoded:

```json
{
  "id": "t-b6-finalizer",
  "rows": 68576,
  "cols": [
    { "name": "Type", "kind": "entity", "entityKind": "type",
      "values": [0, 0, 14, 22, 14, ...] },
    { "name": "Retained Bytes", "kind": "measure",
      "unit": "Bytes", "semantics": "Absolute",
      "values": [8123904, 512, 40960, ...],
      "stats": { "min": 24, "p50": 512, "p99": 1048576, "max": 8123904, "sum": 1846290944 } },
    { "name": "Gen", "kind": "enum", "dict": ["gen0","gen1","gen2","loh","poh"],
      "values": [2, 2, 0, 3, ...] },
    { "name": "Has Finalizer Root", "kind": "bool",
      "values": [1, 1, 0, null, ...], "nulls": { "3": "not-measured" } }
  ]
}
```

Properties this buys, all of which the row-major format blocks:

- group-by, histogram, percentile and sort computed client-side over the full column, in a worker
- entity columns are ids, so the 131,568 repeated type names cost one dictionary
- `stats` precomputed at build time, so a header shows distribution without inflating the segment
- nulls are null and carry a reason ([§7.3](#73-unknown-is-a-value)); booleans are booleans
- per-column typing removes the whole "string cell in a number column" class of defect

`nulls` maps row index → reason only for rows whose reason differs from the column default; a column
that is entirely unmeasured states it once.

### 14.3 Encoding policy

| Payload size | Encoding | Why |
|---|---|---|
| below the existing plain-JSON threshold | plain JSON, uncompressed | small reports stay greppable — a genuinely good property of the current renderer, retained |
| above it | JSON arrays, gzipped, base64 | ~2× larger than binary, but debuggable and dependency-free |
| very large numeric columns | typed-array base64 (binary) | only where measured to matter, per column, flagged in the manifest |

Binary is a per-column decision, not a per-report mode, so greppability degrades exactly where the
data was never readable anyway.

## 15. Schema and versioning

**Version the data contract; never version the look.** The renderer ships inside the file with its
payload, so an old payload is never fed to a new viewer — the problem style versions solve does not
exist for self-contained artifacts. `ReportStyleVersion` therefore has no successor here.

| Thing | Versioned | Kind |
|---|---|---|
| Session report schema | yes — `schemaVersion` | consumer contract: JSON exports, cross-run trend, golden baselines |
| Synthesis rules | yes — each rule has `id` + `version` | provenance: explains why a claim changed between tool versions |
| Observation type vocabulary | open, with a registry | new analyzers add types without a schema break |
| Widget kinds | **closed** | a new widget is a breaking change and should be |
| Severity, predicates, measure semantics | **closed** | consumers switch on these |
| Presentation | **not versioned** | see above |

Compatibility rules: additive fields only within a major; unknown fields preserved on round-trip;
unknown observation types render generically rather than failing; unknown widget kinds are a hard
error, because silently dropping a widget hides data.

**Determinism requirement:** the same artifact analyzed by the same tool version must produce a
byte-identical `report.json`, timestamps excluded. Without it, golden diffing is impossible and
every regression review becomes manual. This constrains implementation — stable ordering
everywhere, no hash-set iteration order in output, fixed float formatting.

## 16. Performance and scale contract

### 16.1 Generation side

Unchanged from the project's core philosophy, restated because the report is where it usually
leaks: stream, never materialize the heap, no full graphs in memory, disk-backed indices, bounded
memory. The report builder is subject to the same rules as an analyzer — it may not `.ToList()` an
observation stream to sort it, and it may not hold the column store in memory to write it.

### 16.2 Delivery side

**These are pre-build targets, not a measured contract.** Every number below is a design goal set
before the segmented envelope, column store or virtualized table exist — none of it has been spiked
against a real payload yet. That's consistent with how this project treats performance claims
elsewhere (design numbers get measured before they're load-bearing, not asserted and trusted); it
should hold here too. Before the wire format in [§14](#14-envelope-and-segmentation) is locked, run
a cheap spike against the existing Appendix A fixture — build the segmented envelope for that one
real payload and measure time-to-verdict and browser heap directly — rather than committing the
format to numbers nobody has checked. If the spike misses a target, that's the format's problem to
solve before the rest of Part IV is built on top of it, not a target to quietly loosen later.

Budgets, gated in CI once validated, measured against the Appendix A fixture (3.35 GB dump →
175,158 rows, 131,568 unique type names):

| Gate | Target | Today |
|---|---|---|
| Time to verdict (first meaningful paint) | ≤ 150 ms | ~30.9 MB `JSON.parse` on every open |
| App shell (HTML + CSS + JS, excl. segments) | ≤ 120 KB | 457 KB (154 CSS + 303 JS) |
| `core` segment, inflated | ≤ 2 MB | n/a — one 30.9 MB blob |
| Total payload, inflated | ≤ 8 MB | 30.9 MB |
| Browser heap after full drill-down | ≤ 250 MB | unmeasured |
| Largest table render (68,576 rows) | ≤ 100 ms to first row, virtualized | 20 rows rendered of 68,576 shipped |
| Sort/filter over a full column | ≤ 200 ms, off main thread | n/a |
| CSS, minified | ≤ 20 KB | 154 KB |
| JS, minified | ≤ 80 KB | 303 KB unminified |

The synthetic-fixture trap is worth naming: a generated fixture will not reproduce 131,568 distinct
type names or the long-tail size distribution, and both dominate real behavior. The golden fixture
must be built from a real payload — and per the project's testing rule, any test that loads a real
dump runs one at a time, in the foreground.

---

# Part V — Quality, scope and honesty

## 17. Contracts as enforceable gates

Every contract in this document is a build-time check or it is decoration. Grouped by what they
protect.

### 17.1 Editorial gates

| Gate | Threshold |
|---|---|
| Claim without a structured `assertion` | 0 |
| Claim with an empty `falsifiedBy` array | 0 — CI-mechanical, presence only; see below |
| Claim with severity ≥ Warning and no `counter` field (present, possibly empty with a stated reason) | 0 |
| Claim without `coverage` | 0 |
| Claim whose `derivedFrom` rules are not in the registry | 0 |
| Number rendered anywhere without a resolvable observation | 0 |
| Hand-written table title | 0 |
| Headline measure without a reference or an explicit "no reference" | 0 |
| Signal that fires with no claim consuming it | 0 (rule-coverage bug) |
| Verdict contradicting its own basis claims | 0 |
| Unresolved anchor | 0 |

**A gate on presence is not a gate on quality, and `falsifiedBy` is the row where that distinction
matters most.** CI can only check that the array is non-empty; it cannot check that the test inside
it is real rather than a boilerplate placeholder ("capture another dump") pasted across every claim
a rule author wrote that afternoon. A hard mandatory field with only a mechanical check is a
Goodhart's-law generator — the fastest way to satisfy it is to defeat its purpose. So: the CI gate
above stays (schema-level, mandatory, cheap), but it is backstopped by the claim-quality corpus
([§17.5](#175-testing-whether-the-report-is-right-not-just-well-formed)) and by periodic human
review that specifically looks for `falsifiedBy` text repeated near-verbatim across many claims —
that repetition is the tell that the field was filled to pass the gate, not to state a real
falsification condition. Do not treat "the gate is green" as evidence the claims are trustworthy;
treat it as evidence they are well-formed, which is all any gate in this section can prove.

### 17.2 Data gates

| Gate | Threshold |
|---|---|
| Presentation string in data (`"Yes"`, `"No"`, `"N/A"`, `—`) | 0 |
| Measure without unit or semantics | 0 |
| String cell in a `number` / `bool` column | 0 |
| Null without a reason | 0 |
| Duplicated entity name across the payload | 0 |
| Observation carrying a weighted composite or a judgment-typed name | 0 |
| Cross-artifact join at fidelity `None` | 0 |
| Claim rendered above its fidelity/alignment cap | 0 |

### 17.3 Design gates

Adopted from [ReportTemplateCleanSlate §9](ReportTemplateCleanSlate.md#9-budgets) — color literals
outside primitives 0, `!important` 0, breakpoints 3, selector depth ≤ 3, duplicate top-level
declarations 0, `innerHTML` 0, unreferenced classes 0 — plus:

| Gate | Threshold |
|---|---|
| Widget without all seven states in its fixture set | 0 |
| Severity encoded by color alone | 0 |
| Numeric cell without `tabular-nums` | 0 |
| Contrast < 4.5:1 for any severity pair in either scheme | 0 |
| Chart without units on every axis | 0 |
| Truncated axis on a length encoding | 0 |

### 17.4 The test pyramid

| Layer | What |
|---|---|
| Rule unit tests | given observations, does the rule produce the expected signal/claim, including the counter-evidence and the falsification test |
| Widget fixture tests | render each widget from each of its seven state fixtures; assert DOM shape, ARIA, data attributes |
| Gallery visual | `gallery.html` snapshots per mode combination (light/dark × density × contrast) |
| Schema conformance | generated reports validate against the published schema |
| Determinism | same artifact twice → byte-identical JSON |
| Golden end-to-end | one real-payload report, asserted structurally, never by source-string matching |
| **Claim quality harness** | below |

Explicitly replaced: the current assertions that string-match JavaScript source inside the bundle
(`"main.appendChild(actionQueue);"`, `"if (ev.key === 'ArrowLeft'"`). Those break on any refactor
and guarantee nothing about behavior.

### 17.5 Testing whether the report is *right*, not just well-formed

Every gate above checks well-formedness. None checks truth, and truth is the actual product. The
missing piece, and the one I would insist on:

**A claim-quality corpus.** A set of dumps with *known* ground truth — a deliberately leaked
`static List<T>`, a real deadlock, a genuine finalizer stall, and importantly several **healthy**
processes. For each, a checked-in expectation file:

```json
{ "artifact": "known-static-leak-01.dmp",
  "mustClaim":    [ { "predicate": "Retains", "subject": "type:LeakedItem", "minSeverity": "Warning" } ],
  "mustNotClaim": [ { "predicate": "Blocks", "subject": "thread:*" } ],
  "mustCover":    [ "heap.objects", "threads.stacks" ] }
```

This gives precision and recall numbers per rule, tracked over time — the only way to know whether a
rule change made the tool better or merely different. The healthy-process cases matter most: a tool
that finds a leak in every process is a random number generator with good typography.

The existing discrepancy-test corpus is the natural home. Per project rule, these run one at a time.

## 18. Anti-goals

Named because each is a plausible next step that would undo something above.

| Not building | Why |
|---|---|
| Persona / reading modes | [§1.2](#12-the-reader--answering-the-question-both-siblings-left-open) — the reader changes state mid-session; depth is navigation |
| A presentation version enum | [§15](#15-schema-and-versioning) — self-contained artifacts do not need one |
| A server-side pre-render path | a second renderer obliged to stay visually identical, drifting on the first one-sided fix |
| Per-analyzer bespoke display code | [§10.1](#101-the-shape-taxonomy) — a new analyzer emits an existing shape |
| Capped or sampled tables | settled project direction; [§10.2](#102-rendering-large-sets-without-capping) makes caps unnecessary |
| A score without a visible breakdown | F1 |
| Prose not derived from a claim | it drifts, silently, and there is no test that catches it |
| LLM-written narrative over the data | it produces exactly the confident, unfalsifiable prose [§1.3](#13-the-three-ways-a-generated-report-fails) exists to prevent. Generating *from* the claim graph is fine; generating *instead of* it is not |
| A server, live query, or a hosted UI | a real ask, a different product; the file must stay openable from `file://` with no network |
| Alerting, notifications, dashboards over many reports | a fleet product, not a report |
| Importing WinDbg output | [§12.3](#123-selection-diff-and-handoff) |

## 19. Minimum viable path

The whole document is a multi-quarter program and should not be executed as one. These five steps
carry most of the value, each is independently useful, each is reader-visible, and none requires the
platform refactor to complete first.

| Step | Scope | Reader-visible effect | Depends on |
|---|---|---|---|
| **M1 — Claims** | `Claim` record + rule registry + generated prose, over today's domain results. No new UI beyond `dd-claim`. Findings become claims; `falsifiedBy` and `counter` required from day one | every conclusion states its support, its refutation and what would disprove it | nothing |
| **M2 — Entities** | entity table replacing the string pool; type + thread dossier | investigate by type and thread instead of by analyzer; ~9.9 MB of duplicate names disappears as a side effect; per-thread retention finally surfaces | nothing |
| **M3 — Coverage** | `CoverageRecord` promoted out of the appendix; `unknown` as a real value with a reason; negative claims published | "we looked and did not find it" becomes visible; caveats become specific | M1 |
| **M4 — Payload** | segmented envelope + column store + virtualized tables | opens instantly; full 68,576 rows genuinely navigable | nothing |
| **M5 — Query** | filter chips + `dd.query()` over a plain predicate object, URL = query (see [§12.1](#121-the-query-algebra) — the full grammar is a later growth target, not this milestone) | uncapped data becomes navigable; links are shareable and testable | M2, M4 |

After M1–M5, the rest is mostly *consequence*: trend is a series over observations
([§3](#3-the-session-model)), diff is a two-anchor query, and a trace source adds artifacts to a
session without touching the report.

**A bound on the M1 adapter, stated up front because [§21 open question 1](#21-risks-assumptions-and-open-questions)
names the risk without resolving it.** M1 builds `Claim` over today's `AnalyzerDomainResult`, before
Phase 5's observations exist to back it — that's the right call, but only if the adapter stays an
adapter. Concretely: the M1 adapter may map an existing domain result's already-computed fields
(severity, the finding text, whatever confidence exists today) onto a `Claim`'s shape. It may **not**
grow new judgment of its own — no new thresholds, no new banding, no new weighting invented inside
the adapter to make a `Claim` look more complete than the domain result underneath it actually
supports. The moment a `falsifiedBy` test or a `counter` entry requires reasoning the domain result
doesn't already carry, that reasoning belongs in a Phase 5 synthesis rule, not in the adapter — an
adapter that starts accumulating its own judgment is exactly the fifth judgment location
[modularity-plan.md § 4a](../refactor/modularity-plan.md#4a-relationship-to-the-analyzer-pipeline--leadfinding-audit)
already spent effort avoiding. If a claim can't be honestly built from what a domain result already
computes, it waits for Phase 5 rather than getting a shortcut.

Mapping to existing plans, so this does not become a third parallel roadmap:

| This | Sibling / platform equivalent |
|---|---|
| M1 | ReportFormatCleanSlate **P1** (leads) — same slot, stronger object |
| M2 | ReportFormatCleanSlate **P3** (entities) |
| M3 | no equivalent — new |
| M4 | ReportFormatCleanSlate **P5** (segmented envelope) |
| M5 | ReportFormatCleanSlate **P7** (filter/console) |
| §11 tokens, §17.3 | ReportTemplateCleanSlate **T1–T5**, adopted wholesale |
| §3 sessions, §5 observations | modularity Phases 1, 5 — this spec is the consumer that justifies them |

ReportFormatCleanSlate's **P0** (turn on the string pool and the row converter) and
ReportTemplateCleanSlate's **T0** (real bundler, kill the `formatBytes` collision) remain worth
doing immediately and independently of everything here.

## 20. Where this disagrees with the sibling docs

| Topic | Siblings | This spec | Why |
|---|---|---|---|
| Editorial unit | `lead` object of `what` / `abnormal` / `action` prose per widget | structured `Claim` with support, counter-evidence, falsification, coverage, dual confidence | prose can drift from data; a structured claim is consumed identically by HTML, JSON, Markdown and tests |
| Primary index | analyzer sections, with entity dossiers as a pivot | entity primary, analyzer sections as saved queries | what is primary decides what is cheap; investigation is entity-shaped |
| Multi-dump | a distinct §5 with its own metric store and trend-only widgets | no trend mode; a session with N time anchors and one widget set | removes the mode axis before it acquires a third value (traces) |
| Time | absent | a temporal spine, including per-dump temporal signals like uptime and cumulative GC counts | rate is more useful than magnitude, and available from one dump |
| Confidence | one number plus caveats | measurement and inference tracked separately, with named limiting factors | they degrade for different reasons and have opposite remedies |
| Coverage | analyzer status appendix | first-class, linked from every claim, with an explicit `boundary` | a report that cannot state coverage cannot state a negative |
| Widget set | 11 widgets derived from the 11 types that exist | 10 widgets derived from 10 evidence shapes | shape-derived sets stay closed when new sources arrive |
| Interaction | filter box + console API as separate features | one query engine; URL = query = fixture | collapses four features into one and makes views testable |
| Reading modes | shipped as presentation modes; content question left open | deleted as a content mechanism | the reader changes state mid-session |

**Adopted from the siblings unchanged**, and worth restating so this is not read as a replacement:
the token architecture and `light-dark()` approach; cascade layers; BEM + data attributes; one file
per component; the gallery; the deletion inventory (`PreRender`, `ReportHtmlShared`, style v1/v2,
the bundle fallback, orphan files); the real bundler; keeping `TreeWidget`, finding fingerprints,
gzip+base64 transport, the plain-JSON threshold, stable section ids, and `CompactHeader`'s typed
metadata.

## 21. Risks, assumptions and open questions

**Assumptions I have not verified and that should be checked before building on them:**

1. Which temporal signals ClrMD 4 actually exposes cheaply — process uptime, cumulative per-generation
   GC counts, exception timestamps, thread creation order ([§3.3](#33-the-time-model--the-biggest-thing-both-siblings-miss)).
   Uptime and GC counts are near-certain; thread creation time is not. The model degrades rather than
   breaks when one is absent, but the leak-rate flagship in [§8.2](#82-leak-rate-as-the-flagship-derived-claim)
   depends on uptime.
2. That the observation store for a 25 GB dump fits the tiering in [§5.5](#55-cardinality-and-where-observations-live)
   without a new index. Unmeasured. Cheapest sufficient check: count observations a handful of
   analyzers would emit on an existing indexed dump, extrapolate; do not build a 25 GB A/B for this.
3. That claim authors will write good falsification tests. Untested culturally, and it is the
   single biggest execution risk in the document.

**Open questions:**

1. ~~**Should the report lead the platform refactor, or follow it?**~~ **Resolved 2026-09-08:** lead,
   for M1/M2/M4 specifically — not a resequencing of the whole plan. Per § 19's own table these three
   depend on nothing, so [modularity-plan.md § 4b](../refactor/modularity-plan.md#4b-relationship-to-the-report-vision-doc)'s
   narrower framing wins: only claims requiring full observation lineage (cross-analyzer synthesis,
   measurement-vs-inference confidence split) wait on Phase 5. The "adapter that outlives its
   welcome" risk is bounded by the rule [§19](#19-minimum-viable-path) states — no new judgment
   invented inside the adapter — and [modularity-plan.md § 10 point 2](../refactor/modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)
   records the decision to start M1/M2/M4 now rather than continue carrying this as a live,
   disagreeing reference across three docs.
2. ~~**Does the query language earn its complexity?**~~ **Resolved:** no, not for v1. The full
   grammar in [§12.1](#121-the-query-algebra) is the defensible smaller version's growth target,
   not its starting point — v1 ships structured filter chips plus `dd.query()` over a plain
   predicate object, with the URL carrying that object as JSON. Grammar and parser only get built if
   readers actually type queries once the chips ship.
3. **Who owns the rule registry?** Rules are where domain expertise lives. If they stay C# they are
   testable and fast but need a rebuild to tune; if they are data they are tunable but need their
   own validation and a safe evaluator. Recommendation: C# with versioned ids first; revisit only
   if tuning cadence proves it wrong.
4. **Is a build-time JS bundler acceptable** in a pure-MSBuild project, or must the bundle be
   pre-built and committed? Unchanged from both siblings.
5. **Where does the observation side-car live** relative to the HTML when someone emails the report?
   Self-containment is a real property of the current artifact and this spec risks it. Probable
   answer: the HTML embeds everything any claim cites and degrades gracefully — the side-car is an
   optimization, never a dependency — but that boundary needs stating precisely.
6. **Do Markdown and text renderers get their own presentation layer**, or a shared one over the
   claim graph? [§13](#13-the-artifact-set) assumes shared. That is a real refactor of two existing
   formatters.
7. **How does an entity dossier stay bounded** for a type with 4 million instances? The dossier is
   per-entity aggregate, so it should be fine, but the "relations" panel is a graph query and needs
   a stated boundary — which then needs to appear in `coverage.boundary` rather than as a silent cap.
8. **Does "internal distribution" actually cover whole-process scalars?** [§8](#8-baselines-and-expectation)
   marks it "always" available, but that's only true where a measure has a natural per-entity
   population to rank against. A whole-process scalar — total thread count, total managed heap
   bytes, finalizer queue depth — has none; on a single dump with uptime unavailable, such a measure
   has no reference from any of the four kinds. Options: define a cross-dump corpus to rank
   process-scalars against (which §8.1 explicitly rules out as "a built-in constant is a guess about
   someone else's service"), or accept that a minority of scalar measures render "no reference
   available" honestly rather than force one. Not yet resolved.

---

## Appendix A — one claim end to end

Using the measured baseline's largest table: B6, 68,576 finalizer-queue entries, 7.7 MB shipped, 20
rendered, under a title that begins with the word "Top."

### A.1 Observations (raw, no judgment)

```json
[
  { "id": "o1", "type": "finalizer.queue-depth",
    "subjects": [{"kind":"process","id":0}], "when": {"kind":"Instant","captured":"2026-03-23T18:21:21Z","uptime":"PT9H24M"},
    "measures": { "entries": {"value":68576,"unit":"Count","semantics":"Absolute"} },
    "provenance": {"artifact":"a0","analyzer":"FinalizableObjectAnalyzer","fidelity":"Full"},
    "confidence": 1.0 },

  { "id": "o2", "type": "thread.wait-state",
    "subjects": [{"kind":"thread","id":31,"role":"finalizer"}],
    "measures": { "isBlocked": {"value":1,"unit":"None","semantics":"Absolute"} },
    "evidence": [{"kind":"stack","threadOsId":"0x1f4c"}],
    "provenance": {"artifact":"a0","analyzer":"ThreadAnalyzer","fidelity":"Full"} },

  { "id": "o3", "type": "type.finalizer-queue-share",
    "subjects": [{"kind":"type","id":412,"name":"System.Data.SqlClient.SqlConnection"}],
    "measures": { "entries": {"value":41203,"unit":"Count","semantics":"Absolute"},
                  "share":   {"value":0.601,"unit":"Fraction","semantics":"Ratio"} },
    "provenance": {"artifact":"a0","analyzer":"FinalizableObjectAnalyzer","fidelity":"Full"} }
]
```

### A.2 Signal

```json
{ "id": "s1", "rule": "finalizer.queue-not-draining@2",
  "statement": "queue depth 68,576 with the finalizer thread blocked",
  "from": ["o1","o2"] }
```

### A.3 Claim

```json
{
  "id": "c-finalizer-stall", "fingerprint": "finalizer.stall/thread-blocked/SqlConnection",
  "subjects": [{"kind":"thread","id":31},{"kind":"type","id":412}],
  "assertion": { "predicate": "Stalls", "subject": "thread:31 (finalizer)",
                 "measure": {"value":68576,"unit":"Count","semantics":"Absolute"},
                 "reference": {"kind":"distribution","text":"p99.9 of observed finalizer queues"},
                 "qualifiers": {"mechanism":"blocked finalizer","location":"SqlConnection.Dispose"} },
  "severity": "Critical",
  "support": [
    {"obs":"o1","why":"queue depth is three orders of magnitude above a draining queue"},
    {"obs":"o2","why":"the finalizer thread is blocked, so the queue cannot drain"},
    {"obs":"o3","why":"60.1% of the queue is one type, consistent with a single stuck finalizer"}
  ],
  "counter": [
    {"obs":"o7","why":"a gen0 collection had just completed; a transient spike is possible",
     "weight":"low"}
  ],
  "falsifiedBy": [
    {"kind":"capture","test":"a second dump ≥5 min later showing queue depth below ~1,000 makes this a transient spike"},
    {"kind":"windbg","test":"finalizer thread stack not blocked in native code","command":"~31s; !clrstack"},
    {"kind":"code-inspection","test":"SqlConnection.Dispose completing without a network wait contradicts the mechanism"}
  ],
  "coverage": [{"capability":"threads.stacks","status":"Complete"},
               {"capability":"heap.objects","status":"Complete"}],
  "confidence": { "measurement": 0.98, "inference": 0.86,
                  "limitingFactors": ["single snapshot — cannot distinguish stall from spike"] },
  "nextCapture": [{"what":"second full dump in 5 minutes","why":"separates stall from spike",
                   "raises":"inference","to":0.97}],
  "derivedFrom": [{"rule":"finalizer.queue-not-draining","version":2},
                  {"rule":"severity.blocking-resource","version":5}],
  "actions": ["a-inspect-finalizer-stack"]
}
```

### A.4 Rendered

> **Critical — the finalizer thread is stalled.**
> 68,576 objects are queued for finalization and thread 31 (the finalizer) is blocked; 60.1% of the
> queue is `SqlConnection`. Confidence: measurement high, inference high — *limited by a single
> snapshot, which cannot distinguish a stall from a spike.*
> **Against this:** a gen0 collection had just completed, so a transient spike is possible (weak).
> **This would be wrong if:** a dump five minutes later shows the queue below ~1,000 · the finalizer
> stack is not blocked (`~31s; !clrstack`) · `SqlConnection.Dispose` completes without a network wait.
> **Next:** capture a second dump in 5 minutes — would raise inference confidence to ~0.97.
> **Evidence:** [queue depth](#obs/o1) · [thread state](#obs/o2) · [type breakdown](#obs/o3) ·
> [all 68,576 entries](#q/finalizer-queue)

Compare with the status quo: a table titled "Top finalizer queue entries by estimated retained
size," 68,576 rows shipped, 20 visible, no statement of what it means, and 7.7 MB of payload.

## Appendix B — a type dossier from today's analyzers

Everything the existing analyzers already compute about a single type, which the report currently
scatters across nine sections. This is the concrete answer to *"what would you do if you had the
data"* — no new analysis, one new index.

| Panel | Observations | Source today |
|---|---|---|
| Identity | name, `MethodTable`, module, generic arity, base/interfaces | TypeSystem C1/C2 |
| Population | instances, shallow bytes, share of heap, rank | Memory A2 |
| Generations | gen0/1/2/LOH/POH split; gen2 fraction | GC B1 |
| Retention | exact retained bytes (dominator tree), dominator rank, dominance chain | Memory A3 |
| Roots | root kinds, static owners, root chains **with field names**, per-thread attribution | Memory A5/A6, thread retention index |
| Shape | field layout, padding waste, boxed value types | TypeSystem C2/C5 |
| Collections | if a collection: capacity vs. count, over-allocation, growth pattern | TypeSystem C3/C4 |
| Strings | if string-heavy: duplicate share, byte ownership | Memory A7 |
| Lifecycle | finalizable, queue presence, weak-reference liveness, GC handle kinds | GC B6/B7/B8 |
| Events | subscriptions held, subscriptions to it, suspected leaked handlers | Threads D4 |
| Async | if a state machine: suspend-state distribution, `async void`, fire-and-forget | Async E2 |
| Infrastructure | if a channel/connection/command: state, endpoint, pool, transaction | Infra H1–H7 |
| Allocation | allocation-site clustering, churn indicators | GC B2 |
| Over time | every measure above as a series across the session | any N>1 session |
| Claims | every claim naming this type, with support and refutation | claim graph |
| Coverage | what was **not** measured about this type, and why | coverage record |

Sixteen panels, zero new analysis, one structural change.

## Appendix C — widget catalog quick reference

| Widget | Shape | Replaces | Series-aware | Trace-ready |
|---|---|---|---|---|
| `dd-measure` | Scalar | `KeyMetrics`, `MetricBlock` | yes | yes |
| `dd-composition` | Composition | treemap in `sections.js`, `ChartBlock` | yes | yes |
| `dd-table` | Ranking | `CompactTable`, `TableBlock` | yes (extra column per snapshot) | yes |
| `dd-distribution` | Distribution | `ChartBlock`, `SparklineBlock` | yes (small multiples) | yes |
| `dd-scatter` | Correlation | — (new) | yes (animated by snapshot) | yes |
| `dd-sequence` | Sequence | `NamedStackTrace`, `StackFrameBlock`, `RootOwnedSubgraph`, `TypeSampleTrace` | n/a | yes (spans) |
| `dd-graph` | Graph | `TreeWidget`, `StackCluster`, dominator/lock/event graphs | yes (diff overlay) | yes |
| `dd-series` | Series | trend sparklines, `SeverityHistory` | inherently | inherently |
| `dd-entity` | Entity | `LeakCandidateCard`, `EventLeakGroupCard`, `EventLeakInstanceCard` | yes | yes |
| `dd-claim` | Assertion | `LeadFinding`, `ConfidenceBandBlock`, `InterpretationBlock`, `NextStepsBlock` | yes (lifecycle) | yes |

## Appendix D — the trace delta

What changes when `.nettrace` becomes a second artifact kind. Short, because the honest answer is
*"less than expected, if Parts II–III are built first"* — and this is the part of the document I am
least certain about, since no trace ingest exists yet to check it against.

**Unchanged:** session, entity model, observation record, claim graph, coverage, confidence, all
four surfaces, the query algebra, the wire format, every gate.

**Changes:**

| Area | Delta |
|---|---|
| `TimeAnchor` | `Interval` becomes common rather than degenerate; a dump instant sits *inside* a trace interval |
| Entity kinds | `method` becomes primary (it is nearly unused for dumps); `type` joins by name — fidelity `Exact` for simple types, `Low` for compiler-generated |
| Observation types | new vocabulary entries — `cpu.sample-count`, `gc.pause-duration`, `alloc.rate`, `exception.thrown` — no schema change, because the vocabulary is open |
| Widgets | `dd-sequence` gains a span/waterfall variant; `dd-composition` over `dd-sequence` is a flame graph. **No new widget kind** |
| Claims | existing rules gain fidelity — a leak-rate claim built from dump snapshots becomes a *measured* allocation rate; same rule, same fingerprint, higher confidence |
| Correlation | new claims that cite observations from two artifacts, confidence capped by join fidelity **and** alignment fidelity |
| Coverage | capability model already expresses "this analysis was dump-only, degraded" — traces just add capabilities |

The value of doing Parts II–III first is exactly this table: if the report were built trace-aware
from scratch later, it would be a second renderer and a fourth mode.

---

## Appendix E — cross-checked against a sibling implementation

A second, independently-built tool in this project's lineage (`d:\POC\Rohit_DumpDetective`) already
ships a multi-format report pipeline (`IRenderSink` → Console/Html/Markdown/Text/Json/Bin/Capture
sinks, plus `render` and `diff` commands) in production. Two things from its code are directly
relevant here.

**It independently hit, and documented, the exact problem this doc's widget vocabulary
([§10](#10-evidence-shapes-and-the-widget-vocabulary)) exists to kill.** Its
`Docs/IRenderSink-Extension-Guide.md` is an 11-step, 10-file checklist required to add *one* new
visual element type: `ReportDoc.cs` (model + `[JsonDerivedType]`) → `CoreJsonContext.cs`
(AOT JSON registration) → `IRenderSink.cs` (interface + text fallback) → `HtmlSink.cs`/`.css` (real
render) → `CaptureSink.cs` (capture for replay) → `BinSink.cs`/`JsonSink.cs` (forwarding) →
`MarkdownSink.cs`/`TextSink.cs` (text renders) → `ReportDocReplay.cs` (replay case) — with an
explicit warning that skipping any step "will silently break" a format. This is [§10.1](#101-the-shape-taxonomy)'s
"12 typed slots + 18-case `SectionBlock` union plus their bespoke JS renderers plus the second
server-side renderer" problem, independently confirmed as something a sibling team actually built,
hit, and had to write a process document to survive — not a hypothetical this spec invented to
justify a redesign. It's concrete evidence the ten-shape, one-renderer design in §10 is worth its
cost.

**It also proves a much cheaper thing is possible, and worth weighing as an interim step.** Its
`ReportDoc`/`ReportDocReplay`/`ReportDiffer` deliver working report replay and diff (the `render` and
`diff` commands — converting a saved report to a different format, or comparing two saved reports)
entirely by walking a polymorphic `ReportChapter → ReportSection → ReportElement` tree and matching
chapters/sections/rows by name or key column. No observation model, no entity join, no typed measure
semantics, no confidence. This is more fragile than [§5](#5-the-observation-store)–[§6](#6-the-claim-graph)'s
design — string-keyed matching instead of `EntityRef`/`Measure.semantics`, no distinction between "a
row disappeared" and "a row was never comparable" — but it ships real trend/diff value at a fraction
of the cost. If [§21](#21-risks-assumptions-and-open-questions)'s minimum-viable path (M1–M4) needs
to move even faster than a thin adapter over `AnalyzerDomainResult`/`InsightFinding`, an
element-tree diff in this shape is a legitimate, cheaper fallback for the diff/trend slice
specifically — not a replacement for the claim graph, but a way to de-risk shipping *something*
comparable while [modularity-plan.md Phase 5](../refactor/modularity/phase-5-observations-synthesis.md)
is still in flight.

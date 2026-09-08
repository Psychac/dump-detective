# Observation & Correlation Model

Core design doc for how analyzers report facts and how those facts fuse across sources.
Parent: [../modularity-plan.md](../modularity-plan.md). Read
[source-model.md](source-model.md) first — this builds directly on `EntityRef`, `TemporalExtent`,
and `Capability`.

---

## 1. Why observations instead of domain results

Today an analyzer returns an `AnalyzerDomainResult` subtype — an arbitrarily-shaped bag of
domain data — and a paired `IFindingGenerator` turns it into prose findings, plus a paired
`IAnalyzerTrendComparer` knows how to diff two of them. That's three hand-written, tightly-coupled
pieces per analyzer, and every one of them is shaped around a single dump.

The problem for multi-source: **there is no way to compare, correlate, or fuse two domain results
generically**, because they have no common structure. The unified doc's `DiagnosticSignal` is
reaching for exactly this — a normalized layer where fusion is possible. This document takes that
idea further: make the normalized form the analyzer's *primary* output rather than a projection
bolted on afterwards.

```csharp
public sealed record Observation
{
    public ObservationId Id { get; init; }
    public string ObservationType { get; init; }        // "gc.pressure", "type.retention", "cpu.hotspot"
    public IReadOnlyList<EntityRef> Subjects { get; init; }
    public TemporalExtent When { get; init; }
    public IReadOnlyDictionary<string, Measure> Measures { get; init; }
    public Provenance Provenance { get; init; }
    public double Confidence { get; init; }             // 0..1, analyzer's own certainty
    public IReadOnlyList<EvidenceRef> Evidence { get; init; }
}

public readonly record struct Measure(double Value, MeasureUnit Unit, MeasureSemantics Semantics);
// Semantics: Absolute | Rate | Ratio | Duration | Count — determines how it's diffed and aggregated

public sealed record Provenance
{
    public ArtifactId Artifact { get; init; }
    public string AnalyzerKey { get; init; }
    public IReadOnlySet<Capability> CapabilitiesUsed { get; init; }
    public FidelityLevel Fidelity { get; init; }        // which optional capabilities were available
}
```

An observation is **a typed, entity-anchored, time-extented, evidence-bearing quantitative fact**.
That is enough structure to diff, join, rank, and fuse generically — which is what unlocks
everything below.

---

## 2a. Observations must not carry judgment

The [analyzer-pipeline audit](../analyzer-pipeline-stages-and-leadfinding-dedup.md) supplies a
litmus test for stage-1 purity: *"if a field requires a hand-picked constant or weight to compute
(`/35.0`, `*0.30`, a threshold cutoff), it doesn't belong here."*

As first drafted, `Observation` failed that test — an observation typed `gc.pressure` carrying a
`Confidence` is a judgment call made inside the analyzer. Corrected rules:

| Field | Rule |
|---|---|
| `Measures` | **Raw only.** `gen2Ratio = 0.8`, `retainedBytes = 8.0e8`, `pauseMs = 4200`. Never a weighted composite, never a normalized-against-a-magic-constant score. |
| `ObservationType` | A **factual characterization**, not a severity claim. `gc.generation-composition`, not `gc.pressure-high`. It names *what was measured*, not *how bad it is*. |
| `Confidence` | **Measurement** confidence only — was the capability degraded, was sampling partial, was the data truncated. Never severity confidence. |
| `Subjects` / `When` / `Provenance` | Facts by construction. |

Everything the audit catalogues as Smell A — `MemoryPressureScore`'s weighted composite,
`GCPressureLevel`'s banding, `HealthScore`, `SeverityScore`, `SuspicionScore`, and
`LeakCandidateRecord.Severity` (a `FindingSeverity` baked into a domain row) — moves into synthesis
rules. So does the audit's Smell B row *selection* judgment.

**This is a multi-source requirement, not just hygiene.** A composite score computed inside an
analyzer is frozen at the fidelity of whatever capabilities that analyzer had. Computed in
synthesis, the same score sharpens automatically when a session supplies `trace.gc-events` —
which is exactly the graded-fidelity premise the platform rests on. Baked stage-1 scores would
quietly defeat it.

### Where the raw per-entity data lives

The audit's Smell B asks whether N separately-capped `Top*` lists should collapse into one complete
raw table, and flags a bounded-memory concern about doing so. The answer here: **the complete table
lives in the disk-backed index**, observations reference rows via `EvidenceRef`, and
ranking/selection happens in synthesis or render. That's the same discipline already applied to
millions of heap objects — an uncapped complete table is safe precisely because it was never going
to be materialized in memory. Domain-result `Top*` lists stop being an analyzer-side artifact.

---

## 2. Findings are derived, not authored

```
Analyzers ──emit──> Observations ──synthesis──> Findings ──> Report
                         │
                         └──> also queryable directly (UI drill-down, exports)
```

A `Finding` becomes a *synthesized narrative over one or more observations*, produced by
**synthesis rules** rather than by per-analyzer `IFindingGenerator` code:

```csharp
public sealed record Finding
{
    public string Fingerprint { get; init; }            // stable across runs — dedup/trend identity
    public Severity Severity { get; init; }
    public string Title { get; init; }
    public string Narrative { get; init; }
    public string Recommendation { get; init; }
    public IReadOnlyList<ObservationId> DerivedFrom { get; init; }   // full lineage
    public ConfidenceBreakdown Confidence { get; init; }
    public IReadOnlyList<string> Caveats { get; init; }
}
```

`DerivedFrom` is the important field: every finding traces to the exact observations that produced
it, which traces to the artifact + analyzer + capabilities that produced *those*. Full provenance
chain from prose back to bytes-on-disk. This makes "why does the tool think this?" answerable —
today's `EvidenceRef` gestures at this; the observation model makes it structural.

**Migration note.** `AnalyzerDomainResult` doesn't have to die immediately. An analyzer can emit
observations *and* keep its rich domain result for its detail section — the domain result stops
being the input to findings/trends and becomes purely presentational. That's the low-risk
sequencing (Phase 5), and it matches the unified doc's own instinct that the signal layer "should
feed reporting, not replace existing analyzer domain result contracts immediately."

---

## 3. Synthesis rules

Synthesis is where domain expertise lives, but expressed as rules over a uniform substrate rather
than as bespoke per-analyzer code:

```csharp
public interface ISynthesisRule
{
    string RuleId { get; }
    ObservationQuery Match { get; }     // declarative: types, subject kinds, measure predicates
    ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(ObservationMatchSet matched, SynthesisContext ctx);
}
```

Three tiers, all using the same mechanism:

- **Single-observation rules** — "`gc.pressure` with `gen2Ratio > 0.7` → warning." Replaces most
  of today's per-analyzer finding generators.
- **Multi-observation, single-source rules** — cross-analyzer synthesis within one artifact.
  This is what `InsightEngine` does today, generalized.
- **Cross-source rules** — § 4 below. Structurally identical; they just happen to match
  observations whose `Provenance.Artifact` differs.

That structural identity is the payoff: cross-source correlation isn't a new subsystem bolted onto
the side, it's the same synthesis engine matching a wider set.

---

## 4. Cross-source correlation

### The join

Two observations are **correlation candidates** when:

1. **Subject overlap** — they share at least one `EntityRef` with compatible `JoinKey`, OR they're
   linked by a declared entity relation (a `MethodRef` whose `DeclaringType` matches a `TypeRef`).
2. **Temporal compatibility** — their `TemporalExtent`s overlap, or one contains the other, or the
   session is unaligned (in which case this test is skipped and a caveat attaches).
3. **Process identity** — same `ProcessIdentity`. Non-negotiable; cross-process correlation is a
   separate, unbuilt feature.

### Correlation recipes worth building first

These are the concrete payoffs that justify the entire multi-source effort — worth naming, because
"we could correlate things" is not a design:

| Recipe | Dump observation | Trace observation | Fused finding |
|---|---|---|---|
| **Leak with allocation site** | `type.retention`: `T` holds 800 MB, 2 M instances, static-rooted | `alloc.hotspot`: `T` allocated at 12 k/s from `M` | "`T` is leaking — allocated from `M` at 12 k/s, retained by static root; 800 MB and growing" |
| **GC pressure with real cost** | `gc.composition`: gen2 = 80 % of heap | `gc.pause`: 12 gen2 GCs, 4.2 s total pause | "Gen2 pressure is costing 4.2 s of pause; heap composition confirms promotion, not just churn" |
| **Contention with duration** | `thread.blocked`: 8 threads blocked on lock `L` | `contention.event`: `L` accumulated 3.1 s across 40 events | "Lock `L` is the hang's cause — 3.1 s contention, 8 threads blocked at capture" |
| **Hidden exception storm** | `exception.live`: 3 `TimeoutException` on heap | `exception.burst`: 47 k `TimeoutException` thrown in 60 s | "Exception storm invisible in the dump alone — 47 k thrown, only 3 live at capture" |
| **Fire-and-forget confirmation** | `statemachine.suspended`: 4 k suspended `<SendAsync>d__7` | `cpu.sample`: near-zero time in `SendAsync` | "4 k async operations suspended and making no progress — stalled, not slow" |

The fourth row is the clearest illustration of *why* this matters: it is **not derivable from
either source alone**. A dump shows almost nothing; a trace shows a storm with no retention
context. That's the argument for multi-source in one sentence.

### Scoring

The unified doc proposes `FinalScore = Impact × Confidence × CrossSourceMultiplier`. That's a
reasonable bootstrap, but it has two flaws worth fixing before it calcifies:

1. **Multiplicative confidence double-counts corroboration.** Two weak agreeing signals shouldn't
   multiply to something weaker than either.
2. **It doesn't check independence.** Two observations from the *same artifact* by the *same
   analyzer* are not corroboration — they're one signal counted twice.

Proposed instead:

```
Impact      = domain-calibrated 0..100 (bytes, pause ms, blocked threads — normalized per type)

Confidence  = noisy-OR over INDEPENDENT corroborating observations:
                  C = 1 - Π(1 - cᵢ)     for i over distinct (artifact, analyzer) pairs
              then apply, in order:
                  × conflict penalty     (observations that contradict → multiplicative penalty)
                  ⌈ capped by min(MatchFidelity) over every EntityRef join in the lineage
                  ⌈ capped by AnchorConfidence when the finding makes a temporal claim
                  × capability-fidelity factor (analyzer ran degraded → scale down)

FinalScore  = Impact × Confidence
```

The two **caps** are the part that matters most and the part the multiplicative formula misses
entirely: no amount of agreement between sources can make a finding more certain than the identity
join underlying it. If a correlation rests on a `Low`-fidelity lambda name match, its ceiling is
low, full stop. Same for temporal claims resting on `Unknown` alignment.

```csharp
public sealed record ConfidenceBreakdown(
    double Composite,
    double EvidenceStrength,
    double IdentityFidelityCap,
    double TemporalAlignmentCap,
    double CapabilityFidelity,
    double ConflictPenalty,
    IReadOnlyList<string> LimitingFactors);   // "capped by lambda-name match fidelity"
```

Surfacing `LimitingFactors` in the report is what keeps the tool honest — a user who sees
"confidence 0.4, limited by: artifacts not time-aligned" can go fix the input and re-run.

---

## 5. Trend falls out for free

This is the strongest structural argument for the observation model.

**Trend is just synthesis over observations that share `(ObservationType, Subjects)` but differ in
`TemporalExtent`.** Given entity-anchored observations carrying typed `Measure`s with declared
`MeasureSemantics`, a *generic* differ handles the overwhelming majority of trend analysis:

- `Absolute` measures → delta, % change, direction
- `Rate` measures → rate-of-rate, acceleration
- `Ratio` measures → point difference (never % of %)
- `Count` measures → delta + growth curve fit across ≥ 3 anchors
- `Duration` measures → delta + distribution shift

Today's ~30 hand-written `IAnalyzerTrendComparer` implementations mostly re-implement this per
domain. Under this model they collapse to one generic comparer plus a small number of genuine
domain exceptions (where "worse" isn't monotonic in the measure — e.g. thread-count changes, where
both directions can be bad depending on context).

Same mechanism, no new code: a 3-dump session produces a temporal series; a dump+trace session
produces temporally-overlapping observations; a 3-dump + trace session produces both. The
"rolling trend windows" and "baseline selection semantics" the unified doc lists as trend
enhancements become **queries over the observation timeline**, not new pipeline features.

---

## 6. Guarding against false correlation

The unified doc correctly flags "false confidence in correlation" as a risk. Concrete mitigations,
beyond the confidence caps above:

- **Independence tracking** — corroboration counted per distinct `(artifact, analyzer)`, never
  per observation.
- **Conflict is first-class, not filtered.** When observations disagree (dump says a type is
  retained; trace shows it being collected steadily), that's a *finding* — "signals conflict,
  investigate" — not something to silently drop or average away.
- **Negative evidence.** "Capability `trace.alloc-samples` was available and shows *no* allocation
  of `T`" is meaningful and should be representable. Absence of an observation from an artifact
  that *could* have produced it is evidence; absence from an artifact that couldn't is not. The
  `Provenance.CapabilitiesUsed` field is what makes this distinguishable.
- **A correlation floor.** Below a configured composite confidence, findings are emitted into a
  "weak signals" appendix rather than the main report — visible, not authoritative.
- **Deterministic and versioned.** Scoring constants live in config; the report stamps a scoring
  model version (this already exists today as `ScoringModelVersion` — keep it, extend it to cover
  correlation constants).

---

## 7. Open questions

- **Observation volume.** A CPU-hotspot analyzer over a large trace could emit millions of
  observations. Resolution direction: observations are themselves disk-backed and streamed, using
  the same columnar/interned discipline as heap indices — an observation store, not an in-memory
  list. This needs real design before Phase 6, and is the single biggest unvalidated assumption in
  this model.
- **Do synthesis rules need to be code, or can they be declarative?** Declarative rules (a DSL or
  config) would let rules ship without recompiling; code rules are more expressive. Leaning:
  `ISynthesisRule` as code, with a declarative `ObservationQuery` for matching — hybrid.
- **Does `AnalyzerDomainResult` survive long-term?** Phase 5 keeps it for presentation. Whether
  detail sections should eventually be built from observations too (making domain results
  redundant) is unresolved — it would be more uniform but could lose domain-specific richness
  that's genuinely valuable in the detail sections.
- **Observation type vocabulary governance.** `ObservationType` strings are a shared namespace
  across all plugins. Uncoordinated, they'll fragment (`gc.pressure` vs `gc-pressure` vs
  `gcPressure`). Needs a registry with the same versioning discipline as capabilities.

# Phase 7 — Cross-Source Correlation & Unified Scoring

Part of [../modularity-plan.md](../modularity-plan.md). The payoff phase — where multi-source stops
being "two reports in one file" and becomes findings neither source could produce alone. Depends on
[phase-6-trace-source.md](phase-6-trace-source.md) — specifically 6b, since correlation needs
findings from trace-fed analyzers, not just the raw ingest 6a alone provides.

## Goal

Turn the synthesis engine loose across artifact boundaries: entity- and time-joined observations
from different sources producing correlated findings with honest, capped confidence.

## Why it's structurally cheap by this point

Everything needed already exists if Phases 1–6 landed as designed:

| Needed | Delivered by |
|---|---|
| Cross-source entity join | `EntityRef.JoinKey` + `EntityCanonicalizer` (Phase 1), validated (Phase 6) |
| Temporal join | `TemporalExtent` + `TimelineAligner` (Phases 1–2) |
| Uniform facts to join | `Observation` (Phases 1, 5) |
| Rule engine to match them | `ISynthesisRule` (Phase 5) |
| Multi-artifact sessions | Session DAG (Phase 4) |

So this phase is mostly **authoring correlation rules plus the confidence machinery** — not new
infrastructure. That's by design: the expensive work was front-loaded precisely so the payoff phase
would be small. If this phase looks like it needs major new plumbing, something earlier was skipped.

## Cross-checked against a sibling implementation — a cheaper path ships real value first

A sibling tool (`d:\POC\Rohit_DumpDetective`) already ships dump+trace correlation in production,
and it validates two things this phase should act on, one confirming the plan and one questioning
its sequencing:

- **Confirms the entity-join design is solving a real problem, not a hypothetical one.** Its
  `TraceDumpCorrelator`/`CorrelationEngine` implement **26 correlation rules total** (16 cross-source
  in `TraceDumpCorrelator`, 10 trace-only in `CorrelationEngine`) as hand-written static methods, each
  comparing pre-aggregated fields on named POCOs (`ctx.Sql.SlowCommandCount`, `ctx.Snapshot.ConnectionCount`,
  …) with hand-picked thresholds and hand-tuned integer confidence scores built by literal
  `score += 15; score = Math.Min(score, 95)` arithmetic repeated in every rule. This is precisely the
  "N independently-drifted judgment sites" failure mode this project's own analyzer-pipeline audit
  and [§10 point 4](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)
  warn about, now observed in a second, independent codebase — good evidence the risk is real, not
  theoretical, and that `ConfidenceBreakdown`'s named, versioned rules are worth the investment
  planned here.
- **Questions whether entity-join machinery needs to exist before correlation ships value.** Of
  those 16 cross-source rules, only one (`CheckAllocConvergesWithHeapDominance`, "top allocating type
  from trace also dominates the heap in the dump") does anything resembling an entity join, and it
  does it with a plain `HashSet<string>` case-insensitive type-name match — no canonicalizer, no
  `MatchFidelity`, no `EntityRef`. The other 15 correlate purely on **aggregate signal thresholds**
  (counts, rates, percentages) with no per-entity join at all. That means most of this sibling's
  real, shipped diagnostic value — 15 of 16 rules — needed none of Phases 1–6's entity-identity
  investment. Recommend adding a lightweight milestone, **Phase 7a**, that ships signal-level
  correlation rules (modeled directly on `ITraceDumpCorrelationRule`/`TraceDumpCorrelationContext`)
  as soon as Phase 6's trace analyzers exist, in parallel with — not gated behind — the full
  `EntityRef`/`ConfidenceBreakdown` machinery in this phase, which then only needs to land for the
  minority of recipes (starting with allocation-convergence) that actually require an entity join.
  This is a concrete instance of this plan's own §8 minimum-viable-path argument, with a real
  reference implementation to model the interim shape on.
- Its 16 rules also expand the recipe list below well past the five in
  [observation-and-correlation-model.md § 4](observation-and-correlation-model.md): LOH growth ×
  fragmentation, pinned handles × GC pause, finalizer-queue backlog × allocation rate, HTTP latency ×
  async backlog, CPU saturation × idle thread-pool workers, deadlock wait-chain overlap × blocked
  threads, connection-pool leak × live connection count, and GC-handle growth × pinned-handle count —
  worth adopting as additional recipes rather than re-deriving them from scratch.

## Work

1. **`CorrelationEngine`** — a session-scoped pipeline node producing candidate observation pairs
   by subject overlap → temporal compatibility → process identity (see
   [observation-and-correlation-model.md § 4](observation-and-correlation-model.md)). Must be a
   streaming/indexed join, not an O(n²) scan — index observations by `EntityRef.JoinKey` in the
   `ObservationStore` and probe.
2. **Correlation rules** — the five recipes from the model doc are the initial set:
   leak-with-allocation-site, GC-pressure-with-real-cost, contention-with-duration, hidden
   exception storm, fire-and-forget confirmation. Each is a normal `ISynthesisRule` matching
   observations across artifacts.
3. **Confidence implementation** — noisy-OR over independent corroboration, conflict penalty, and
   the three caps (identity fidelity, temporal alignment, capability fidelity). `LimitingFactors`
   populated with human-readable reasons.
4. **Conflict findings** — disagreement between sources emitted as its own finding type, not
   discarded.
5. **Negative evidence** — represent "capability was present and showed nothing," distinguished
   from "capability absent," using `Provenance.CapabilitiesUsed`.
6. **Weak-signal appendix** — findings below the configured confidence floor are reported
   separately rather than suppressed or promoted.
7. **Scoring config + version stamp** — constants in config, `ScoringModelVersion` extended to
   cover correlation.

## Report surface

- Every finding carries source attribution (which artifacts contributed).
- Correlated findings rank above single-source findings *only through* the scoring formula, never
  by a hardcoded bonus — cross-source is not automatically more important.
- `ConfidenceBreakdown` is rendered, not just computed: users should see *why* confidence is what
  it is.
- The correlation lineage (finding → observations → artifacts) is navigable in the report.

## Exit criteria

- All five correlation recipes produce correct findings on a real dump+trace pair from the same
  process.
- **Negative control passes**: a dump and trace from *different* processes produce no correlated
  findings, and the session warns clearly. This is the single most important test in the phase —
  a correlation engine that always finds correlations is worthless.
- Confidence caps demonstrably bind: a correlation resting on a low-fidelity lambda match reports
  low confidence with the limiting factor named.
- Unaligned artifacts still correlate on entity joins, with the temporal caveat attached.
- Correlation join is sub-quadratic and stays within bounded memory on large observation sets.

## Risk / effort

Medium effort *if* the foundations landed; the risk is concentrated in judgment, not engineering.

The real danger is **plausible false positives**. A correlation engine will always find *something*,
and a confidently-worded wrong finding is worse than no finding — it sends someone chasing a
non-existent leak for a day. Mitigations are the negative control test above, the conflict findings,
the confidence floor, and a strong bias toward under-claiming in narrative wording.

Recommend: before shipping, run the engine against several dump+trace pairs where the actual root
cause is known, and measure both precision and recall. If precision is poor, raise the floor and
ship fewer, better findings — the product value here is trust, and it's spent much faster than it's
earned.

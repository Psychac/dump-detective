# Phase 7 — Cross-Source Correlation & Unified Scoring

Part of [../modularity-plan.md](../modularity-plan.md). The payoff phase — where multi-source stops
being "two reports in one file" and becomes findings neither source could produce alone. Depends on
[phase-6-trace-source.md](phase-6-trace-source.md).

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

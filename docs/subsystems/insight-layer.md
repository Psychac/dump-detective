# Insight Layer

**Purpose:**
- Synthesize cross-analyzer diagnostics (insights) — conclusions no single analyzer can reach alone.

**Responsibilities:**
- Match observations across analyzers and turn them into ranked findings (leak suspicion, GC
  pressure, thread contention, module and crash correlations).
- Keep findings that belong to no single analyzer run separate, so they reach the report's
  Cross-Domain Insights and the console summary.

**Key types / interfaces:**
- `ISynthesisRule` — one class per insight, under `DumpDetective.Reporting/SynthesisRules/Insight/`,
  named `{ShortName}InsightRule`, all with `Finding.Analyzer = "InsightEngine"`
- `DefaultSynthesisRuleCatalog` — explicit registration (these rules have no backing analyzer)
- `SynthesisFindingEnricher` — runs the rules; returns `SynthesisEnrichment.UnattachedFindings`
  for cross-analyzer findings
- `InsightByteFormat` — shared byte formatting for the insight rules

`InsightEngine`, the old standalone class, was retired in phase 5 step 6 — see
[phase-5-step6-insight-engine-plan.md](../refactor/modularity/phase-5-step6-insight-engine-plan.md).

**Performance / safety constraints:**
- Read observations, never per-object heap data.
- Keep heuristics explainable: every finding cites its source observations in `DerivedFrom`.

**Related docs:**
- [docs/architecture.md](../architecture.md)
- [docs/known-issues.md](../known-issues.md) — open insight-rule issues

# TrendAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **14** · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §14.1 Growth Trends (per-type deltas, growth rate classification)
- §14.2 Regression Detection (new leak signals, severity escalation)

---

## Currently Produces
- `AnalyzerTrendResult`: per-analyzer metric deltas across two snapshots
- `ExtractTimeline`: per-metric values across N snapshots
- `MetricTrendDirection`: `Stable | Increasing | Decreasing | Volatile`

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| **Regression detection** — semantic "new leak" label | §14.2 | High |
| Severity classification of trend changes | §14.2 | Medium |
| Growth rate (% change per delta, not just absolute) | §14.1 | Medium |

---

## Required Changes

1. **Add `GrowthRatePercent`** to each `MetricDelta` — `(current - baseline) / baseline * 100`.
   Pure arithmetic on existing values.
2. **Add `RegressionSeverity`** enum — `None | Minor | Moderate | Severe` — applied when a
   metric crosses a threshold in the wrong direction. Thresholds configurable.
3. **Add `NewLeakSignals`** — `IReadOnlyList<NewLeakSignal>` on `AnalyzerTrendResult` — a type
   that appears in `current` leak results but was absent or negligible in `baseline`.
   Requires that `MemoryLeakDomainResult` and `StaticRootDomainResult` expose type-level
   data comparable across snapshots (they partially do via `TopRootsByRetainedBytes`).

---

## Phase Assignment — Cross-Dump, No Per-Dump Phase

`TrendAnalyzer` operates across multiple dump analyses. It has no Phase 1 or Phase 2
in the per-dump sense. Its "input" is serialized `AnalysisSnapshot` records:

```
AnalysisSnapshot serialization (per-dump artifact):
  Written at end of Phase 2 once all AnalyzerRunResults are complete.
  Format: JSON (human-readable) or MessagePack (compact binary) per snapshot.
  Stored at: {DumpDir}/snapshots/{timestamp}.snapshot.json
```

The required enhancements (`GrowthRatePercent`, `RegressionSeverity`, `NewLeakSignals`)
are pure Phase 2 computations on deserialized snapshot pairs. No new index files needed.

---

## Related Analyzers
- **`MemoryLeakAnalyzer`** — leak candidate list is a primary signal for `NewLeakSignals`
- **`StaticRootLeakDetector`** — `TopRootsByRetainedBytes` feeds cross-snapshot root comparison
- **`InsightEngine`** — consumes trend results for §14.2 regression severity escalation findings

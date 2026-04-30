# AllocationPatternAnalyzer — Design Spec

## Status
**New** · Implementation Priority **8** · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §2.3 Allocation Patterns (heuristic classification: Accumulating/Churning/Balanced)
- §9.1 Allocation Patterns (per-type generation profile, short vs long-lived)
- §9.2 GC Efficiency (GC pressure score, promotion pressure)

## Rationale
No current analyzer classifies allocation behavior or GC efficiency. Both §2.3 and §9 require
heuristic classification derived from generation distribution data that `GCGenerationAnalyzer`
already produces — this is a **pure post-processing analyzer** that requires no heap scan.

---

## Domain Result

```csharp
AllocationPatternDomainResult(
    double Gen0Pct,
    double Gen2Pct,
    double LohPct,
    AllocationProfile Profile,
    GCPressureLevel GCPressure,
    double PromotionPressureScore,
    IReadOnlyList<TypeAllocationProfile> TopShortLivedTypes,
    IReadOnlyList<TypeAllocationProfile> TopLongLivedTypes)

// Enums
AllocationProfile : Transient | Steady | Retained | Mixed
GCPressureLevel   : Low | Moderate | High | Critical

TypeAllocationProfile(
    string TypeName,
    int Gen0Count, int Gen1Count, int Gen2Count,
    double LongLivedRatio,
    AllocationProfile Profile)
```

---

## Implementation Strategy

- **Input**: `GCGenerationDomainResult` + `PerTypeGenerationProfile`
  (added to `GCGenerationAnalyzer` per its change spec)
- **No heap scan. No ClrMD calls. No index reads.** Pure arithmetic on already-produced data.
- Classification rules:
  - `Gen0Pct > 70%` → `Transient`
  - `Gen2Pct > 50%` → `Retained`
  - Mixed otherwise
- `GCPressureLevel` = `(Gen0Pct × 0.3) + (Gen2Pct × 0.5) + (LohPct × 0.2)` normalized 0–100 → level

---

## Phase Assignment — Entirely Phase 2 (Post-Processor)

`AllocationPatternAnalyzer` has **zero Phase 1 footprint**. It is a pure Phase 2 post-processor.

**Execution order constraint**:
- `GCGenerationAnalyzer.Order = 10`
- `AllocationPatternAnalyzer.Order = 11` (runs immediately after)

`RuntimeAnalysisContext` must expose `IReadOnlyList<AnalyzerRunResult> CompletedResults` so
`AllocationPatternAnalyzer.AnalyzeAsync` can retrieve `GCGenerationDomainResult` from the
already-completed run without coupling to the specific analyzer class.

---

## Related Analyzers
- **`GCGenerationAnalyzer`** — primary input; must complete before this analyzer runs
- **`InsightEngine`** — consumes `GCPressureLevel` for executive summary `GCPressureScore (0-100)` and GC pressure escalation findings

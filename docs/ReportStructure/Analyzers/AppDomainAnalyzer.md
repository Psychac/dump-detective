# AppDomainAnalyzer — Design Spec

## Status
**New** · Implementation Priority **18** · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §18.1 AppDomain Inventory (domain names, IDs, memory attribution)
- §18.3 Type Density per Module (type count from `EnumerateTypes()`, load overhead)

> §18.2 Assembly Version Conflicts is fully covered by `ModuleAnalyzer`. See [ModuleAnalyzer.md](ModuleAnalyzer.md).

## Rationale
`ModuleAnalyzer` covers version conflicts and heap footprint per assembly. Per-`AppDomain`
breakdown and type counts from `ClrModule.EnumerateTypes()` are not currently produced.

---

## Domain Result

```csharp
AppDomainDomainResult(
    int TotalDomains,
    IReadOnlyList<AppDomainSnapshot> Domains,
    int TotalDynamicModules,
    int AnonymousModuleCount,
    IReadOnlyList<ModuleTypeCountEntry> TopModulesByTypeCount)

AppDomainSnapshot(
    string Name,
    ulong Address,
    int DomainId,
    int ModuleCount,
    ulong EstimatedManagedBytes)

ModuleTypeCountEntry(
    string ModuleName,
    string AssemblyName,
    int TypeCount,
    int LiveTypeCount,
    long ObjectCount,
    ulong TotalBytes)
```

---

## Implementation Strategy

- Enumerate `ClrRuntime.AppDomains` — very fast; typically 1–3 domains in modern .NET
- For each domain, enumerate `ClrAppDomain.Modules` and call `ClrModule.EnumerateTypes()`
  (bounded to top 50 modules by `ClrModule.Size`)
- Cross-reference each `ClrType` in the module with `TypeAggregates` dict (O(1) MT lookup)
  to get live object count and bytes without a heap scan
- Anonymous modules: `ClrModule.FileName == null || ClrModule.FileName.Length == 0`
- **No heap enumeration** — purely `ClrRuntime.AppDomains` + `TypeAggregates` join

---

## Phase Assignment — Entirely Phase 2

```
Phase 2:
  1. runtime.AppDomains — enumerate; typically 1–3 items, negligible cost
  2. Per domain: domain.Modules — enumerate loaded modules
  3. Per module (top 50 by ClrModule.Size): module.EnumerateTypes() — type list
  4. For each type: heap.GetTypeByMethodTable(MT) → TypeAggregates[MT] — O(1) lookup
  5. Aggregate memory per domain, type count per module
```

No new disk file required. Purely Phase 2 ClrMD + TypeAggregates join.

---

## Related Analyzers
- **`ModuleAnalyzer`** — provides §18.2 version conflicts; `AppDomainAnalyzer` is complementary for §18.1/18.3
- **`InsightEngine`** — dynamic assembly accumulation alert (TotalDynamicModules growing), anonymous module detection

# EfCoreAnalyzer — Design Sketch

> Priority: **P2 item 2** (second half, paired with `CacheHealthAnalyzer`) — ships after
> `CacheHealthAnalyzer` because it carries the same version-layout risk as `DiScopeLeakAnalyzer`
> (EF Core internal field shapes change across major releases) and requires more field-layout
> research before implementation can begin.
>
> Feasibility: **Medium**. `ChangeTracker`'s internal `IStateManager`/`InternalEntityEntry` types
> have changed shape across EF Core 3–9. `TypedResourceSampler` infrastructure reuse amortises
> some cost; type-layout research is EF-specific and non-transferable.
>
> Effort: **L** (~3–4 wk). Version-layout research and the change-tracker entry sampler dominate.
> Compiled-query-cache reporting is strictly simpler and can ship first.

---

## 1. Problem statement

Entity Framework Core leaks manifest in two distinct ways:

1. **Undisposed / long-lived `DbContext` instances** — each `DbContext` holds its `ChangeTracker`,
   identity map, and potentially thousands of tracked entity entries. A `DbContext` leaked from a
   DI scope (or created directly and not disposed) retains all tracked entities for its lifetime.
   This is one of the highest-impact per-context memory costs in EF Core applications.

2. **Compiled-query cache growth** — `DbContext` stores compiled LINQ queries in a static-like
   per-provider cache (`_compiledQueryCache` or `QueryCompilationContextFactory`'s internal store).
   Unbounded parameterization patterns (constant values inlined in expressions rather than passed
   as parameters) cause the cache to grow without bound, retaining compiled expression trees.

DumpDetective currently has no analyzer that detects either.

---

## 2. Applicable types and EF Core version matrix

### 2.1 Primary type: `DbContext`

```
Microsoft.EntityFrameworkCore.DbContext
```

`DbContext` is `public` and abstract. Subclasses are the concrete types in user code (e.g.
`ApplicationDbContext`). Discovery should use a base-class walk: find all types whose inheritance
chain includes `Microsoft.EntityFrameworkCore.DbContext` using `ClrType.BaseType` traversal,
then enumerate instances of those concrete types.

**Shortcut via TypeAggregates**: rather than walking the full type hierarchy at scan time, use
`TypeNamePatternMatcher.HasAnyPrefix("Microsoft.EntityFrameworkCore")` in a preliminary pass over
`TypeAggregates` to find the `DbContext` base MT, then use `ClrType.Subclasses` (if ClrMD
exposes it) or scan for concrete types whose `BaseType.Name` matches. Cache the set of candidate
MTs before the heap scan.

### 2.2 Internal types and version matrix

> **Key implementation risk.** The internal field layout below must be validated against EF Core
> source on each major release. All offsets are research starting points, not stable contracts.
> Run the field-layout spike against a live dump from each supported EF Core version before
> committing any offset-based read.

#### `DbContext` (all EF Core versions)

| Field | Type | Notes |
|-------|------|-------|
| `_changeTracker` | `ChangeTracker` ref | lazy-initialized; may be null if no tracked entity touched yet |
| `_database` | `DatabaseFacade` ref | not needed for leak analysis |
| `_disposed` | `bool` | indicates disposal; field name / offset stable across EF 3–9 |

#### `ChangeTracker` → `IStateManager` (EF 3–9)

`ChangeTracker` holds an `_stateManager` field (the `IStateManager` impl, concretely
`StateManager`). `StateManager` holds:

| Field | EF 3/4 | EF 5/6 | EF 7+ | Notes |
|-------|--------|--------|-------|-------|
| `_entityReferenceMap` | `EntityReferenceMap` | same | same | Identity map: entity → `InternalEntityEntry` |
| `_dependentMap` | `Dictionary<…>` | — | removed | Removed in EF 6 |
| `_entries` count | via `_entityReferenceMap._count` | same | same | Tracked entity count — cheap scalar read |

**Approach for tracked-entity count**: read `_entityReferenceMap` from `StateManager`, then read
the `_count` field of its underlying dictionary. Avoid enumerating entries unless building the
type breakdown for top-K contexts.

#### `InternalEntityEntry` — type breakdown (top-K only)

For the top-K contexts by estimated retained size, enumerate `_entityReferenceMap._entries` up to
`MaxEntriesToSample`, read the `EntityType.Name` field of each entry's entity type (a
`IEntityType` implementation), and group by entity type name. This gives the "what entity types
are being tracked, and how many of each" breakdown that tells the user what's leaked.

#### Compiled-query cache (EF 6+)

Starting from EF Core 6, compiled queries are cached in `CompiledQueryCache`
(`Microsoft.EntityFrameworkCore.Query.Internal.CompiledQueryCache`). It holds a
`_memoryCache` field of type `IMemoryCache` — which is the same `MemoryCache` that
`CacheHealthAnalyzer` already inspects. If `CacheHealthAnalyzer` ships first, the EF compiled-
query cache can be identified as a `MemoryCache` instance whose static-root path leads to
`CompiledQueryCache`. Rather than duplicating the inspection, `EfCoreAnalyzer` can cross-reference
`CacheHealthDomainResult.CacheSnapshots` for any cache rooted via an EF type, and surface those
in its own section. This is a `IDeferredAnalyzer` pattern — `EfCoreAnalyzer` reads
`CacheHealthDomainResult` after the main pass.

For EF Core 3–5, compiled queries are cached in a `ConcurrentDictionary` inside
`QueryCompilationContextFactory` or similar internal types that vary by version. Field-layout
research required; skip in initial implementation and document as a known gap.

---

## 3. Scan design

### 3.1 Heap-scan approach: `IHeapIndexScanParticipant`

**`BeforeHeapIndexScan`**: enumerate `TypeAggregates` to find all MTs whose type inherits from
`DbContext`. Build the candidate-MT set (includes concrete user subclasses). Allocate a bounded
accumulator for candidate `DbContext` addresses.

**`OnHeapEntry`**: filter by candidate MT set. Accumulate up to `MaxContextsToInspect` addresses.

**`AnalyzeAsync`** (post-scan enrichment):
1. For each candidate, read `_disposed` and `_changeTracker`.
2. If `_changeTracker` is non-null, read the tracked-entity count via the `StateManager._entityReferenceMap._count` path.
3. Estimate retained size: tracked-entity count × average-InternalEntityEntry-size (heuristic from
   TypeAggregates if `InternalEntityEntry` is present, else use a fixed 256-byte estimate).
4. For top-K by estimated retained size: enumerate entity types in the identity map up to
   `MaxEntriesToSample`.
5. Resolve sample root path via `SampleRootPathFinder`.
6. If running as `IDeferredAnalyzer`: cross-reference `CacheHealthDomainResult` for EF-compiled-
   query caches (see §2.2).
7. Populate `Evidence` and return `EfCoreDomainResult`.

### 3.2 DbContext subclass discovery without a full type hierarchy walk

Enumerating all `ClrType.Subclasses` at scan time is expensive if not supported directly by
ClrMD's index. Preferred approach:

- Iterate `TypeAggregates` once during `BeforeHeapIndexScan`.
- For each MT, resolve the `ClrType` via `HeapAnalysisCache` (cached `GetTypeByMethodTable`).
- Check `clrType.BaseType?.Name` chains up to a configurable depth (default: 5) for
  `"Microsoft.EntityFrameworkCore.DbContext"`.
- Cache the resulting set of candidate MTs before the heap scan; do not re-walk per object.

This is O(distinct MTs) — typically a few thousand — not O(heap objects).

---

## 4. Domain result and output model

```
EfCoreDomainResult : AnalyzerDomainResult
  IsPresent                          bool           // false if no EF Core types found in heap
  TotalContextCount                  int
  DisposedContextCount               int
  UndisposedContextCount             int
  ScanCapped                         bool
  TotalTrackedEntityCount            long
  CompiledQueryCachePresent          bool
  CompiledQueryCacheEntryCount       int?           // from CacheHealthDomainResult cross-ref, or null
  ContextSnapshots                   List<EfCoreContextSnapshot>

EfCoreContextSnapshot
  Address                            ulong
  ConcreteTypeName                   string         // e.g. "ApplicationDbContext"
  IsDisposed                         bool
  TrackedEntityCount                 int
  EstimatedRetainedBytes             ulong
  EntityScanCapped                   bool
  Evidence                           Evidence
  EntityTypeBreakdown                IReadOnlyList<EntityTypeEntry>

EntityTypeEntry
  TypeName                           string         // EF entity type name
  TrackedCount                       int
  EntityState                        string?        // "Added"/"Modified"/"Unchanged"/etc., if readable
```

---

## 5. Infrastructure reuse

| Need | Existing infrastructure |
|------|------------------------|
| Type-name matching | `TypeNamePatternMatcher.HasAnyPrefix` |
| MT discovery (EF types in TypeAggregates) | `TypedResourceCandidateScanner.DiscoverCandidates` (Layer A) |
| Root path for evidence | `SampleRootPathFinder` |
| Evidence + confidence | `Evidence`, `EvidenceSignal`, `EvidenceConfidence.Compute` |
| Cross-reference compiled-query cache | `AnalyzerRunResultsExtensions.GetResult<CacheHealthDomainResult>` via `IDeferredAnalyzer` |
| Inter-analyzer result bus for leak ranking | `AnalyzerRunResultsExtensions.GetResult<EfCoreDomainResult>` from `LeakCandidateAnalyzer` |

---

## 6. Registration fan-out

| Artifact | Class name |
|----------|-----------|
| Domain result | `EfCoreDomainResult` |
| Finding generator | `EfCoreFindingGenerator : IFindingGenerator<EfCoreDomainResult>` |
| Trend comparer | `EfCoreTrendComparer` — delta on `UndisposedContextCount`, `TotalTrackedEntityCount` |
| Section builder | `EfCoreSectionBuilder : ISectionBuilder<EfCoreDomainResult>` |

---

## 7. Scan caps

```
MaxContextsToInspect         200     // DbContext address accumulator cap
MaxContextsToEnrich           20     // top-K contexts to run SampleRootPathFinder on
MaxEntriesToSample          5000     // identity-map entry enumeration cap per context
MaxEntityTypesToReport        30     // M in the entity-type breakdown
BaseTypeScanDepth              5     // max inheritance-chain hops for DbContext subclass discovery
```

---

## 8. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| `StateManager` internal field names change in EF Core major releases | Per-version field-name/offset table (`EfCoreFieldLayout` struct); graceful degradation to count-only mode when layout unresolvable |
| User's DbContext subclass uses a custom change tracker | Check `_changeTracker` is a known `ChangeTracker` type before reading its fields; skip enrichment otherwise |
| Very large identity map (`_count` > `MaxEntriesToSample`) | Set `EntityScanCapped = true`; still report total count from scalar `_count` read |
| Compiled-query cache not yet inspected (CacheHealthAnalyzer not run) | Gate the cross-reference on `GetResult<CacheHealthDomainResult>` returning non-null; if null, set `CompiledQueryCachePresent = false` and log a diagnostic |
| EF Core 3–5 compiled-query cache location varies | Skip compiled-query cache reporting for those versions in v1; document as known gap |
| Multiple `DbContext` types in the same application | All concrete subclass MTs are collected — all contexts are reported |

---

## 9. Implementation sequence recommendation

Given the field-layout research cost, ship in two increments:

**Increment 1 (no version risk):** `DbContext` instance count, disposed/undisposed breakdown,
total tracked-entity count from scalar `_count` read, root path evidence. This requires only the
`_disposed` and `_changeTracker` reads plus the `StateManager._entityReferenceMap._count` chain —
all of which are public-ish and stable.

**Increment 2 (version-gated):** Entity-type breakdown (full `_entityReferenceMap` enumeration),
`EntityState` classification, compiled-query cache cross-reference. Gate behind runtime-version
guards and the field-layout spike.

---

## 10. What this analyzer does NOT do

- Report on database connection lifetimes (that is `DbConnectionAnalyzer`'s domain).
- Detect N+1 query patterns (a runtime concern; not visible in a static dump).
- Enumerate pending migrations or schema state.
- Analyse EF Core's internal query pipeline or expression tree caches beyond the
  compiled-query-cache entry count.
- Support Dapper, `ADO.NET` (raw), or other ORMs — scope is strictly EF Core.

# DiScopeLeakAnalyzer — Design Sketch

> Priority: **P2 item 1** — highest-value missing capability.
> All prerequisites satisfied: P0 ranking engine, Evidence/EvidenceSignal model, P1
> type-classification layer (`TypeNamePatternMatcher`), typed-resource sampler
> (`TypedResourceScanDriver`), and thread-stack dispatcher are all in place.
>
> Feasibility: **Medium**. `ServiceProviderEngineScope` is an `internal` type whose field layout
> has shifted across .NET 5→9 concrete DI implementations. Plan for a per-major-version
> offset matrix and ongoing re-validation cost.
>
> Effort: **L** (~3–4 wk), mostly field-layout research and the version-dispatch table.

---

## 1. Problem statement

The Microsoft DI container creates a new `ServiceProviderEngineScope` for every `IServiceScope`
that is created via `IServiceScopeFactory.CreateScope()`. If a scope is not disposed — or is
disposed but a transient service it resolved holds a reference back into the scope's internal
`_resolvedServices` dictionary — every resolved service in that scope is retained for the process
lifetime. This is the most common DI-related memory leak pattern in long-running ASP.NET Core
applications, and DumpDetective currently has no analyzer that detects it.

### What we want to report

- **Undisposed scope count** — how many live `ServiceProviderEngineScope` / `AsyncServiceScope`
  instances are in the heap, and how many of them have not been disposed (`_disposed` flag not set).
- **Scope retention estimate** — estimated retained bytes per undisposed scope, derived from the
  count/size of entries in its `_resolvedServices` dictionary.
- **Root path** — sample root path from the scope to a GC root, to tell the user *why* the scope
  is alive (static field, thread local, ambient scope stack, etc.).
- **Service type breakdown** — for the top-K undisposed scopes: the set of resolved service
  types with instance counts, so the user knows what is being leaked, not just how much.

---

## 2. Applicable types and .NET version matrix

The concrete implementation class for `IServiceScope` in `Microsoft.Extensions.DependencyInjection`
is `ServiceProviderEngineScope` across all supported .NET versions. The class is `internal` and
`sealed`; its layout is not part of any public API contract.

### 2.1 Type name patterns (for `TypeNamePatternMatcher`)

```
Microsoft.Extensions.DependencyInjection.ServiceProviderEngineScope
Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope
Microsoft.Extensions.DependencyInjection.ServiceProvider+ServiceProviderEngineScope
```

The namespace/nesting path changed between minor DI versions. All three should be treated as
candidate type names via `TypeNamePatternMatcher.HasAnyPrefix` + a `ServiceProviderEngineScope`
short-name check. The `AsyncServiceScope` wrapper struct is a stack-allocated value type
containing a reference to the scope; scanning for `AsyncServiceScope` is not useful — only the
inner scope instance counts.

### 2.2 Field layout (per major .NET version)

> **Key implementation risk.** This table must be re-verified on each new .NET major release before
> shipping. Treat all field offsets below as starting points for empirical verification, not stable
> facts.

| .NET | `_disposed` | `_resolvedServices` | `_rootProvider` | Notes |
|------|-------------|--------------------|-----------------|----|
| 5 | `bool` at offset 8 | `Dictionary<ServiceCacheKey, object>` ref at offset 16 | `ServiceProvider` ref at offset 24 | `ServiceProviderEngineScope` in `ServiceLookup` namespace |
| 6 | Same shape as .NET 5 | Same | Same | Layout unchanged |
| 7 | Same shape | Same | Same | Inlining changes; field offsets may shift |
| 8 | `bool` at offset 8 | `Dictionary<ServiceCacheKey, object>` ref at offset 16 | `ServiceProvider` ref at offset 24 | Keyed-service additions added `_keyedResolvedServices`; check both dicts |
| 9 | TBD — must verify against `dotnet/runtime` source at GA | TBD | TBD | `ActivatorUtilities.ObjectFactory` changes; scope internals may move |

**Spike required**: validate each row against the actual field offsets in a live dump from that
runtime version before implementing the version-dispatch logic. Use `ClrObject.ReadField` with a
named field lookup first; if the field name isn't resolvable (ClrMD doesn't always surface
private field names by name on private types), fall back to offset-based reads verified by a
small in-process sanity check (read the root provider pointer and confirm it resolves to a
`ServiceProvider` type).

### 2.3 Discovering the runtime version

```csharp
// already available on AnalysisContext
string? runtimeVersion = context.Runtime.ClrInfo.Version.ToString(); // e.g. "9.0.0"
int majorVersion = context.Runtime.ClrInfo.Version.Major;
```

---

## 3. Scan design

### 3.1 Heap-scan approach

`DiScopeLeakAnalyzer` implements `IHeapIndexScanParticipant` (the same dispatcher-participant
interface used by `EventLeakAnalyzer`, `CrashAnalyzer`, etc.) so it joins the single shared
heap-index pass. It does not need its own separate heap walk.

**`BeforeHeapIndexScan`**: resolve the `MethodTable`(s) for `ServiceProviderEngineScope` from
`TypeAggregates`. Build the version-specific field-layout record (offset table based on
`majorVersion`). Allocate a fixed-capacity bounded accumulator for candidate scopes
(per `MaxScopesToInspect`, default 500).

**`OnHeapEntry`**: filter by MethodTable match. For each matching entry, read the `_disposed`
field (a single byte read at the known offset). Accumulate all undisposed-scope addresses into the
bounded candidate list; track total scope count and disposed-scope count regardless.

**`AnalyzeAsync`** (post-scan enrichment):
1. For each candidate in the bounded list, read the `_resolvedServices` dictionary and sum the
   entry count and estimated size (entry count × average-service-size heuristic, since reading
   every value's type would be expensive at scale).
2. Resolve a sample root path for the top-K candidates via `SampleRootPathFinder`.
3. Build per-scope service-type breakdowns only for the top-K by estimated retained size (read
   the `ServiceCacheKey` / type field from dictionary entries for those scopes only — bounded
   cost, not for every undisposed scope).
4. Populate `Evidence` records for top-K and return `DiScopeLeakDomainResult`.

### 3.2 Reading a `Dictionary<ServiceCacheKey, object>` from the heap

A managed `Dictionary<TKey, TValue>` in .NET stores its entries in a private `Entry[]` array
(`_entries`) at a well-known offset. Each `Entry` is a struct with `hashCode` (int), `next` (int),
`key` (TKey), and `value` (TValue) fields. Reading entry count from `_count` (int field, stable
offset) is cheap and avoids enumerating the array for the aggregation path.

For service-type breakdown (top-K only), enumerate `_entries` up to `_count`, read the `key`
field (a `ServiceCacheKey` struct containing a `Type` reference), and resolve the type name via
`TypeAggregateNameResolver`. This is bounded to top-K scopes and their entry counts, so the
total read cost is O(K × scope_entry_count), not O(total_live_scopes × entry_count).

---

## 4. Domain result and output model

```
DiScopeLeakDomainResult : AnalyzerDomainResult
  TotalScopeCount          int
  DisposedScopeCount       int
  UndisposedScopeCount     int
  ScanCapped               bool          // true if > MaxScopesToInspect
  TopLeakingScopeSnapshots List<DiScopeLeakSnapshot>   // top-K by est. retained bytes

DiScopeLeakSnapshot
  Address                  ulong
  EstimatedRetainedBytes   ulong
  ResolvedServiceCount     int           // _count from _resolvedServices dict
  KeyedServiceCount        int           // _count from _keyedResolvedServices (.NET 8+), 0 if absent
  ScanCapped               bool
  Evidence                 Evidence
  ServiceTypeBreakdown     IReadOnlyList<ServiceTypeEntry>  // top-M types by instance count

ServiceTypeEntry
  TypeName                 string
  InstanceCount            int
```

`Evidence` is the standard `Evidence`/`EvidenceSignal` model already used by
`DominatorAnalyzer`, `StaticRootLeakDetector`, and `EventLeakAnalyzer`.

---

## 5. Infrastructure reuse

| Need | Existing infrastructure |
|------|------------------------|
| Type-name pattern matching (scope type discovery) | `TypeNamePatternMatcher.HasAnyPrefix` + short-name check |
| Candidate MT discovery from TypeAggregates | `TypedResourceCandidateScanner.DiscoverCandidates` (Layer A of `TypedResourceScanDriver`) |
| Evidence population (root path + signals) | `Evidence` + `SampleRootPathFinder` |
| Confidence scoring | `EvidenceConfidence.Compute(evidence)` |
| Generation/segment classification | `SegmentKindMapper.ResolveGeneration` |
| Inter-analyzer result bus (for ranking) | `AnalyzerRunResultsExtensions.GetResult<DiScopeLeakDomainResult>` from `LeakCandidateAnalyzer` |

---

## 6. Registration fan-out

Following the standard 4x fan-out pattern:

| Artifact | Class name |
|----------|-----------|
| Domain result | `DiScopeLeakDomainResult` |
| Finding generator | `DiScopeLeakFindingGenerator : IFindingGenerator<DiScopeLeakDomainResult>` |
| Trend comparer | `DiScopeLeakTrendComparer : IAnalyzerTrendComparer` — delta on `UndisposedScopeCount`, estimated retained bytes total |
| Section builder | `DiScopeLeakSectionBuilder : ISectionBuilder<DiScopeLeakDomainResult>` |

Register in `DefaultAnalyzerFactory`, `DefaultAnalyzerFeatureModuleCatalog`, and
`SectionIdDomainMap`.

---

## 7. Scan caps and memory bounds

```
MaxScopesToInspect          500     // candidate accumulator cap; ScanCapped flag set if exceeded
MaxScopesToEnrich            20     // top-K scopes to run SampleRootPathFinder on
MaxServiceTypesPerScope      30     // M in the service-type breakdown for top-K scopes
MaxServiceEntriesPerScope  2000     // hard stop on _entries enumeration per scope (malformed dict guard)
```

All accumulators are fixed-capacity. No `ToList()` on `EnumerateIndexedEntries`.

---

## 8. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Field layout shifts on a new .NET major release | Per-version offset table in `DiScopeFieldLayout` readonly struct; verification spike before each release; graceful degradation (skip enrichment, report count only) when layout cannot be confirmed |
| `_resolvedServices` dict has GC-collected entries (`WeakReference` values) | Check value validity (`obj.IsValid`) before reading type; skip invalid entries rather than throwing |
| Very large scope (`_count` > `MaxServiceEntriesPerScope`) | Hard-cap entry enumeration; set `ScanCapped` on the snapshot |
| Multiple DI containers in the same process (e.g. nested test hosts) | Each `ServiceProvider` root is independent; scopes from all roots are counted — this is correct behavior, not a bug |
| Scope type name varies by DI version | Candidate set is resolved from TypeAggregates by short-name match across all three fully-qualified name patterns in §2.1 |

---

## 9. What this analyzer does NOT do

- Detect services resolved from the root provider that should have been scoped (anti-pattern
  detection requires understanding service lifetimes, which are not in the dump).
- Detect circular-dependency cycles in the DI graph (a build-time concern, not a dump concern).
- Enumerate keyed services below .NET 8 (the `_keyedResolvedServices` field does not exist).
- Walk `IHostedService` registrations or startup-time service creation.

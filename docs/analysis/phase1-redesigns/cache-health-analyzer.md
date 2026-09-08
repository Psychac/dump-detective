# CacheHealthAnalyzer — Design Sketch

> Priority: **P2 item 2** (first half) — highest-feasibility new analyzer, best
> value-to-effort ratio of all six. Ships before `EfCoreAnalyzer` despite sharing
> the same roadmap item because it has no version-layout risk and no feasibility spike.
>
> Feasibility: **High**. `MemoryCache.CoherentState._entries` layout has been stable since
> .NET Core 3.1. Reuses the existing static-field sweep from `StaticRootLeakDetector`,
> `TypedResourceScanDriver`, and the `Evidence` model — no new infrastructure required.
>
> Effort: **S–M** (~1–1.5 wk). Main cost is the cache-entry typed sampler; reachability
> rides on existing infrastructure.

---

## 1. Problem statement

`Microsoft.Extensions.Caching.Memory.MemoryCache` is the standard in-process cache in .NET
applications. Three failure modes surface in heap dumps:

1. **Entry count runaway** — the cache has accumulated far more entries than expected, indicating
   a missing or ineffective size limit or eviction policy.
2. **Expired-but-unevicted entries** — entries whose `AbsoluteExpiration` or `SlidingExpiration`
   has passed at dump-capture time, but which have not yet been swept by the background cleanup
   compaction timer. This signals a stalled expiration scan (e.g. the timer fired while the
   lock was held, or the cache is never accessed, so the lazy-expiry path never triggers).
3. **Cache rooted via a leaked scope or static field** — the cache instance itself is not
   collected even though the subsystem it belongs to has been torn down, because it is reachable
   from a static field or a leaked DI scope.

DumpDetective currently has no analyzer that reports any of these conditions.

---

## 2. Applicable types

### 2.1 Primary type

```
Microsoft.Extensions.Caching.Memory.MemoryCache
```

The implementation class (`MemoryCache`) is `sealed` and `public`. Its type name is stable across
.NET Core 3.1–9 and Microsoft.Extensions.Caching.Memory 3.x–9.x.

**Variant:** `Microsoft.Extensions.Caching.Memory.MemoryCache+CoherentState` is the private inner
class that actually holds the entry dictionary. This inner type must also be discovered to read
the entry collection.

Custom `IMemoryCache` implementations cannot be inspected beyond their type name and object size.
They should be enumerated and counted but not field-introspected.

### 2.2 Entry type

```
Microsoft.Extensions.Caching.Memory.CacheEntry
```

A `CacheEntry` is `internal sealed`. Its relevant fields:

| Field | Type | Purpose |
|-------|------|---------|
| `AbsoluteExpiration` | `DateTimeOffset?` | wall-clock expiry; `null` if not set |
| `SlidingExpiration` | `TimeSpan?` | sliding window; `null` if not set |
| `LastAccessed` | `DateTimeOffset` | last-access timestamp used by sliding expiry |
| `Value` | `object` | the cached value — its type name and size are the diagnostic payload |
| `_isDisposed` | `bool` | whether the entry has been evicted/disposed |

Layout has been stable since .NET Core 3.1. All fields are readable by name via ClrMD on
current dumps; no offset-table required.

### 2.3 Type-name patterns

Use `TypeNamePatternMatcher.HasAnyPrefix` with:
- `"Microsoft.Extensions.Caching.Memory.MemoryCache"` — catches `MemoryCache` and `MemoryCache+CoherentState`
- `"Microsoft.Extensions.Caching.Memory.CacheEntry"` — entry instances

Also collect `IMemoryCache`-implementing types that are not `MemoryCache` itself (indicate custom
implementation, count only).

---

## 3. Scan design

### 3.1 Heap-scan approach

`CacheHealthAnalyzer` implements `IHeapIndexScanParticipant` (joins the shared dispatcher pass).

**`BeforeHeapIndexScan`**: resolve `MethodTable`s for `MemoryCache` and `CacheEntry` from
`TypeAggregates`. A zero-result means the package is not loaded — set an "absent" flag and return
an empty domain result from `AnalyzeAsync`.

**`OnHeapEntry`**: accumulate `MemoryCache` instance addresses (bounded to `MaxCachesToInspect`,
typically 100 — applications rarely have more than a handful) and `CacheEntry` count/total-size
into MT-keyed accumulators (count, estimated total size). `CacheEntry` instances are not
individually retained in memory — only aggregate counters per MethodTable.

**`AnalyzeAsync`** (post-scan enrichment, for each discovered `MemoryCache` instance):
1. Resolve the `CoherentState` reference (via the `_coherentState` field on `MemoryCache`).
2. Read `CoherentState._entries` (a `ConcurrentDictionary<object, CacheEntry>` reference) and
   extract the entry count via the dictionary's `_count` field (cheap scalar read).
3. Enumerate `CacheEntry` instances up to `MaxEntriesToSample` for expiry analysis:
   - Read `AbsoluteExpiration` and `LastAccessed` + `SlidingExpiration`; compare against
     `context.CaptureTime` (or current time as a proxy — the exact capture time may not be in the
     dump, but GC timestamps or the dump's file mtime are usable proxies) to classify entries as
     `Live`, `Expired`, or `Expiry_Unknown`.
   - Collect the top-K value types (by estimated size) for the "cached value type breakdown."
4. Resolve a sample root path for the cache instance via `SampleRootPathFinder` to characterise
   its retention source (static field, DI singleton, etc.).
5. Populate `Evidence` and return `CacheHealthDomainResult`.

### 3.2 Static-field reachability check

Reuse `StaticRootLeakDetector`'s existing static-field sweep approach: after `AnalyzeAsync`
collects discovered cache addresses, check whether each cache is reachable from a static field by
looking it up in the `RootSetCache` (already built by `GCRootAnalyzer` / `StaticRootLeakDetector`
if they run first, shared via `AnalysisContext`).

This is a lookup, not a new sweep — `RootSetCache` already exposes `TryGetRootRecord(address)`
or equivalent. The result (root kind: `Static`, `Thread`, `Handle`) becomes an `EvidenceSignal`
on the `CacheEntry`'s parent `Evidence`.

---

## 4. Domain result and output model

```
CacheHealthDomainResult : AnalyzerDomainResult
  DiscoveredCacheCount          int
  TotalEntryCount               long           // sum across all discovered caches
  TotalEstimatedEntryBytes      ulong
  ScanCapped                    bool
  CacheSnapshots                List<CacheSnapshot>
  CustomImplementationCount     int            // IMemoryCache impls that aren't MemoryCache

CacheSnapshot
  Address                       ulong
  TypeName                      string         // e.g. "MemoryCache" or custom type name
  EntryCount                    int            // from _count
  ExpiredEntryCount             int            // entries past expiration at capture time
  ExpiredEntryCountUnknown      bool           // set if capture time not determinable
  EstimatedTotalEntryBytes      ulong
  EntryScanCapped               bool
  RootKind                      string?        // "Static", "Thread", "Handle", null if unknown
  Evidence                      Evidence
  TopValueTypes                 IReadOnlyList<CacheValueTypeEntry>

CacheValueTypeEntry
  TypeName                      string
  InstanceCount                 int
  EstimatedTotalBytes           ulong
```

---

## 5. Infrastructure reuse

| Need | Existing infrastructure |
|------|------------------------|
| Type-name matching | `TypeNamePatternMatcher.HasAnyPrefix` |
| MT discovery from TypeAggregates | `TypedResourceCandidateScanner.DiscoverCandidates` (Layer A) |
| Root reachability classification | `RootSetCache` (shared, already populated by earlier analyzers) |
| Root path for evidence | `SampleRootPathFinder` |
| Evidence + confidence | `Evidence`, `EvidenceSignal`, `EvidenceConfidence.Compute` |
| Generation resolution | `SegmentKindMapper.ResolveGeneration` (for entry generation breakdown if desired) |

---

## 6. Registration fan-out

| Artifact | Class name |
|----------|-----------|
| Domain result | `CacheHealthDomainResult` |
| Finding generator | `CacheHealthFindingGenerator : IFindingGenerator<CacheHealthDomainResult>` |
| Trend comparer | `CacheHealthTrendComparer` — delta on `TotalEntryCount`, `ExpiredEntryCount` |
| Section builder | `CacheHealthSectionBuilder : ISectionBuilder<CacheHealthDomainResult>` |

---

## 7. Scan caps

```
MaxCachesToInspect        100     // MemoryCache instance accumulator cap (rarely exceeded)
MaxEntriesToSample        5000    // CacheEntry enumeration cap per cache for expiry analysis
MaxValueTypesToReport      20     // top-M value types in the breakdown
```

---

## 8. Capture-time estimation for expiry analysis

The dump does not contain a reliable "dump captured at UTC time T" field that ClrMD exposes
directly. Two proxies in decreasing precision:

1. **Process start time** — available via `ClrRuntime` / DAC metadata; combined with a known
   uptime estimate (unavailable from the dump alone), this gives a lower bound.
2. **Dump file `LastWriteTime`** — passed into `AnalysisContext` via `AnalysisOptions` or
   derivable from the dump file path. Use this as the "now" for expiry classification.
3. **Flag as `ExpiredEntryCountUnknown`** — if neither proxy is reliably available, record the
   fact that expiry classification was skipped rather than producing wrong numbers.

Capture-time estimation is a best-effort heuristic. Label it as such in the section builder.

---

## 9. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| `CoherentState` inner-class name changes | Fall back to scanning for `CacheEntry` instances directly if `CoherentState` reference is not resolvable; still produce aggregate counts |
| `ConcurrentDictionary` internal layout changes | Read `_count` by field name (stable since .NET Core 3.1); if field not found, skip expiry analysis and report entry count as `Unknown` |
| Very large cache (`_count` > `MaxEntriesToSample`) | Set `EntryScanCapped = true`; still report total entry count from the scalar `_count` read |
| Custom `IMemoryCache` implementations | Count but do not field-introspect; surface in `CustomImplementationCount` |
| Capture time unknown → false "expired" classification | Gate expiry classification on `context.CaptureTimeAvailable`; set `ExpiredEntryCountUnknown` when not available |

---

## 10. What this analyzer does NOT do

- Inspect `IDistributedCache` (`Redis`, `SQL Server`) — those caches are external services, not
  in-heap.
- Measure cache hit/miss ratios — not available in a static dump.
- Enumerate individual cached values beyond type name and size estimate.
- Detect over-caching of large objects (that overlaps with `StringAnalyzer` / `DominatorAnalyzer`
  for specific value types; a future finding-generator cross-reference could flag it).

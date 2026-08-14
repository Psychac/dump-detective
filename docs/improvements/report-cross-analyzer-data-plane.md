# Cross-Analyzer Data Plane — Analysis & Proposal

**Companion docs:**
- [report-information-architecture.md](report-information-architecture.md) — the *findings* plane (correlating diagnoses). Complementary, lower priority than this.
- [report-display-vision.md](report-display-vision.md) — visual presentation.

**This doc is about the data plane:** many analyzers independently measuring the *same entities* and each publishing its own table, instead of contributing columns to one.

**Source of truth:** read from code. Where `docs/ReportStructure/` disagrees, the code is described.

---

## 1. The finding in one paragraph

DumpDetective already has a proper cross-analyzer data spine **inside the analysis layer**: a MethodTable-keyed per-type aggregate table built once in Phase 1 and persisted to disk, plus a shared-scan dispatcher that fans one heap-index pass out to 11 analyzers and one thread-stack walk out to 4. That architecture is good. The problem is the **publish boundary**: every analyzer takes its slice of that shared, MT-keyed data, computes its specialization, truncates to a private top-N, drops the MethodTable, and publishes a name-keyed record type of its own invention. There are **21 such per-type record types across 18 domain-result files, using 5 different field names for "the type this row is about", and not one of them carries a MethodTable.** The reporting layer then tries to rebuild the join by matching type-name strings across independently-truncated top-N lists. That is why each analyzer "shows its own thing."

---

## 2. What already works (do not rebuild this)

### 2.1 MT-keyed shared type aggregates

[`TypeAggregateIndexEntry`](../../src/DumpDetective.Analysis/Indexing/TypeAggregateIndexEntry.cs) — built during the Phase 1 heap scan, written to `TypeAggregateIndex.bin` (80-byte records), section `TypeAggregates = 1` in the cache container:

```csharp
internal readonly record struct TypeAggregateIndexEntry(
    ulong MethodTable,          // ← the real identity
    int   ModuleId,
    long  Count,
    ulong TotalSize,
    long  LohCount,
    ulong LohSize,
    ulong SampleAddress,
    long  Gen0Count = 0,
    long  Gen1Count = 0,
    long  Gen2Count = 0,
    TypeAggregateFlags Flags = TypeAggregateFlags.None);
```

Plus [`TypeAggregateFlags`](../../src/DumpDetective.Analysis/Indexing/TypeAggregateFlags.cs) — per-type classification bits written once and consumed by name: `IsStringType`, `IsTaskType`, `IsDelegateType`, `IsFinalizableType`, `IsArrayType`, `IsAsyncStateMachineType`. The doc comment is explicit that this exists so analyzers can "filter the TypeAggregates dictionary without re-scanning the heap."

This is exactly the right primitive, and it is already on disk.

### 2.2 Shared scan dispatchers

[`IHeapIndexScanParticipant`](../../src/DumpDetective.Analysis/Pipeline/IHeapIndexScanParticipant.cs) — "N participating analyzers cost one scan instead of N scans." Current participants (11):

`AsyncTaskAnalyzer`, `CollectionAnalyzer`, `CrashAnalyzer`, `DbConnectionAnalyzer`, `DominatorAnalyzer`, `EventLeakAnalyzer`, `EventLeakFastScanner`, `HangAnalyzer`, `HttpObjectAnalyzer`, `StringAnalyzer`, `WcfChannelAnalyzer`

[`IThreadStackScanParticipant`](../../src/DumpDetective.Analysis/Pipeline/IThreadStackScanParticipant.cs) — one shared per-thread frame walk, sized to `max(all participants' requested frame counts)`. Current participants (4):

`HangAnalyzer`, `LockGraphAnalyzer`, `ThreadAnalyzer`, `ThreadStackClusterAnalyzer`

Both dispatchers isolate per-participant failures and report `succeeded` so consumers can gate on trustworthiness. This is careful, correct work.

**So: the expensive part — shared acquisition — is solved. The cheap part — shared representation — is not.**

---

## 3. Where it breaks: the publish boundary

### 3.1 Identity is dropped

Compare upstream to what gets published:

| | Key | Count type |
|---|---|---|
| `TypeAggregateIndexEntry` (shared, on disk) | `ulong MethodTable` + `int ModuleId` | `long` |
| `TypeSnapshot` (published by MemoryAnalyzer) | `string TypeName` | `int` ← narrowed |
| `TypeGenerationProfile` (GCGenerationAnalyzer) | `string TypeName` | `long` |
| `TypeShapeProfile` (ObjectShapeAnalyzer) | `string TypeName` | — |

[`TypeSnapshot`](../../src/DumpDetective.Core/Models/AnalyzerDomainResult.cs#L20-L29) is the closest thing to a canonical type row in the system, and it has no `MethodTable` field. `Count` narrows `long → int` at the boundary — on a 25 GB dump with a few hundred million instances of a small type, that overflows.

### 3.2 Twenty-one per-type record types, five names for the same concept

Across `src/DumpDetective.Analysis/Models/`:

| Field name | Records using it |
|---|---|
| `TypeName` | `TypeSnapshot`, `TypeGenerationProfile`, `TypeShapeProfile`, `AllocationPattern*`, `AsyncStateMachine*` (×2), `Boxing*` (×2), `Dominator*` (×2), `FinalizableObject*` (×2), `LeakCandidate*`, `NewLeakSignal`, `ReferenceChain*`, `StaticRoot*` (×2), `String*`, `LargeObjectSnapshot`, `LohTypeProfile`, `Infrastructure*` (×9) |
| `ElementTypeName` | `ArrayDomainResult` (×3) |
| `ValueTypeName` | `BoxingDomainResult` |
| `TargetTypeName` | `GCRootDomainResult` (×2) |
| `ObjectTypeName` | `LockGraphDomainResult` |

Every one is a string. None carries an MT. The same physical type is represented by five differently-named string fields in twenty-one different record shapes.

There are also two "shared primitives" that make the lossiness explicit:

```csharp
public sealed record NameCountEntry(string Name, int Count);
public sealed record NameBytesEntry(string Name, ulong Bytes);
```

A name and a number, with the producer and the identity both discarded.

### 3.3 Each analyzer truncates independently, then the report tries to intersect the truncations

[`TypeSystemSectionBuilder`](../../src/DumpDetective.Reporting/SectionBuilders/TypeSystemSectionBuilder.cs) is the one place that attempts a real cross-analyzer type join — and it is the clearest demonstration of the problem:

```csharp
// join driver: Memory's top-30, and only Memory's top-30
int limit = Math.Min(memory.TopTypes.Count, TopRows);   // TopRows = 30
for (int i = 0; i < limit; i++)
{
    TypeSnapshot type = memory.TopTypes[i];
    TypeShapeProfile?      profile = FindShape(shape, type.TypeName);
    TypeGenerationProfile? gen     = FindGeneration(gcGen, type.TypeName);
    ...
}

private static TypeShapeProfile? FindShape(ObjectShapeAnalyzerDomainResult? shape, string typeName)
{
    for (int i = 0; i < shape.TopReferenceHeavyTypes.Count; i++)
        if (string.Equals(shape.TopReferenceHeavyTypes[i].TypeName, typeName, StringComparison.Ordinal))
            return shape.TopReferenceHeavyTypes[i];
    for (int i = 0; i < shape.TopValueHeavyTypes.Count; i++)
        ...
}
```

Four defects, all structural rather than sloppy:

1. **The join is an intersection of independently-chosen top-Ns.** A type in Memory's top-30 that fell outside ObjectShape's top-N renders `"—"`. Not "unknown for this type" — just a dash, indistinguishable from "measured as zero." The join is lossy in a way the reader cannot see.
2. **Driven solely by Memory's ranking.** A type that is #1 by retained size, #1 by LOH bytes, and #1 by finalizable overhead but #45 by shallow size never appears in the table at all.
3. **O(n·m) linear scans on `StringComparison.Ordinal`** against generic type names that can run to hundreds of characters.
4. **The Method Table column renders the literal string `"N/A"`** ([line 75](../../src/DumpDetective.Reporting/SectionBuilders/TypeSystemSectionBuilder.cs#L75)) — the table has a column for the exact identity that would make the join correct, and it cannot fill it, because the identity was discarded three layers upstream.

### 3.4 Only 6 of ~20 type-measuring analyzers participate

`TypeSystemSectionBuilder.SourceAnalyzers` = Memory, GCGeneration, ObjectShape, Module, GCRoot, Dominator.

Analyzers that measure per-type facts and are **not** in the join, each printing its own top-N table in its own section instead:

| Analyzer | Per-type fact it holds | Where it renders today |
|---|---|---|
| `StringAnalyzer` | bytes of strings owned per owning type (`TopStringOwnerTypes`) | A7 |
| `ArrayAnalyzer` | array waste per element type | C4 |
| `BoxingAnalyzer` | boxed instances per value type | C5 |
| `CollectionAnalyzer` | capacity waste, entry counts per collection type | C3 |
| `FinalizableObjectAnalyzer` | finalizer-queue instances per type | B6 |
| `LohFragmentationAnalyzer` | LOH bytes per type (`LohTypeProfile`) | B4 |
| `GCHandleAnalyzer` | pinned/strong handle targets per type | B7 |
| `WeakReferenceAnalyzer` | live/dead weak targets per type | B8 |
| `LeakCandidateAnalyzer` | suspicion score per type | A1 |
| `StaticRootLeakDetector` | static-field retention per type | A6 |
| `AsyncStateMachineAnalyzer` | state-machine instances, Gen2 fraction per type | E2 |
| `HeapTopologyAnalyzer` | segment distribution per type | B3 |
| `AllocationPatternAnalyzer` | allocation-rate proxy per type | B2 |
| `ReferenceChainAnalyzer` | retained type sets | A4 |

**That is the answer to "each analyzer seems to be showing its own thing."** Fourteen analyzers hold a column of the same table, and each prints its column as a standalone top-N in its own section, because there is no table to put it in.

### 3.5 Same pattern on threads

`ThreadStackScanDispatcher` shares the stack walk across 4 analyzers — then `ThreadDomainResult`, `HangDomainResult`, `LockGraphDomainResult`, `ThreadStackClusterDomainResult` and `AsyncTaskDomainResult` each publish their own thread projection, and there is no per-thread join at all (no thread equivalent of `TypeSystemSectionBuilder`). A reader asking "what is thread 0x1a4c doing" reads four sections and joins by eye.

---

## 4. Proposal: contributed columns over a shared row set

> Analyzers should **contribute columns to a shared entity table**, not publish private top-N tables.

### 4.1 Preserve identity through the publish boundary

Additive, non-breaking (all new members optional):

```csharp
public sealed record TypeSnapshot(
    string TypeName,
    int    Count,                      // keep for compat; see LongCount below
    ulong  TotalBytes,
    ulong  LohBytes,
    ulong  AverageSize = 0,
    ulong  EstimatedRetainedBytes = 0,
    ulong  SampleAddress = 0,
    string? ModuleName = null,
    long   Gen2Count = 0,
    ulong  MethodTable = 0,            // NEW — 0 = unknown
    int    ModuleId = -1,              // NEW
    long   LongCount = 0);             // NEW — non-narrowing count
```

Same treatment for the other per-type records. `MethodTable` is already in hand at every construction site — the analyzers read it out of `TypeAggregateIndexEntry` and then don't carry it forward.

Introduce one canonical key so the five field names converge:

```csharp
public readonly record struct TypeKey(ulong MethodTable, string TypeName)
{
    // MT when known (exact, module/ALC-safe); normalized name otherwise.
    public bool IsExact => MethodTable != 0;
}
```

Same for `ThreadKey(uint OsId, int ManagedId)` and `ModuleKey(string Name, Guid Mvid)`.

### 4.2 One shared row set, chosen once

The reason every analyzer truncates is memory discipline, and that constraint is correct — nothing here proposes materializing all types. The fix is to **choose the row set once, globally, instead of N times, locally.**

After Phase 1, `TypeAggregates` is already on disk and MT-keyed. Select the report's canonical row set from it in one pass:

```
Rows = top K types by max(shallow%, LOH%, Gen2%)          // K ≈ 200
     ∪ every type referenced by any Critical/Warning finding
     ∪ every type any analyzer explicitly nominates as salient
```

Bounded, deterministic, computed before the analyzers publish. Each analyzer then fills its column **for those rows** rather than sorting and truncating its own.

This is *cheaper* than today — N per-analyzer sorts collapse to one selection — and lossless for the rows that matter, which is the only losslessness that was ever on offer.

### 4.3 The contribution API

```csharp
public interface ITypeFactContributor
{
    string Analyzer { get; }
    IReadOnlyList<TypeFactColumn> Columns { get; }        // schema: id, label, unit, semantics
    void Contribute(TypeFactSink sink);                    // sink.Set(typeKey, columnId, value, provenance)
}

public readonly record struct FactProvenance(
    bool   Measured,          // false = estimated/heuristic
    long   SampledOf,         // 0 = full population
    long   PopulationTotal,
    string? CapNote = null);
```

`FactProvenance` is the piece that makes a merged table honest. Today a value from a 50k sample of 3.2M strings renders identically to a fully-enumerated count. In the merged table each cell knows whether it was measured or estimated, and over what coverage — which is what lets the renderer mark it, and lets a consumer decide whether two cells are actually comparable.

Analyzers keep their existing domain results unchanged. `ITypeFactContributor` is a second, additive surface — implement it incrementally, one analyzer at a time, with no coordinated migration.

### 4.4 What the merged table gives you

A single **Type Fact Table** — the union of ~20 analyzers' columns over ~200 shared rows:

```
Type                    Cnt    Shallow  Retained  Gen2%  LOH     Strings  ArrWaste  Boxed  Pinned  Fin  Leak
MyApp.SessionCache     1,204    212 MB    4.2 GB✻  99.4%    —     1.1 GB~      —       —      12    —   0.94
System.Byte[]        892,441    3.1 GB       —      12%   2.9 GB      —     840 MB     —   1,203    —     —
System.String        3.2 M      2.4 GB       —      88%      —    2.4 GB✓      —       —       4    —   0.31

✻ bounded BFS, lower bound   ~ from 50k sample of 3.2M   ✓ fully measured
```

Every number in that table exists in the report today. It is spread across nine sections, and no two of the columns can currently be placed on the same row with confidence.

Direct consequences:

- **"Everything known about type X" becomes a query,** not a nine-section reading exercise. This is the subject-dossier idea from the companion doc, but obtained for free from the data model rather than bolted on.
- **Cross-analyzer disagreement becomes computable.** `LeakCandidate: 2.1 GB retained` vs `Dominator: 340 MB retained` on the same `TypeKey` with comparable units is a real signal — it tells you the retention estimate is unreliable *for that type*. Today it is two numbers in two sections that nothing compares.
- **Per-analyzer sections keep their content** and gain a link to the row. Nothing is discarded; the redundant top-N framing is what goes.
- **`InsightEngine` gets a far better input.** Its 31 rules currently pattern-match on scalar fields of individual domain results. Given a joined fact table they can express per-type conditions across analyzers directly, instead of the current cross-result field plumbing.
- **Trend mode gets a stable join key.** Comparing `TypeKey` across snapshots is sound; comparing display names across snapshots is what happens today.

### 4.5 Same shape for threads

`ThreadFactTable` keyed by `ThreadKey`, columns contributed by `ThreadAnalyzer` (state, wait reason, stack depth), `HangAnalyzer` (hang score, top frame), `LockGraphAnalyzer` (held/awaited sync blocks, cycle membership), `ThreadStackClusterAnalyzer` (cluster id, signature), `AsyncTaskAnalyzer` (pending tasks attributed to the thread). The shared stack walk already exists; only the publish boundary needs the same treatment.

This is where the payoff is largest, because there is no thread join at all today.

---

## 5. Phasing

Each phase ships independently and is useful alone.

### Phase 1 — Carry identity *(small, mechanical, unblocks everything)*
- Add `MethodTable` / `ModuleId` / `LongCount` to `TypeSnapshot` and the other per-type records (optional params, no breaks).
- Populate at construction sites — the values are already in scope from `TypeAggregateIndexEntry`.
- Introduce `TypeKey` / `ThreadKey` / `ModuleKey`.
- **Immediate win with no further work:** `TypeSystemSectionBuilder.FindShape`/`FindGeneration` become MT dictionary lookups instead of O(n·m) `Ordinal` string scans, and the Method Table column stops rendering `"N/A"`.

### Phase 2 — Shared row set
- `TypeRowSetSelector` over the on-disk `TypeAggregates` after Phase 1 completes; bounded, deterministic.
- Publish the row set on `AnalysisContext` so analyzers can see it.

### Phase 3 — Fact table + first contributors
- `TypeFactTable`, `TypeFactColumn`, `FactProvenance`, `ITypeFactContributor`.
- Wire the 6 analyzers already in `TypeSystemSectionBuilder.SourceAnalyzers` as contributors — behaviour-preserving, so it doubles as the correctness harness.
- `TypeSystemSectionBuilder` renders from the fact table instead of hand-joining.

### Phase 4 — Remaining contributors
- Add the 14 analyzers from §3.4, highest-value first: String, LOH, Array, Collection, GCHandle, Finalizable, LeakCandidate, StaticRoot.
- Their own sections keep their detail views and link to the shared row.

### Phase 5 — Thread fact table
- Same shape, 5 contributors.

### Phase 6 — Consumers
- Per-type / per-thread dossier views.
- Cross-analyzer value-disagreement detection.
- `InsightEngine` rules over the fact table.
- Trend joins on `TypeKey`.

Phases 1–2 are prerequisites. 3 onward are incremental and individually shippable.

---

## 6. Constraints this respects

Checked against `CLAUDE.md` core philosophy:

- **No full materialization.** Row set is bounded (K ≈ 200) and selected from an on-disk index that already exists. No heap enumeration is added.
- **One pass.** Row selection is a single pass over `TypeAggregateIndex.bin`; contribution happens during work analyzers already do.
- **`ulong` address / MT identity** — that is precisely the point of the change.
- **Fewer allocations, not more.** N per-analyzer top-N sorts and N private list allocations collapse into one shared row set and one column array per contributor.
- **No LINQ in hot paths.** Contribution is `sink.Set(key, columnId, value)` on a pre-sized MT-keyed dictionary.
- **Schema-additive.** All new record members are optional with defaults; `SchemaVersion` bump only when the fact table itself is serialized (Phase 3).

---

## 7. Summary

| | Today | Proposed |
|---|---|---|
| Shared acquisition | ✅ `HeapIndexScanDispatcher` (11), `ThreadStackScanDispatcher` (4) | unchanged |
| Shared per-type store | ✅ `TypeAggregateIndexEntry`, MT-keyed, on disk | unchanged |
| Identity at publish boundary | ❌ dropped — 21 record types, all `string TypeName`, 5 field names | `TypeKey` (MT + name) carried through |
| Row-set selection | each analyzer picks its own top-N | one bounded set, chosen once, globally |
| Cross-analyzer join | string match over intersected top-Ns; `"N/A"` MT column | MT dictionary lookup over a shared row set |
| Analyzers measuring types | ~20 | ~20 |
| Analyzers in the type join | 6 | ~20 |
| Missing-vs-zero | both render `—` | `FactProvenance` distinguishes them |
| Sampling visibility | invisible at point of use | per-cell coverage |
| Thread join | none | `ThreadFactTable` |

The instinct in the question is right, and the architecture is already most of the way there — the shared scan and the MT-keyed aggregate index are the hard parts and they are built. What is missing is that analyzers dissolve that shared structure back into private, name-keyed, independently-truncated tables at the moment they publish. Carry the MethodTable across that boundary and pick the row set once, and "solidified data from multiple sources" stops being something the reader has to do by eye.

# DuplicateObjectAnalyzer — Design Sketch

> Priority: **P2/P3 boundary** — high feasibility and no version-layout risk, but lower business
> priority than the DI/EF/Cache/NativeInterop suite. Ships when the P2 analyzers are complete
> and capacity allows, or earlier if a specific user-request elevates its priority.
>
> Feasibility: **High**. Pure heap-scan + structural field-value hashing, no internal-BCL-type
> coupling whatsoever. `StringAnalyzer`'s hash-based dedup is the direct model.
>
> Effort: **S–M** (~1–1.5 wk). Main design cost is the bounded hash-bucket index (no `ToList()`
> on the hash accumulator) and defining what "structurally equal" means for each value-type family.

---

## 1. Problem statement

`StringAnalyzer` already detects duplicate string instances (same character content, multiple heap
addresses) and reports wasted bytes. No analyzer applies this same idea to non-string value-type
instances: structs, records, and reference-type objects where all fields have equivalent values.

The failure mode is typically:
- Large arrays of deduplicated data (e.g. configuration objects, enum-derived label structs,
  coordinate points) represented as independent heap instances that could be shared or interned.
- BCL value types boxed redundantly (e.g. thousands of `KeyValuePair<string, int>` with identical
  keys and values stored as separate boxed objects).
- User-defined record types (`record class`) that override `Equals`/`GetHashCode` but are
  allocated redundantly rather than being cached.

---

## 2. Scope: what counts as "duplicate"

Structural equality for this analyzer means: **all primitive and reference fields at the same
offsets have the same values** (primitive fields compared by bit-equality; reference fields
compared by the address they point to, not by recursive structural equality of the referee).
This is intentionally shallow — recursive equality would be unboundedly expensive on the heap.

### 2.1 Eligible type families

| Family | Eligible? | Notes |
|--------|-----------|-------|
| Value types (structs) — boxed | Yes | Common duplication source; boxed structs have a fixed field layout |
| `record class` types | Yes | Compiler-generated `Equals`/`GetHashCode` means the app expects value equality — duplicate instances are meaningful |
| Regular reference types with stable field layouts | Yes, with caveats | Must have at least one "interesting" non-address field (e.g. primitive or interned string) to produce a useful hash |
| `string` | **No** — already handled by `StringAnalyzer` | Deduplication of `string` is `StringAnalyzer`'s domain; do not re-report |
| Collection types (`List<T>`, `Dictionary<…>`, arrays) | **No** — length/content comparison is expensive | Skip types whose name matches `CollectionAnalyzer`'s patterns |
| Types with finalizers | **No** — structural equality on finalizable objects is semantically undefined | Skip types with `ClrType.IsFinalizeSuppressed` / finalizer presence |
| Types with mutable reference fields that appear equal by address but differ over time | Accepted false positive | Document as a known limitation |

### 2.2 Minimum instance threshold

Only types with at least `MinDuplicateCandidateCount` instances in the heap (default: 10) are
eligible for structural hashing. Types with fewer instances have negligible savings potential and
would dominate the type-enumeration scan for no output benefit.

---

## 3. Scan design

### 3.1 Heap-scan approach: `IHeapIndexScanParticipant`

`DuplicateObjectAnalyzer` joins the shared dispatcher pass.

**`BeforeHeapIndexScan`**: from `TypeAggregates`, filter to MTs that:
- Have `Count >= MinDuplicateCandidateCount`.
- Are not `string`, collection types, or finalizable types.
- Are a struct (boxed) or a record class (check for compiler-generated `EqualityContract` property
  or `<Clone>$` method presence via `ClrType.Methods`).

Build an `EligibleMtSet` (a `HashSet<ulong>`). This is the candidate filter for `OnHeapEntry`.

**`OnHeapEntry`**: for entries whose MT is in `EligibleMtSet`, add the address to a per-MT
**streaming hash accumulator** (see §3.2). Do not allocate per-object; update bounded accumulators.

**`AnalyzeAsync`** (post-scan):
1. For each eligible MT, extract the top duplicate groups from its hash accumulator.
2. For groups with `DuplicateCount >= MinDuplicatesInGroup` (default: 2), compute estimated waste:
   `(DuplicateCount - 1) × InstanceSize`.
3. Sort groups by estimated waste descending.
4. For top-K groups: read the actual field values from one representative instance for the report.
5. Return `DuplicateObjectDomainResult`.

### 3.2 Streaming hash accumulator — bounded memory design

The core challenge: compute a structural hash per object on the hot path without retaining every
address in memory. The design mirrors `StringAnalyzer`'s approach but with a structural hash
instead of a string-content hash.

**Per-MT bounded accumulator:**

```csharp
// Fixed capacity per MT — evict lowest-count bucket when cap is hit.
// Uses the same Space-Saving / Misra-Gries approach as DominatorAnalyzer's FanInSketch.
internal sealed class StructuralHashSketch
{
    private readonly Dictionary<ulong, int> _hashBucketCounts;  // hash → occurrence count
    private readonly int _capacity;   // e.g. MaxHashBucketsPerType = 1000

    // Increments the bucket for this structural hash.
    // When full, evicts the minimum-count bucket and starts the new hash at the evicted count.
    public void Record(ulong structuralHash, ulong address);

    // Returns groups with count >= minCount, sorted by count desc.
    public IEnumerable<(ulong Hash, int Count)> GetDuplicateGroups(int minCount);
}
```

One `StructuralHashSketch` per eligible MT, allocated lazily in `BeforeHeapIndexScan`.

**Global MT accumulator cap**: at most `MaxEligibleMtCount` MTs are tracked (default: 200);
if the eligible set exceeds this, sort by `TypeAggregates.TotalSize` descending and take the top
`MaxEligibleMtCount`.

### 3.3 Structural hash computation

On the hot path, read the first `MaxFieldBytesToHash` bytes (default: 128) of the object's field
data as a flat byte span via `ClrObject.ReadValueTypeField` or direct memory read, and compute a
fast non-cryptographic hash (xxHash64 or FNV-1a). This gives a structural fingerprint without
field-by-field decomposition.

```csharp
// Read raw field bytes from the heap for the object at entry.Address.
// FieldDataOffset = sizeof(void*) for the MT pointer prepended by the runtime.
Span<byte> fieldBytes = stackalloc byte[MaxFieldBytesToHash];
if (!context.DataReader.ReadMemory(entry.Address + FieldDataOffset, fieldBytes, out _))
    return;   // unreadable — skip

ulong hash = XxHash64.Hash(fieldBytes);
_sketches[entry.MethodTable].Record(hash, entry.Address);
```

No per-object heap allocation. The `stackalloc` is inside the hot path; `MaxFieldBytesToHash`
must be small enough to keep this safe (128 bytes → safe for most structs, and field-rich records
will be partially hashed — which is still useful as a collision filter).

---

## 4. Domain result and output model

```
DuplicateObjectDomainResult : AnalyzerDomainResult
  TotalEligibleTypeCount          int
  TotalDuplicateGroupCount        int
  TotalEstimatedWastedBytes       ulong
  ScanCapped                      bool
  TopDuplicateGroups              List<DuplicateGroup>

DuplicateGroup
  TypeName                        string
  InstanceSize                    ulong
  DuplicateCount                  int            // total occurrences with this structural hash
  EstimatedWastedBytes            ulong          // (DuplicateCount - 1) × InstanceSize
  IsApproximate                   bool           // true if the hash sketch was capped (hash collision possible)
  SampleFieldSummary              string?        // human-readable summary of the representative instance's fields
```

---

## 5. Infrastructure reuse

| Need | Existing infrastructure |
|------|------------------------|
| Eligible MT filtering | `TypeAggregates` (count, size, MT) from `HeapAnalysisCache` |
| Type-name exclusion (strings, collections) | `TypeNamePatternMatcher.HasAnyPrefix` / `ContainsAny` |
| Finalizer check | `ClrType.IsFinalizeSuppressed` / finalizer presence via ClrMD |
| Streaming heavy-hitter approach | Misra-Gries pattern (same as `DominatorAnalyzer`'s `FanInSketch`, §2 of dominator-analyzer.md) |
| Representative field read for report | `ClrObject` field reads via ClrMD, post-scan (top-K only) |

---

## 6. Registration fan-out

| Artifact | Class name |
|----------|-----------|
| Domain result | `DuplicateObjectDomainResult` |
| Finding generator | `DuplicateObjectFindingGenerator : IFindingGenerator<DuplicateObjectDomainResult>` |
| Trend comparer | `DuplicateObjectTrendComparer` — delta on `TotalEstimatedWastedBytes`, `TotalDuplicateGroupCount` |
| Section builder | `DuplicateObjectSectionBuilder : ISectionBuilder<DuplicateObjectDomainResult>` |

---

## 7. Scan caps

```
MinDuplicateCandidateCount       10     // minimum instances for a type to enter the eligible set
MaxEligibleMtCount              200     // max concurrent StructuralHashSketch instances
MaxHashBucketsPerType          1000     // capacity of each StructuralHashSketch
MaxFieldBytesToHash             128     // bytes hashed per object on the hot path
MinDuplicatesInGroup              2     // minimum duplicate count for a group to be reported
MaxGroupsToReport                50     // top-K groups in the domain result
```

---

## 8. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Hash collisions produce false-positive duplicate reports | Set `IsApproximate = true` when the sketch was at capacity (evictions occurred); mention in section builder that results are approximate |
| Reference fields that are coincidentally at the same address in two separate instances but point to different logical values | Document as a known limitation — shallow field-byte comparison is explicitly shallow |
| `stackalloc` of `MaxFieldBytesToHash` bytes in hot path stack overflow for deeply nested calls | 128 bytes is well within stack limits even in constrained contexts; but guard against very large `MaxFieldBytesToHash` values in config |
| Eligible type count explosion on generic-heavy codebases | `MaxEligibleMtCount` cap; select top by `TotalSize` from TypeAggregates to maximise waste-detection value |
| Record types with mutable state (not true value types) | Accept as false positive; user-visible `IsApproximate` flag covers the case |

---

## 9. Relationship to `StringAnalyzer`

`StringAnalyzer` is the reference implementation for the "hash-based dedup" pattern, but its
hash is content-based (string character span), not field-byte-based. The design above is
independently correct for non-string types. The section builder should cross-link to
`StringAnalyzer`'s findings for completeness ("for string deduplication, see the Strings section").

---

## 10. What this analyzer does NOT do

- Deep/recursive structural equality (unboundedly expensive).
- Detect logically equivalent but differently-typed values (e.g. two objects representing the
  same concept via different types).
- Suggest or apply automatic deduplication at runtime (analysis only).
- Analyse `string` instances (that is `StringAnalyzer`'s exclusive domain).
- Report on array contents — only the boxed value-type or record instances are hashed, not their
  element arrays.

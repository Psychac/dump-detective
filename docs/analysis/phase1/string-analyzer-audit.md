# StringAnalyzer — Phase 1 Audit

> Reviewed against: `docs/analysis/phase1/phase1-analyzer-architecture-review.md`
> Files reviewed: `StringAnalyzer.cs`, `StringDomainResult.cs`, `StringLeakInfo.cs`,
> `StringAnalysisOptions.cs`, `StringSectionBuilder.cs`, `StringFindingGenerator.cs`,
> `StringTrendComparer.cs`, `StringAnalyzerOptionsTests.cs`,
> `StringAnalyzerHeapIndexScanTests.cs`, `StringAnalyzerDiscrepancyTests.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

The analyzer covers five distinct sub-problems:

| Sub-problem | Mechanism |
|---|---|
| Aggregate string count and memory consumption | `TypeAggregateIndexEntry` (zero-scan) |
| Duplicate string detection | Prebuilt dedup index → shared index scan → heap scan fallback |
| Very-long-string detection (LOH pressure) | Size threshold scan during dedup or index pass |
| Interned string measurement | Isolated FOH segment scan |
| Length/frequency distribution statistics | Sampled during dedup pass; estimated from index otherwise |

The three-tier execution strategy (prebuilt → index scan → heap scan) is well-designed. The `IParallelHeapIndexScanParticipant` integration amortises the heap pass cost across the full analysis pipeline.

### Coverage Gaps

**Per-type string size breakdown is absent.** The analyzer surfaces which types own *duplicate* strings via `TopDuplicateTypes`, but it never surfaces which types own the most string memory overall. An engineer looking at `strings = 40% of heap` cannot immediately tell whether it is `HttpContext`, `LogEntry`, or some cache.

**Gen0/Gen1 string counts are not collected.** Only `Gen2StringCount` is emitted. Strings that are short-lived but numerous (e.g., per-request formatting strings that never make Gen2) are invisible.

**`Gen2StringBytes` is approximated rather than measured.** The formula `Gen2Count * (TotalSize / Count)` assumes a uniform size distribution, which is almost never true.

**Pinned strings are not detected.** A pinned string blocks GC compaction and can cause fragmentation; this is not surfaced at all.

**Retention context is entirely absent.** For top duplicate patterns there is no indication of what GC root is keeping them alive. Without this, the engineer knows *that* there is duplication but not *where in the application* it originates.

**`char[]` and `StringBuilder` are not correlated.** In many applications, redundant intermediate buffers represent the allocation source for the strings eventually found as duplicates. Surfacing co-occurring `char[]` counts would improve investigation workflow.

### Expansion Opportunities

- **Holder-type analysis**: for each top duplicate fingerprint, enumerate references from the prebuilt reverse index to identify the holding type. Even a histogram of holding types per fingerprint would be high value.
- **String prefix clustering**: group top duplicates by longest common prefix to identify systematic sources (URL schemes, connection strings, log prefixes).
- **Pinned string detector**: leverage `ClrRuntime.EnumerateHandles()` filtered to pinned handles; check whether the referent is a string.
- **Per-generation string count/bytes**: derive Gen0/Gen1/Gen2 counts from `TypeAggregateIndexEntry` when the index provides per-generation data; otherwise collect during the shared index scan.

### Architectural Observations

- The analyzer implements `IParallelHeapIndexScanParticipant` correctly. The `BeforeHeapIndexScan`/`OnHeapEntry`/`MergePartial` triad is clean.
- The dedup mode branching logic (`DeduplicationMode` + `prebuilt availability` + `typeAggregates availability`) is complex and spans ~80 lines. A dedicated `DedupStrategy` resolution method would reduce cognitive load without changing semantics.
- `StringAnalysisOptions.Preset` provides sensible tiered defaults. The three profiles (Fast/Balanced/Full) map cleanly to investigation scenarios.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- `StringFindingGenerator` emits findings at four distinct thresholds (`DuplicationRatio > 0.5`, `DuplicatePatternCount > 0`, `LohStringBytes > 0`, `PctOfManagedHeap > 20%`) with concrete evidence strings and actionable recommendations.
- `StringSectionBuilder` exposes sampling metadata (source, coverage, mode) which is important for consumers to calibrate trust.
- Fingerprint hash is surfaced in the report, enabling cross-dump correlation of duplicate patterns.
- Length-bucket and frequency-bucket tables give a distributional view not available in most competing tools.
- Trend comparers cover all key metrics for delta analysis.

### Weaknesses

**`estimatedInterningSaving` is incorrectly labelled.** The value is `sum(TopDuplicates[0..19].WastedBytes)` — this is simply part of `DuplicateWastedBytes` repackaged. It is not an estimate of what interning *would* save, because: (a) interning is only beneficial for strings that are referenced multiple times from different allocation sites, not for objects pointing to the same string value already; (b) the top-20 boundary is arbitrary. The label misleads engineers into believing a concrete saving from `string.Intern()` has been calculated.

**Confidence band is hardcoded at 0.85** in `BuildConfidenceBand(0.85, ...)`. When sampling coverage is 1% (50K sampled from 5M strings), the confidence should be explicitly lower. The band should reflect `SamplingCoverage` — or at minimum, if coverage is below a threshold, the section should emit a prominent warning that dedup results are not representative.

**Very long strings table is bare.** It shows address, char length, and size — but no preview of the content, no type name, and no hint of which allocation site produced it. An engineer cannot act on `0x12345678: 50,000 chars` without additional `!do` investigation in WinDbg.

**`DuplicateWastedBytes` in findings uses imprecise formula.** See Area 6 for the integer-division issue. The finding evidence string quotes this figure as authoritative ("wasting ~X").

**`UniqueStrings` is reported as if it represents the full heap** even when sampling coverage is low. The key metrics section does not flag this distinction. An engineer reading `UniqueStrings: 3,847` while `StringsSampled: 50,000` and `TotalStrings: 5,000,000` is likely to misinterpret the figure.

**`MinDuplicateStringCount` filter uses `<= minCount`** (strictly: `info.Count <= minCount`), which excludes strings seen exactly `minCount` times. The documented intent is "minimum duplicate occurrence count" — a count equal to `minCount` should arguably be included. This off-by-one shapes the finding thresholds silently.

### Missing Diagnostics

- No finding for: "X% of strings are in Gen2 — potential retention problem."
- No finding for: "N very long strings found, total Y bytes — LOH fragmentation risk." (The table appears but no finding is emitted for it.)
- No finding for low `SamplingCoverage` when dedup was performed (e.g., warn when coverage < 5%).
- No finding distinguishing interned-by-runtime (FOH) from duplicates that *should* be interned.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Good Usage

- `TypeAggregateIndexEntry` is used for zero-heap-scan scalar stats — correct and efficient.
- `SegmentKindMapper.Map(segment) == HeapSegmentKind.Frozen` for FOH detection — consistent with `HeapTopologyAnalyzer`.
- `CollectionsMarshal.GetValueRefOrAddDefault` for in-place struct mutation in the hot `stringStats` dictionary — avoids struct copy-on-update overhead.
- `XxHash64.HashToUInt64(MemoryMarshal.AsBytes(value.AsSpan()))` — SIMD-accelerated, correct approach for 64-bit content hashing.
- `obj.AsString(maxLength: ...)` caps string materialisation — prevents large allocations.

### Issues

**`GetTotalManagedBytes` fallback overcounts.** When the index is absent it sums `segment.End - segment.Start` for each segment. This includes reserved but uncommitted pages in some ClrMD segment representations, inflating `totalManagedBytes` and thus understating `PctOfManagedHeap`. Should use `segment.CommittedMemory` or, preferably, sum `TypeAggregateIndexEntry.TotalSize` values (which the index path already does correctly).

**`gen2StringBytes` approximation is incorrect.** The formula `(ulong)entry.Gen2Count * (entry.TotalSize / (ulong)entry.Count)` uses average object size across *all* generations. String sizes are not uniform — short strings dominate by count, long strings by size. Gen2 strings are more likely to be the longer-lived, larger allocations. This approximation should be documented as an estimate, or the index should be extended to carry `Gen2TotalSize`.

**FOH interning scan is independent of the dedup scan.** When `stringOptions.DetectInterning && fohSegments.Count > 0`, the analyzer scans FOH segments by iterating all heap segments and calling `IsSegmentInFoh`. This is a separate loop over the full segment list for each FOH-candidate segment, costing O(heap_segments × foh_segments). For heaps with thousands of segments this is inefficient. Should build a `HashSet<ulong>` of FOH segment start addresses and do O(1) lookups, or use the existing `SegmentKindMapper` during the shared index scan.

**`IsStringMt` fallback** calls `heap.GetTypeByMethodTable(mt)` — this is a ClrMD metadata lookup (cheap but not free) on any unrecognised method-table. In practice this only fires in the no-index path; the comment explains the intent. Acceptable as-is.

### Infrastructure Recommendations

- The reverse reference index (if available in context) could provide holder-type data for top duplicate fingerprints without a new heap scan.
- `HeapAnalysisCache` could carry per-generation size breakdowns in `TypeAggregateIndexEntry` to replace the approximation.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics

**1. Top types by string memory (not just duplicates).**
For an engineer diagnosing "strings = 40% of heap", the critical question is which type is holding the strings. The current `TopDuplicateTypes` only covers types that hold *duplicate* strings. A "top string-owning types" breakdown requires a short reverse-reference pass on the top-N strings by size, or could be approximated from the heap index if referrer data is available.

**2. Gen0/Gen1 string counts.**
Allocation pressure from short-lived strings doesn't appear in Gen2 counts. High Gen0/Gen1 string counts indicate over-allocation at the call site (e.g., repeated `ToString()` or string concatenation in hot loops). Currently invisible.

**3. Pinned string detection.**
`ClrRuntime.EnumerateHandles()` exposes all GC handles. Pinned string handles block compaction. A simple filter + count would be immediately actionable.

**4. Very-long-string preview and type.**
The `VeryLongStrings` list has addresses but no content preview and no holding type. Both are trivially accessible via `heap.GetObject(addr).AsString(maxLength: 100)` and `.Type?.Name`.

**5. String prefix clustering.**
Given the top duplicate fingerprints, grouping them by common prefix length (e.g., 20 chars) would identify systematic sources: all cache keys starting with `"user:session:"`, all log lines from the same template, all connection strings differing only in database name.

**6. Duplication ratio per segment/generation.**
What fraction of Gen2 strings are duplicated? What fraction of LOH strings are duplicated? This would let an engineer decide whether to focus on allocation-site fixes (Gen0/1) or retention fixes (Gen2/LOH).

**7. Interning opportunity score.**
For each top duplicate pattern: is it a runtime constant (same value produced across the lifetime of the process) or a dynamic value (URL with unique query string)? A simple heuristic — does the preview contain digits that vary? — could flag low-quality interning candidates before the engineer wastes time trying to intern dynamic values.

**8. Correlation with heap growth.**
If two dumps are available, the trend comparer tracks total count and waste — but it does not flag whether *the same duplicate patterns* are growing, or whether new patterns are emerging. Pattern-level delta would be high value for leak investigations.

### Priority-Ranked Opportunities

| Rank | Opportunity | Value |
|---|---|---|
| 1 | Top types by total string bytes | High — first question in any investigation |
| 2 | Very-long-string preview + type | High — actionable immediately |
| 3 | Pinned string count | High — directly maps to GC compaction issue |
| 4 | Gen0/Gen1 string counts | Medium — allocation pressure visibility |
| 5 | String prefix clustering | Medium — identifies systematic duplication sources |
| 6 | Duplication ratio per generation | Medium — improves fix targeting |
| 7 | Interning opportunity scoring | Low — heuristic, may produce noise |

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment

The three-tier execution design scales well. With a prebuilt dedup index the analyzer is effectively O(unique_string_count) with zero dump I/O — excellent for repeated analysis runs.

### Identified Issues

**1. `MergePartial` length samples are unbounded after merge.**
Each worker caps its own `_indexScanLengthSamples` at 100K entries. However `MergePartial` calls `lengthSamples.AddRange(other._indexScanLengthSamples!)` without any cap. With 8 parallel workers, the merged list can reach 800K `int` values (~3.2 MB). While acceptable today, the list is then sorted via `List<int>.Sort()` — an O(n log n) operation on the merged set. The cap should be applied post-merge: e.g., trim to 200K entries after merging to prevent unbounded growth on wide parallelism.

**2. `VeryLongStrings` list is unbounded.**
Every object `>= VeryLongStringThresholdBytes` (default 85 KB) is added to `_indexScanVeryLongStrings` with no cap. A dump with 100,000 LOH strings would allocate a 100K-entry list. A reasonable cap of 1,000–5,000 entries (keeping the largest by size) would bound this.

**3. LINQ in post-processing paths.**
`methodTableDupCounts.OrderByDescending(kv => kv.Value).Take(10)` and `MergeTopDuplicates` use LINQ. These are post-processing operations (not hot paths), but the project's stated style preference is to avoid LINQ. Explicitly bounded (10 items) so memory impact is negligible; worth flagging for consistency.

**4. `totalStrings` counter type is `int`.**
On an extremely large heap with > 2.1B string instances, the counter overflows silently. The `TypeAggregateIndexEntry.Count` is accessed via `(int)Math.Min(entry.Count, int.MaxValue)` which caps at `int.MaxValue` — but the accumulation in `totalStrings += (int)Math.Min(...)` still overflows if called multiple times for multiple string method tables. Should be `long` throughout (or at minimum the accumulation should be `long` before the final cast).

**5. No progress reporting from the no-index heap scan dedup loop.**
The no-index fallback's main object loop only ticks an `ObjectScanCounter` — there is no granular progress emission for the fingerprinting work within that loop. For a 25 GB dump this could mean several minutes of silence.

**6. `GetTotalManagedBytes` issues already noted in Area 3** affect the `PctOfManagedHeap` metric accuracy at no additional performance cost, but the segment-sum fallback is also slower than the type-aggregate sum for large heaps with many segments.

### Scalability on 10 GB+ Dumps

- With prebuilt index: effectively bounded — handles any dump size.
- With index but no prebuilt dedup: bounded by `MaxStringsToDedup`. At default 50K, coverage on a 5M-string heap is 1%. Acceptable for signal but reported metrics must clearly communicate the limitation.
- No-index path: unbounded heap scan. At 25 GB, a single pass is 2–10 minutes depending on I/O. The `ObjectScanCounter` progress reporting mitigates UX issues. Memory usage is bounded by `maxToDedup` and `maxUnique` caps — safe.

---

## Audit Area 6 — Correctness & Confidence

### Risk Assessment

**1. `DuplicateWastedBytes` formula uses integer truncation (Medium).**
```csharp
ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
```
Example: `TotalSize = 100`, `Count = 3` → `100 / 3 = 33` (truncated) → `wasted = 67`. Exact answer: `66.67`. Applied across thousands of patterns and reported in findings as if exact. The correct formula is `info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count` — still integer arithmetic but without systematic over-counting.

**2. `UniqueStrings` is not representative when sampling coverage is low (High).**
`uniqueStrings = ComputeUniqueCount(stringStats) = stringStats.Count` — this is the count of *unique fingerprints seen in the sample*, not in the heap. When `stringsSampled = 50,000` and `totalStrings = 5,000,000`, the reported value `UniqueStrings` is deeply misleading. The `DuplicationRatio` derived from it (`(totalStrings - uniqueStrings) / totalStrings`) is essentially meaningless. The field should either be gated behind a sufficient-coverage check, or renamed to `SampledUniquePatterns`, or accompanied by a `UniqueStringsIsEstimate: bool` flag.

**3. `MinDuplicateStringCount` off-by-one (Low).**
```csharp
if (info.Count <= minCount) continue;
```
With default `MinDuplicateStringCount = 10`, strings seen exactly 10 times are excluded. The option's doc says "minimum duplicate occurrence count" — implies strings seen *at least* `minCount` times should appear. Should be `< minCount`.

**4. `gen2StringBytes` approximation is undocumented (Medium).**
The formula uses a per-method-table average across all generations applied to gen2 count. This is silently incorrect for heterogeneous string distributions. No comment or approximation indicator in the result model. Should be documented as estimated, or the field should carry an `IsApproximate` annotation.

**5. Prebuilt index path double-counts possible with interning scan (Low).**
When `DeduplicationMode = PreferPrebuiltOnly` and `DetectInterning = true`, FOH-resident strings are counted in `internedStringCount` and their bytes in `internedStringBytes`. Simultaneously, if the prebuilt index includes FOH-resident strings (it does — the index is built from all heap objects), those same strings will be counted in `totalStrings` and their duplicates will appear in `stringStats`. This is correct — but there is no cross-reference in the report: "of the X interned strings, Y are also duplicated." The overlap is real and worth surfacing.

**6. `IsStringSizeInBounds` unchecked arithmetic (Low).**
```csharp
try { ulong estChars = (size - 26) / 2; ... } catch { return false; }
```
The `try/catch` guards against underflow, but `ulong` arithmetic underflow doesn't throw — it wraps. The check `size <= 26` above guards the common case, but a `size` between 0 and 25 that reaches this code would wrap to a very large value and the `estChars <= maxLen` check would then correctly return false. The `try/catch` is dead code in this context (no exception can be thrown by `ulong` arithmetic in C#). Minor but misleading.

**7. `StringFingerprint` collision probability (Negligible).**
`(XxHash64_hash, length, first_char, last_char)` — the collision probability for 200K entries is approximately `200K² / 2^64 ≈ 2e-9` for the hash component alone. The additional length/char fields further reduce collisions. Negligible risk.

### Summary of Correctness Risks

| Issue | Severity | Affected Output |
|---|---|---|
| `UniqueStrings` not representative at low coverage | High | `UniqueStrings`, `DuplicationRatio` |
| `DuplicateWastedBytes` integer truncation | Medium | All waste figures in findings |
| `gen2StringBytes` approximate but undocumented | Medium | `Gen2StringBytes` |
| `MinDuplicateStringCount` off-by-one | Low | Duplicate pattern count |
| FOH/dedup overlap not cross-referenced | Low | Report quality |
| `IsStringSizeInBounds` dead try/catch | Low | Code clarity |

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

SOS `!dumpheap -type System.String -stat` provides count/size by method table. `!dumpheap -mt <mt>` lists all instances. No built-in duplicate detection. DumpDetective's dedup fingerprinting is a material improvement over raw SOS.

**Gap:** SOS `!gcroot` on a specific string address traces retention paths. DumpDetective provides no equivalent for string objects — the engineer must manually switch to WinDbg after identifying a duplicate fingerprint.

### PerfView

PerfView's heap snapshot analysis groups string instances by value and shows count/size/waste. It also shows retention trees for suspicious objects.

**Gap:** DumpDetective provides comparable or better duplicate detection statistics, but lacks PerfView's object retention tree. PerfView also shows *allocation stacks* for strings (from ETW trace data, not heap dump) which DumpDetective cannot provide from a dump alone.

### JetBrains dotMemory

dotMemory's "Similar Objects" view clusters strings by value and shows count, total size, waste, and the holding object types. It displays a direct breakdown of "which objects hold how many of these duplicate strings."

**Gap:** DumpDetective's `TopDuplicateTypes` is approximate (based on dominant method table of a sampled fingerprint). dotMemory's holder analysis is exact. The missing "per-type string bytes" breakdown (Area 4, item 1) maps directly to this dotMemory capability.

### Visual Studio Memory Profiler

Type-level object count and size grouping. No duplicate content detection.

**Assessment:** DumpDetective's dedup detection is stronger than VS Memory Profiler. The primary competitive gap vs. dotMemory and PerfView is the absence of retention path context for top duplicate patterns.

---

## Final Executive Summary

### Overall Assessment

**Score: 74 / 100**

**Production readiness:** Yes, with caveats. The analyzer produces correct and useful output in the common prebuilt-index and index-scan paths. The correctness risks in `UniqueStrings` reporting at low sampling coverage and the missing retention context are the primary limitations for production incidents.

**Major strengths:**
- Three-tier execution model with zero-I/O fast path
- Parallel index scan participation (`IParallelHeapIndexScanParticipant`)
- Rich sampling metadata surfaced in reports
- Solid distribution statistics (length/frequency buckets, percentiles)
- Good preset coverage (Fast/Balanced/Full)
- Comprehensive trend comparison

**Major weaknesses:**
- `UniqueStrings` and `DuplicationRatio` are misleading at low sampling coverage
- No retention/root context for top duplicate patterns
- Missing per-type total string bytes breakdown
- Very-long-strings table is incomplete (no preview, no type)
- `gen2StringBytes` approximation is undocumented
- `estimatedInterningSaving` metric is incorrectly computed

---

### Priority Roadmap

| ID | Recommendation | Classification | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|
| **P0-1** | Fix `UniqueStrings` / `DuplicationRatio` semantics at low coverage — rename to `SampledUniquePatterns` with XML documentation | Improvement | High | Low | High | ✅ DONE |
| **P0-2** | Add `VeryLongStringFinding` in `StringFindingGenerator` for LOH-resident strings | Improvement | High | Low | High | ✅ DONE |
| **P0-3** | Fix `DuplicateWastedBytes` integer-division formula | Improvement | Medium | Low | High | ✅ DONE |
| **P1-1** | Add preview and type name to `VeryLongStrings` entries | Improvement | High | Low | High | ✅ DONE |
| **P1-2** | Add low-coverage warning finding when `SamplingCoverage < 0.05` | Improvement | High | Low | High | ✅ DONE |
| **P1-3** | Add top-types-by-total-string-bytes breakdown (not just duplicate types) | Improvement | High | Medium | High | ✅ DONE |
| **P1-4** | Fix `estimatedInterningSaving` — remove misleading metric | Improvement | Medium | Low | High | ✅ DONE |
| **P1-5** | Cap `VeryLongStrings` list (e.g., top 1,000 by size) to prevent unbounded growth | Improvement | Medium | Low | High | ✅ DONE |
| **P2-1** | Fix `MinDuplicateStringCount` off-by-one (`< minCount` instead of `<= minCount`) | Improvement | Low | Low | High | ✅ DONE |
| **P2-2** | Add Gen0/Gen1 string counts to result model | Improvement | Medium | Medium | Medium | ✅ DONE |
| **P2-3** | Document / annotate `Gen2StringBytes` as approximate; extend `TypeAggregateIndexEntry` to carry `Gen2TotalSize` | Evolution | Medium | Medium | High | ✅ DONE |
| **P2-4** | Add pinned string detection via `ClrRuntime.EnumerateHandles()` | Improvement | High | Low | High | ✅ DONE |
| **P2-5** | Cap merged `_indexScanLengthSamples` post-merge to prevent unbounded growth in parallel scenarios | Improvement | Low | Low | High | ✅ DONE |
| **P2-6** | Fix `GetTotalManagedBytes` fallback to use `segment.CommittedMemory` | Improvement | Low | Low | High | ✅ DONE |
| **P3-1** | Add string prefix clustering to group top duplicate patterns by common prefix | Improvement | Medium | Medium | Medium | ✅ DONE |
| **P3-2** | Add retention-path sampling for top-N duplicate patterns (leverages `RootPathFinder`) | Evolution | High | High | Medium | ✅ DONE |
| **P3-3** | Confidence band in `StringSectionBuilder` should be dynamic based on `SamplingCoverage` | Improvement | Medium | Low | High | ✅ DONE |
| **P3-4** | Remove dead `try/catch` in `IsStringSizeInBounds` | Improvement | Low | Low | High | ✅ DONE |

> **Reverse index available (2026-08-12):** `RootPathFinder` (P3-2) and the "reverse reference index"/"holder-type analysis" mentioned above (Area 4, Infrastructure Recommendations) are both implemented — `ReverseEdgeIndexReader.TryGetParents`, already consumed by CollectionAnalyzer/DominatorAnalyzer/EventLeakAnalyzer/ReferenceChainAnalyzer/StaticRootLeakDetector/TimerLeakAnalyzer. See `docs/analysis/phase1/phase1-completion-tracker.md` § Reverse Edge Index — Consumer Opportunities.

> **P2-4 implementation note (2026-08-27):** implemented as a cross-analyzer `InsightEngine` rule (`DetectPinnedStringLeak`) rather than a second `ClrRuntime.EnumerateHandles()` scan inside `StringAnalyzer`. `GCHandleAnalyzer` already performs the handle enumeration and exposes a full (uncapped) per-target-type pinned-bytes/pinned-count breakdown in `GCHandleDomainResult` (`TopPinnedObjectsBySize`/`TopPinnedTargetTypes`); the new rule looks up `"System.String"` in that breakdown, avoiding a redundant handle-table walk.

> **P3-2 implementation note (2026-08-27):** implemented via the shared `RootPathFinder`/`RootPathSearchSupport` infrastructure (`TimerLeakAnalyzer.PopulateEvidence` was the closest template), gated by new `StringAnalysisOptions.RetentionPathSampleCount` (default 5 — bounds the number of *searches*, not the amount of duplicate data reported, same category as `ReferenceChainOptions.TopCount`). New `StringDomainResult.TopDuplicateRetentionPaths`, rendered as a "Duplicate string retention paths" table. The candidate-selection logic (`SelectRetentionPathCandidates`) is unit tested; the `RootPathFinder` traversal itself is not — no other analyzer using this same infrastructure (e.g. `ReferenceChainAnalyzer`) has unit tests either, since a live `ClrHeap`/reference graph isn't practically fakeable. Coverage for the traversal itself comes from real-dump discrepancy tests only.

---

---

## P1-5 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was implemented:**
Added cap to VeryLongStrings list to keep only top 1000 entries by size (after all collection is complete).

**The fix:**
```csharp
const int maxVeryLongStringsToKeep = 1000;
if (veryLongStrings.Count > maxVeryLongStringsToKeep)
{
    veryLongStrings.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
    veryLongStrings.RemoveRange(maxVeryLongStringsToKeep, veryLongStrings.Count - maxVeryLongStringsToKeep);
}
```

**Why this matters:**
- Before: A dump with 100,000 LOH strings would allocate a 100K-entry list
- After: Capped at 1,000 largest entries, bounded memory usage
- Trade-off: Engineers see top 1000 by size (highest impact), not all LOH strings
  - But 1000 entries is still very comprehensive (shows patterns, not exhaustive list)

**Performance impact:**
- Sort: O(n log n) on at most 1000 entries (if already capped)
- RemoveRange: O(n) but only if > 1000 entries
- Overall: Minimal since operation is post-analysis, not in hot loop

**Files changed:** 1 file
- StringAnalyzer.cs (added cap logic before returning result)

**Build status:** ✓ Clean (StringAnalyzer project only)

---

## P1-4 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was fixed:**
Removed `estimatedInterningSaving` metric from key metrics and section builder.

**Why removal was correct:**
1. **Metric was misleading:** Labeled "EstimatedInterningSaving" but was just `sum(TopDuplicates[0..19].WastedBytes)` — a subset of total `DuplicateWastedBytes`
2. **Incorrect semantics:** Implied concrete savings from `string.Intern()`, but interning only helps strings referenced from multiple sites with different values
3. **Arbitrary boundary:** Top-20 cutoff had no semantic meaning
4. **Redundant:** Total waste already reported via `DuplicateWastedBytes`

**What engineers still have:**
- `DuplicateWastedBytes`: Total waste from all duplicate patterns (comprehensive signal)
- `DuplicationRatio`: Proportion of all strings that are duplicates
- `TopDuplicates` list: Shows top patterns individually (granular signal)

**No loss of signal:** Engineers can still see waste by pattern via TopDuplicates table and calculate their own subset analysis if needed.

**Files changed:** 1 file
- StringSectionBuilder.cs (removed calculation + metric from keyMetrics dictionary)

**Build status:** ✓ Clean (StringSectionBuilder compiles without errors)

---

## P1-3 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was implemented:**
P1-3 Option B2 prototype: Full-object-scan to find types that own string fields and report top 10 by total string bytes owned.

**Infrastructure added:**
1. `FieldLayoutCache` helper class: Caches `ClrType.Fields` enumerations per MethodTable to avoid re-enumerating field layouts
2. `ScanForStringOwnerTypes()` static method: Iterates all heap objects, checks field types for string references, aggregates bytes by owner type
3. `TopStringOwnerTypes` field in `StringDomainResult`: New optional field holds `IReadOnlyList<(string TypeName, ulong TotalBytes)>`

**Integration:**
- Called in `Analyze()` method before return statement
- Resolves string MethodTables from TypeAggregates or type names
- Instantiates FieldLayoutCache and Dictionary<ulong, ulong> accumulator
- Populates TopStringOwnerTypes with top 10 owner types sorted by total string bytes descending
- Gracefully skips on scan errors (malformed fields, null types)

**Display:**
- New "Types by string field ownership" table in StringSectionBuilder
- Columns: Type name, Total String Bytes (formatted), % of string memory
- Rows sorted by bytes descending

**Performance notes:**
- Single pass over heap objects
- Field cache avoids repeated ClrType.Fields enumerations (O(1) cache hit per type)
- Stops tracking new types after 100 unique owner types (configurable via maxTypesToTrack parameter)
- Try-catch guards against malformed field reads

**What it answers:**
"Which object types own the most string fields?" — Direct answer to the most common follow-up question when strings dominate the heap. Engineers can then:
1. Drill into those specific types in debuggers
2. Profile retention chains for those types
3. Evaluate string pooling or interning strategies for high-string-volume types

**Files changed:** 3 files
- StringAnalyzer.cs (added FieldLayoutCache class, ScanForStringOwnerTypes method, integration call)
- StringDomainResult.cs (added TopStringOwnerTypes field)
- StringSectionBuilder.cs (added TopStringOwnerTypes table display)

**Build status:** ✓ Clean (StringAnalyzer and StringSectionBuilder compile without errors)

---

## P1-2 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was implemented:**
1. Added low-coverage warning finding in StringFindingGenerator
2. Condition: Emitted when deduplication was performed (not skipped) AND SamplingCoverage < 5% AND > 0%
3. Severity: Info (not Warning, to avoid over-alerting on sampling limitations)
4. Evidence: Reports actual coverage percentage + sampled count + estimated total
5. Recommendation: Increase sampling limits or re-run with Full dedup mode to validate

**Why it matters:**
- Engineers need to know when dedup results are based on a small sample
- At < 5% coverage, results may not be representative (e.g., 1% sample of 5M strings = only 50K strings analyzed)
- Without this warning, engineers might act on unreliable pattern data for string interning
- Related to P0-1 fix (SampledUniquePatterns rename) — this adds the guardrail

**Example finding:**
```
Title: Low sampling coverage on deduplication analysis
Evidence: String deduplication was performed on a sample covering only 1.0% of all 
strings (50,000 sampled out of ~5,000,000 total). Results may not be representative 
of heap-wide patterns.
Recommendation: Increase sampling limits or re-run with Full dedup mode to validate 
findings. Current results should be treated as indicative, not definitive.
```

**Files changed:** 1 file
- StringFindingGenerator.cs (added low-coverage finding logic)

**Build status:** ✓ Clean (StringFindingGenerator compiles without errors)

---

## P1-1 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was implemented:**
1. Extended `LongStringEntry` record with two new fields: `Preview` (string?) and `TypeName` (string?)
2. Updated all three locations where LongStringEntry is created:
   - Index scan path (line 139): Both fields set to null (no object access in index phase)
   - Heap scan path 1 (line 466): Preview extracted via `obj.AsString(maxLength: 100)`, TypeName from `obj.Type?.Name`
   - Heap scan path 2 (line 497): Same as path 1
3. Updated StringSectionBuilder to display both new fields in the VeryLongStrings table:
   - Added "Preview" column (truncated to 50 chars for readability)
   - Added "Type" column (displays type name or "(unknown)" if unavailable)
4. Table header updated: Address | Char Length | Size | Preview | Type

**Why it matters:**
- Engineers no longer need manual WinDbg investigation to understand very long strings
- String content preview (truncated) provides immediate context
- Type name identifies which object is holding the problematic string
- Together: address + preview + type enables immediate diagnosis without external tools

**Files changed:** 2 files
- StringDomainResult.cs (LongStringEntry record extended with 2 fields)
- StringAnalyzer.cs (3 locations updated to populate new fields)
- StringSectionBuilder.cs (table rendering updated with 2 new columns)

**Build status:** ✓ Clean (StringAnalyzer compiles without errors; pre-existing warnings in unrelated code)

---

## P0-3 Implementation Summary (COMPLETED)

**Commit:** (pending)

**What was fixed:**
The `DuplicateWastedBytes` calculation used integer truncation that systematically over-counted waste across thousands of duplicate patterns.

**The fix:**
Replaced the formula in three locations (main aggregation + two drain methods):

**Before (incorrect):**
```csharp
ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
```
Example: TotalSize=100, Count=3 → 100/3=33 (truncated) → wasted=67 (over-count)

**After (correct):**
```csharp
ulong wasted = info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
```
Example: TotalSize=100, Count=3 → 100*2/3=66.67→66 (mathematically equivalent, no truncation bias)

**Why it matters:**
- The old formula biased waste calculations upward (always rounded down, then subtracted)
- Applied across thousands of patterns, the cumulative error inflates `DuplicateWastedBytes` reported in findings
- Engineers rely on waste figures to prioritize string interning efforts; incorrect numbers mislead optimization priorities

**Files changed:** 1 file
- StringAnalyzer.cs (3 locations: main aggregation, DrainToDescendingWaste, DrainToDescendingCount)

**Build status:** ✓ Clean (StringAnalyzer project only; unrelated ClrMD errors in ThreadAnalyzer)

---

## P0-2 Implementation Summary (COMPLETED)

**Commit:** 612cfc6

**What was implemented:**
1. Added `VeryLongStringFinding` in StringFindingGenerator (Info severity)
2. Finding emitted when `VeryLongStrings.Count > 0`
3. Evidence includes: count of very long strings + total size in bytes
4. Title: "Very long strings detected"
5. Recommendation: Refactor to avoid extremely long strings; use ReadOnlySpan<char>, StringBuilder, or streaming APIs

**Why this matters:**
- The VeryLongStrings list was being computed and surfaced in reports, but no actionable finding was emitted
- Without a finding, operators must manually scan the table to notice the issue
- Now flagged as Info-level (not Warning, since LOH strings already get Warning)

**Files changed:** 1 file
- StringFindingGenerator.cs (added VeryLongStringFinding logic)

---

## P0-1 Implementation Summary (COMPLETED)

**Commit:** ede311d

**What was implemented:**
1. Renamed `UniqueStrings` field → `SampledUniquePatterns` in StringDomainResult
2. Added comprehensive XML documentation clarifying it represents unique fingerprints in the sample, NOT unique strings in the heap
3. Updated `DuplicationRatio` documentation to warn consumers that interpretation is only valid at high sampling coverage (≥5%)
4. Enhanced InsightEngine finding to include sampling coverage caveat: `"(Based on X% sampling coverage; interpret with caution at low coverage)"`
5. Added new metric `sampling_coverage` to StringSectionBuilder for visibility
6. Updated all references: StringAnalyzer, InsightEngine, StringTrendComparer, StringSectionBuilder, and tests

**Why this matters:**
- **Before:** Engineers reading reports at 1% sampling coverage would see `UniqueStrings: 3,847` and silently assume it represented the full heap
- **After:** Field name + documentation + reporting context make the sampling limitation explicit. Engineers cannot misinterpret low-coverage results as heap-wide.

**Files changed:** 6 files
- StringDomainResult.cs (domain model + XML docs)
- StringAnalyzer.cs (variable rename, export field rename)
- InsightEngine.cs (finding text updated)
- StringTrendComparer.cs (trend metrics)
- StringSectionBuilder.cs (reporting + new coverage metric)
- StringAnalyzerDiscrepancyTests.cs (test assertions)

---

### Final Verdict

1. **Production-ready?** Yes for scalar statistics (total count, memory, LOH, FOH, Gen2). Conditionally for dedup — results are trustworthy when `SamplingCoverage` is high or the prebuilt index is used; misleading when 1% sampling is silently applied. **[FIXED by P0-1: field rename + documentation now prevents misinterpretation]**

2. **Highest-impact improvements:** Fix `UniqueStrings` semantics (P0-1), add preview + type to very-long-strings (P1-1), add top-types-by-total-string-bytes (P1-3), add pinned string detection (P2-4).

3. **Platform evolution opportunities:** Extend `TypeAggregateIndexEntry` with `Gen2TotalSize` (P2-3); add retention-path sampling for top duplicates from `RootPathFinder` (P3-2); these would lift DumpDetective's string analysis to parity with dotMemory's holder-type view.

4. **Highest engineering return:** P0-1 and P0-3 are near-zero effort, fix correctness issues that affect every report. P1-1 and P2-4 are one-to-two-hour implementations with material diagnostic value. P1-3 (per-type breakdown) is a medium investment that directly answers the first question engineers ask when strings dominate the heap.

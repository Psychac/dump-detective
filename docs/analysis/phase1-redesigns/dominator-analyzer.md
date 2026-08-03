## Redesign from Scratch

> What would a ground-up rewrite look like, given the project's hard constraints:
> streaming-only heap traversal, no materialization, bounded memory, disk-backed indices,
> single allocation budget on the hot path?

The current implementation conflates a hot-path fan-in scan, a separately-sourced type-statistics
pass, and a post-scan BFS-based retention estimate that quietly disagrees with itself about what
"retained bytes" means. Every major weakness in this audit — `HeuristicOnly` always `true`,
inconsistent exclusivity semantics, admission-ordering bias, invisible `gen2Count` — traces back to
these three passes never having a shared data model. A clean design starts there.

---

### 1. One Hot-Path Pass Produces Everything the Scan Can Answer

Today `OnHeapEntry` only accumulates `_referenceCount`. Per-type `gen2Count`/`lohSize`/`count` are
sourced separately from `HeapAnalysisCache.GetOrBuildTypeStatistics` and `TypeAggregateIndexEntry`,
with a silent overwrite-if-present branch in `Analyze` that mixes data sources without any output
signal (Area 6, Issue 5). There is no reason the same per-entry callback that already reads
`entry.MethodTable` and `entry.Size` cannot also aggregate type statistics.

**Redesign: `OnHeapEntry` updates two bounded structures per call, nothing else.**

```csharp
private readonly struct TypeAggregate
{
    public ulong TotalSize;
    public ulong LohSize;
    public int   Gen2Count;
    public int   Count;
}

// keyed by MethodTable (ulong) — no string allocation on the hot path
private Dictionary<ulong, TypeAggregate>? _typeAggregates;   // capacity = expected distinct MTs (~few thousand)
private FanInSketch? _fanIn;                                  // see §2
```

`entry.Generation` (already persisted per-object per project memory — see
`HeapEntry.Generation`) drives `Gen2Count` directly; no second cache round-trip and no
`ClrObject`/`TypeAggregateIndexEntry` lookup is needed to know an object's generation. Type name
resolution (MT → string) happens exactly once per distinct MT, lazily, only when building the final
report for the top-K candidates — never during the scan. This removes the entire
"aggregates possibly null, fall back silently" branch: there is only one source of type statistics,
built in the same pass as the fan-in scan, always populated when the scan runs at all.

---

### 2. Replace the Reference-Count Dictionary With a Bounded Streaming Heavy-Hitters Sketch

Area 6, Issue 3 identifies a real bug: `AccumulateReference` admits addresses first-come,
first-served up to `MaxReferenceAddresses`, so once the cap is hit, later (higher-address / later
disk-index-order) objects can never be counted even if they are the true top fan-in hubs. This is a
systematic bias, not a random sample, and no amount of raising the cap fixes it — it only delays
where the bias kicks in.

**Redesign: Space-Saving (a.k.a. Misra–Gries with counters) streaming top-K algorithm.**

Space-Saving is the standard answer to "find approximate heavy hitters over a stream in O(K)
memory, with a provable error bound, no matter how long the stream runs or in what order items
arrive." It replaces the raw `Dictionary<ulong, int>` with a fixed-capacity structure:

```csharp
internal sealed class FanInSketch
{
    // fixed capacity, e.g. 4x TopHighlyReferencedObjectsToShow — never grows past this
    private readonly Dictionary<ulong, int> _slotIndex;   // address -> index into _counts
    private readonly ulong[] _addresses;
    private readonly int[]   _counts;
    private readonly int     _capacity;

    public void Increment(ulong address)
    {
        if (_slotIndex.TryGetValue(address, out int idx)) { _counts[idx]++; return; }
        if (_slotIndex.Count < _capacity) { /* insert new slot */ return; }

        // capacity reached: evict the *current minimum*, not "reject the newcomer".
        // The evicted slot's count becomes the newcomer's starting count (error-bounded
        // overestimate) — this is what removes the first-come admission bias: a truly
        // hot late-arriving object always displaces the current coldest tracked object.
        int minIdx = FindMinIndex();
        _addresses[minIdx] = address;
        _counts[minIdx] = _counts[minIdx] + 1; // inherited count + 1, standard Space-Saving update
        _slotIndex.Remove(_addresses[minIdx]);
        _slotIndex[address] = minIdx;
    }
}
```

Memory is `O(capacity)` regardless of heap size — bounded tighter than today's
1M-entry/20MB-per-worker dictionary, and the top-K it reports has a known worst-case overestimate
bound instead of an undocumented address-order bias. `FindMinIndex` is O(capacity) but capacity is
small (hundreds, not millions), so this stays cheap per increment. This directly retires the P3 item
"reservoir sampling / two-pass min-heap admission" from a research question into a known,
implementable algorithm.

---

### 3. One Retained-Bytes Model With Two Named, Non-Conflicting Numbers

Area 6, Issue 2 is the most damaging correctness bug: `PopulateRetainedBytes` shares one `visited`
HashSet across all highly-referenced objects (exclusive, first-owner-wins), while the top-K
dominator-type loop gives each type a fresh `HashSet<ulong>` (non-exclusive, can double-count the
same subgraph). Both numbers are reported as "Est. Retained" with no distinction.

**Redesign: never let one field name mean two different computations.**

```csharp
public readonly record struct RetainedEstimate(
    ulong ExclusiveBytes,   // shared visited-set walk: sum across all candidates ≤ total heap size
    ulong GrossBytes,       // fresh visited-set walk: upper bound, may overlap other candidates
    bool  WasCapped);       // true if BFS hit MaxBreadth or MaxDepth before exhausting the subgraph
```

Both are computed in a single combined BFS driver (§5) that walks all top-K roots together —
`ExclusiveBytes` comes for free from the shared-visited pass, `GrossBytes` from a second, cheap
per-root walk capped at the same breadth/depth. The section builder renders both columns labeled
plainly: "Exclusive (first-owner)" and "Gross (may overlap)". No more silent disagreement between
the highly-referenced-objects table and the dominator-types table.

`WasCapped` replaces the always-`true` `HeuristicOnly` flag (Area 6, Issue 1) with a value that is
actually sometimes `false` — on smaller heaps or generous limits, the walk genuinely completes.
Confidence becomes: `Complete` (not capped) / `ShallowEstimate` (capped) rather than a permanent,
unearnable 10-point section-builder penalty.

---

### 4. A Bounded True Dominator Computation Over the Candidate Closures Only

This is the single highest-value gap versus dotMemory/VS Memory Profiler (Area 7). A full
Lengauer-Tarjan dominator tree over 200M heap objects is not compatible with the streaming/bounded
philosophy — but computing one over the *union of the top-K candidates' bounded BFS closures* is.
The BFS walk in §3 already visits at most `topCount × maxBreadth` nodes (bounded, currently ≤20×10K).
That subgraph — not the full heap — is small enough to hold as a disposable adjacency list for the
duration of one candidate-set analysis, without violating the "no full object graph" rule because
its size is a fixed, configured cap, never heap-proportional.

**Redesign: `BoundedDominatorTree.Compute(roots, adjacency, cap)`.**

1. During the combined BFS walk (§3), record forward edges taken (`parent[child] = node`) into a
   `Dictionary<ulong, ulong>` sized to the same cap as `visited` — no extra pass, no extra allocation
   class, just one more write per BFS edge already being traversed.
2. Because BFS-discovery order already gives a valid reverse-postorder approximation for a
   single-root walk, a simplified iterative dominator algorithm (Cooper/Harvey/Kennedy's
   "engineering a fast dominator algorithm" — same result as Lengauer-Tarjan, simpler to implement
   iteratively, well suited to a bounded node set) can compute exact dominators over this closure in
   a few passes over the (small, bounded) node set.
3. Add a virtual super-root for the multi-root case (top-K candidates walked together) so a single
   dominator computation covers all candidates and can express "these five types are jointly
   dominated by this one static field."
4. Label the output honestly: **"Exact dominator tree over the analyzed subgraph"** — true and
   correct within the walked closure, not a claim about the whole heap. This gives engineers
   dotMemory-equivalent "Dominated Memory" grouping for the objects that matter (the top-K
   candidates), which is exactly the audit's Area 7 recommendation, without adopting an
   unbounded whole-heap graph algorithm.

This retires the P3 "investigate Lengauer-Tarjan" item from a speculative research spike into a
concrete, memory-bounded design.

---

### 5. Attribution Chains Come From the Same Parent Map, for Free

Area 4's "dominator chain detection" (A → B → C) and Area 2's "no root-path evidence in the main
dominator-type table" are both solved by the `parent[]` map built in §4. For any node in the walked
subgraph, the chain from that node up to its BFS root is a simple parent-pointer walk — no new scan,
no new data structure. The section builder renders a short chain (`A → B → C, 4.2 MB cumulative`)
next to each highly-referenced object and each dominator-type row, using the same map that already
exists for the dominator tree.

---

### 6. Shared-Subgraph Overlap as a Byproduct of the Combined Walk

Area 4's "cross-type retained overlap" metric — why do types A and B both score highly but neither
shows large exclusive retained bytes? — falls out of walking all top-K roots in one combined BFS
(§3) instead of independently per type. When a node is first claimed by root A but later reached
again from root B, record `(A, B) -> sharedBytes` in a small `Dictionary<(int, int), ulong>` keyed
by candidate index, not type name (bounded at `topCount²`, e.g. ≤400 entries for topCount=20). This
answers the audit's question directly: "A and B jointly retain 40 MB of shared subgraph" becomes a
first-class, cheap output instead of an unexplained coincidence between two independent BFS walks.

---

### 7. Fan-In Distribution Histogram, Same Pass, Zero Extra Cost

Area 4's histogram request (0–10 / 10–50 / 50–200 / 200+ buckets) is a single bucket increment
inside `FanInSketch.Increment` — the count is already being read to decide eviction. Track four
`int` counters alongside the sketch and expose them as `IReadOnlyList<HistogramBucket>` on the
result. No additional heap read, no additional pass.

---

### 8. Config-Driven Search Limits, No Hardcoded Constants

Area 3, Issue 3 flags `RootPathSearchLimits` (`MaxCandidateNodes: 5_000`, `MaxCandidateDepth: 8`,
`MaxRootExpansionDepth: 12`, `LargeFanoutThreshold: 100`) as inline constants in `PopulateEvidence`.

**Redesign: fold these into `RetentionOptions` with the current values as defaults.**

```csharp
public sealed record RetentionOptions
{
    ...
    public int EvidenceMaxCandidateNodes { get; init; } = 5_000;
    public int EvidenceMaxCandidateDepth { get; init; } = 8;
    public int EvidenceMaxRootExpansionDepth { get; init; } = 12;
    public int EvidenceLargeFanoutThreshold { get; init; } = 100;
}
```

Profile presets (Fast/Balanced/Full) can now tune evidence-search cost independently of the main
scan caps, addressing the same audit concern raised for `TopHighlyReferencedObjectsToShow`
(Area 2, Issue 2) — the section builder's hardcoded `.Take(20)` is deleted entirely and replaced
with the configured value at every one of the four table sites.

---

### 9. No Debug Logging, No Dead Code, Structured Diagnostics Instead

Area 5's 8 unconditional `Console.Error.WriteLine("[PERF]...")` calls and Area 3's dead
`methodTableHasRefs`/`MethodTableHasOutgoingRefs`/`TypeHasOutgoingRefs` fallback (only reachable
when `cache is null`, which never happens in production) are both deleted outright.

Per-phase timing, if still wanted, goes through the optional `ILogger<T>?` constructor parameter
documented in [docs/architecture.md § 14](../../architecture.md#14--observability), logged at
`Debug` level — silent by default, available on demand, never unconditional stderr noise on every
production run.

---

### 10. What the Redesigned Class Surface Looks Like

```csharp
public sealed class DominatorAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, IDisposable
{
    // Phase A/B combined: single hot-path pass, value types only
    private Dictionary<ulong, TypeAggregate>? _typeAggregates;   // MT -> aggregate, no strings
    private FanInSketch? _fanIn;                                 // bounded Space-Saving sketch
    private HistogramBuckets _fanInHistogram;                    // 4 ints, updated inline

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (!_cache!.MethodTableHasOutgoingRefs(_heap!, entry.MethodTable))
            return;

        AccumulateTypeAggregate(entry, _typeAggregates!);   // §1
        WalkOutgoingRefs(entry, _fanIn!, ref _fanInHistogram); // §2 + §7, no allocation
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken ct)
    {
        // Phase C: post-scan, top-K candidates only
        var candidates = RankCandidates(_typeAggregates!, options);         // §1, no cache overwrite branch
        var walk = CombinedBoundedWalk.Run(candidates, context.Heap, options.EvidenceLimits, ct);
        // walk exposes: ExclusiveBytes, GrossBytes, WasCapped, parent[] map, overlap map — §3-6

        var dominatorTree = BoundedDominatorTree.Compute(walk.Roots, walk.ParentMap);  // §4
        var evidence = EvidenceEnricher.Enrich(candidates, walk, dominatorTree, context, ct); // §5, §6

        return ValueTask.FromResult(BuildResult(candidates, walk, dominatorTree, evidence, _fanInHistogram).Stamp(this));
    }

    public void Dispose() { _typeAggregates = null; _fanIn = null; }
}
```

No `Console.Error.WriteLine`. No dead fallback branch. No ambiguous "retained bytes" field. No
permanently-`true` boolean. Every structure above has a fixed, configured capacity independent of
heap size.

---

### Summary of Key Design Decisions

| Decision | Current | Redesigned |
|---|---|---|
| Type statistics source | Separate `GetOrBuildTypeStatistics` + optional `TypeAggregateIndexEntry` overwrite | Single `_typeAggregates` built in the same hot-path pass, one source of truth |
| Fan-in tracking | `Dictionary<ulong,int>` capped at `MaxReferenceAddresses`, first-come admission | Space-Saving sketch, fixed capacity, evicts true minimum — no order bias |
| Retained bytes | One ambiguous field, computed two incompatible ways (shared vs. fresh visited-set) | `ExclusiveBytes` + `GrossBytes`, both from one combined walk, clearly labeled |
| Confidence flag | `HeuristicOnly`, hardcoded `true` everywhere | `WasCapped` bool, genuinely varies; `Complete`/`ShallowEstimate` tier |
| Dominator tree | None; BFS heuristic only | Exact dominator computation over the bounded top-K BFS closure (not the full heap) |
| Chain attribution | None | Free byproduct of the walk's `parent[]` map |
| Cross-type overlap | None; unexplained co-scoring | `(candidateA, candidateB) -> sharedBytes` map, bounded at `topCount²` |
| Fan-in histogram | None | 4 counters updated inline during the existing scan, zero extra cost |
| Evidence search limits | Hardcoded constants in `PopulateEvidence` | `RetentionOptions.Evidence*` fields, tunable per profile |
| Debug logging | 8 unconditional `Console.Error.WriteLine` | Optional `ILogger<T>?`, `Debug` level, silent by default |
| Dead code | `methodTableHasRefs` dict + two private statics (cache-null fallback) | Deleted — production always passes a non-null cache |
| Table `.Take(N)` | Hardcoded `20` in section builder | Respects `TopHighlyReferencedObjectsToShow` per profile, every table |

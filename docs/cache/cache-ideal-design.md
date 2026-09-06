# Cache Subsystem — Rebuild Plan

A from-zero redesign of the caching/indexing subsystem, derived from what the analysis layer
actually asks for, then measured against the two real reference dumps before anything was committed
to.

**Optimisation order, set by the user: (1) peak RAM, (2) wall clock, (3) disk size.** Where two
conflict the earlier wins. That order is *not* the one the shipped v5–v8 sequence used — it
optimised disk with a byte count as its only metric — and it is the single reason this plan reaches
a different answer.

**Status: designed and measured, not started.** All six gating measurements are closed (§7). Three
of them killed items that looked good on paper. Nothing below waits on further evidence except
where marked.

| | |
|---|---|
| **[M]** | Measured — this session, or from [runtime-rebalance](cache-redesign-runtime-rebalance.md) / [measurements](cache-redesign-measurements.md) |
| **[D]** | Derived — arithmetic over **[M]** inputs only |
| **[U]** | Unverified — stated as an assumption, not a fact |

> 🔴 **Unrelated open bug, read before touching roots or the dominator child index:**
> `StaticRootLeakDetector` has never produced a finding, and the fix is held unmerged. It voids
> format v7's justification. Full record: [backlog.md](backlog.md), first entry.

---

## 1. Bottom line

| Axis | Today **[M]** | After this plan | Change |
|---|---:|---:|---|
| **Cold peak RAM, 27.5 GB** | **12,976 MB** | **≈2,600 MB [D]** | **−80%** |
| Cold peak RAM, 3.3 GB | 4,132 MB | ≈1,400 MB **[D]** | −66% |
| Cold wall clock, 27.5 GB | 1,310.5 s | ≈940 s **[D]** | −28% |
| `cache.bin`, 27.5 GB | 2,418.1 MiB | 1,630.2 MiB **[D]** | −33% |
| `cache.bin`, 3.3 GB | 342.50 MiB | 248.3 MiB **[D]** | −27% |

**RAM is the whole point.** Disk moves modestly — v5–v8 already took most of it — and runtime moves
less than it first appeared, once every estimate was replaced by a measurement. The 5× RAM result is
the reason to do this work, and it is the axis nothing before this ever measured.

Why it matters concretely: the 27.5 GB dump currently peaks at 12.97 GB on a 15.7 GiB machine,
bottoming out at **356 MB available and paging 3.9 GB [M]**. It survives on the commit limit, not on
RAM.

---

## 2. Why the current build costs 12.97 GB

Three causes, all measured.

**2.1 — Object identity is answered three times, expensively.** There is no cheap "which object is
this address?" primitive, so three separate mechanisms exist:

| Mechanism | Where | Cost |
|---|---|---|
| `Dictionary<ulong,int>` | `ReachableGraphWalker.WalkWithCsr` | **2,325.9 MB [M]** at O = 87.1M |
| `Array.BinarySearch` over `ulong[R]` | `ReverseEdgeCsrBuilder.ResolveRow` | 0.43 GB + **~10.3 G random probes [D]** |
| segment table + 2-level search | `ObjectAddressLookup` | small — the only one that is right |

There are also two parallel identity *spaces* — object rows (87.1M) and reachable rows (58.3M) —
each with its own persisted address column.

**2.2 — The edge set is materialised four times.** Following one edge through a cold build **[M]**:
the scan writes it to hash-bucketed forward scratch; `SortForwardIndexBuckets` re-reads and sorts it
(28.6 s); the walk reads it back, resolves both endpoints through the 2.3 GB Dictionary, and
simultaneously streams it to a *second* scratch set (2.19 GB); `ReverseEdgeCsrBuilder` re-reads
that, resolves each endpoint with two binary searches, and rebuilds (39.6 s, 2.62 GB resident). The
walk also builds a complete in-memory reverse CSR of its own from the same edges.

**2.3 — No phase declares a memory budget.** `ReachableGraphWalker`'s doc comment says so outright;
`ReverseEdgeCsrBuilder` defends holding every bucket resident as "a size cap, not a memory-safety
requirement, at the scale measured so far" — where the scale measured so far was zero. Peak is an
emergent property, not a designed one. The walk alone is **~6 GB of the 13 GB peak [M]**.

---

## 3. The plan

### 3.1 Core rewrite — R1 → R2 → R3, one coherent change

Each is a prerequisite for the next; splitting them means building the walk twice.

**R1 — One identity.** The object's row in the address-ordered columnar table, established free
during the scan (it *is* the record index). `address → row` becomes a rank query over
`ObjectAddressBlockBases` — **0.65 MiB, already written to disk today** — plus one page of the
mmap'd delta column. The reachable-node space becomes a rank-select bitmap over object rows
(11 MiB) instead of a second persisted address column (222.99 MiB).

Preconditions, both verified **[M]**: the address column is strictly ascending on both dumps
(checked element-by-element, not inferred), and no capped-scan analyzer remains that depends on
segment-iteration order — all eight index consumers are full passes. `cache-architecture.md` §7
constraint 6 exists to protect a behaviour that no longer exists; sorting `heap.Segments` by `Start`
makes monotonicity a construction guarantee at zero observable change.

> Buys **−2,325.9 MB** and −223 MiB. Costs **+3.4 s** of walk time **[M, §7.2]**.

**R2 — Swizzle once, sort instead of search.** The scan emits `(parentRow:u32, childAddr:u64)` —
12 B, down from 16 B. Then:

```
Pass A: range-partition edges by childAddr (partition boundaries are ROW boundaries,
        taken from ObjectAddressBlockBases), sort each partition, merge-join against
        that partition's contiguous slice of the address column
        -> (parentRow, childRow), already grouped by child = the REVERSE CSR
Pass B: re-partition by parentRow, sort
        -> the FORWARD CSR
```

Two properties carry it. *Range* partitioning (not hash) means an address range is a contiguous row
range, so each partition merge-joins a contiguous 5.4 MiB slice — sequential on both sides. And a
sort by key **is** a CSR: group boundaries are run boundaries, so the degree/prefix-sum/cursor arrays
that cost 0.65 GB today are an artefact of not having sorted.

This is the technique the project already validated once — `DiskBackedObjectIndexWriter.cs:1176`
records Stage B replacing "one random-access binary search per node" with sort + sequential merge —
and then regressed away from in v8, per edge instead of per node.

> Deletes 39.6 s + 2.2 s + 28.6 s, 2.19 GB of scratch round-trip, 10.3 G probes, and 2.62 GB
> resident. Costs two external sorts; the CSR-construction half is **2.59 s for all 137M edges
> [M, §7.3]**.

**R3 — Semi-external walk.** Level-synchronous BFS over the mmap'd forward CSR keyed by object row.
Visited and frontier are bitmaps over rows (31 MB total, and the visited bitmap's 7.29 MB fits in
L3). Successors are an array slice — no hashing, no bucket seek, no parse. No edge capture: the CSR
is already on disk. The visited bitmap *is* the persisted reachability section.

Use a **plain FIFO frontier**. Sorting the frontier was measured and is a loss (§7.3). What makes
the disk-backed CSR viable is that freeing ~6 GB is what keeps its ~870 MiB resident in page cache.

> Removes all six of the walk's resident structures. Measured at **1.63 s mmap'd against the current
> phase's 213.7 s [M, §7.3]** — traversal was never the cost.

### 3.2 Independent items

None of these depend on the rewrite or on each other.

| # | Item | Worth | Evidence |
|---|---|---:|---|
| **O1** | Sort `heap.Segments` by `Start` at load | 0 bytes — makes R1's precondition a guarantee | §7.2 |
| **O2** | Delete `ObjectGenerations`; derive from `SegmentIndex` | **83.07 MiB [D]** | below |
| **O3** | `ReverseEdgeOffsets` → 1-byte degrees + 64-row checkpoints | **159.96 MiB [D]** | below |
| **O4** | Narrow `DominatorRetainedBytes` to 2 B | **332.59 MiB [M]** | §7.1 |
| **O5** | Overlap root enumeration with the heap scan | **≈50 s [D]** | §7.5 |
| **O8** | `PublisherRegistry` Pass 2a reads `ObjectTypeDictionary` | **5.28 s [M]**, zero disk | §7.4 |

**O2** — generation is `f(segment, address)`: `segment.Kind` under regions GC, or a range compare
against the segment's `Generation0/1/2` sub-ranges otherwise. That is exactly what
`DiskBackedObjectIndexWriter.cs:256-259` and `:2054-2057` already do. Storing a byte per object to
memoise a range compare over a 63-row table is 83.07 MiB of pure waste.

**O3** — a monotone `int32[R+1]` whose successive differences are almost all 0/1/2 (mean degree
**2.35 [M]**), stored at full width. Degrees at 1 B plus an absolute checkpoint every 64 rows is
55.64 + 6.95 = 62.59 MiB against 222.55. A lookup reads one checkpoint and sums ≤64 bytes — one
cache line — at 8,851 queries per run. Critically this keeps offsets *on disk*; rebuilding the
prefix sum in RAM would cost 222 MB resident, which is the priority order applied backwards.

**O4** — 69–75% of reachable rows are dominator-tree leaves, and the whole distribution fits 2 bytes
at a 0.19% escape rate. Straight reuse of the `NarrowColumnWidth` + `ColumnOverflowTable` path
`ObjectSizes` already runs. **No `retained − ownSize` subtraction** — measured to earn 0.05 MiB.

**O5** — the root phase is ~98% irreducible native DAC stack unwinding, which is the right kind of
work to overlap rather than optimise. But only ~25% of the overlap is achievable, so this is a
~4%-of-run item, not the ~15% it first looked like. Re-probe at 27.5 GB against the *real* scan
before building it (§7.5).

**O8** — Pass 2a walks all 87.1M objects to derive the set of distinct MethodTables, which is
already persisted as `ObjectTypeDictionary`. Verified identical on both dumps.

**O2, O3 and O4 are pure format changes worth 575.6 MiB together and must ride one version bump** —
measurements §10.2's batching rule, since each bump invalidates every cache on disk.

### 3.3 Sequencing

1. **O1, O2, O3, O4, O8** — independent, no rewrite dependency. O2+O3+O4 in one format bump.
2. **R1 → R2 → R3** — the core, in that order, as one change.
3. **O5** — only after its 27.5 GB re-probe.

---

## 4. What this plan deliberately does not do

| Rejected | Why |
|---|---|
| **`TypeId → rows` index** (was the largest proposed addition, +332 MiB) | Justified entirely by EventLeak's 127.3 s. Measured: **87.9% of that is per-type DAC metadata work no index can reach**; the index reaches 5.0%. §7.4 |
| **Reorder `StaticFieldResolver`'s filters** | The name filter *prunes* a costlier `StaticFields` walk. Both proposed variants measured **22× and 3× slower**. §7.5 |
| **Sort the BFS frontier** | 1.67× slower for 4.6% fewer page faults. §7.3 |
| **Separate `DataTarget` per workload**, to dodge DAC serialisation | Measured **worse than serial** (−8.2% overlap); doubles the dump mapping. §7.5 |
| **Seed the walk with static roots** | Measured no-op — reachable set byte-identical at 6,686,490. CoreCLR already roots statics transitively via pinned handles on the statics blobs. |
| **C.2 / the Part F variant** | Subsumed by R2. Part F costed it at −1.5 GB but concluded it must ship as a permanent second code path; under R2 the reverse CSR comes from the edge sort, so there is nothing to bypass. Don't build it if R1–R3 proceed. |
| **Compress the base object columns** | 22.2× penalty against the 10.49 GB/s zero-copy path **[M]**. |
| **v9 block compression, before the rewrite** | Pure disk lever — priority 3 — and it would be measured against a pipeline this plan replaces. |
| **Any cap or sample** | `MaxParentsPerChild`, top-K types, bounded scan windows. Removed from this project deliberately; every structure above is bounded by streaming, not truncation. |
| **Cluster the object table by type** | Would make type-filtered access a range scan, but destroys the address monotonicity R1 and R2 both rest on. |

---

## 5. Target container

27.5 GB dump. Everything not listed is unchanged.

| Section | Today | Target | Δ |
|---|---:|---:|---:|
| `ObjectAddresses` (4 B block-delta) | 332.28 | 332.28 | — |
| `ObjectTypeIds` (2 B) | 166.14 | 166.14 | — |
| `ObjectSizes` (2 B + escape) | 166.14 | 166.14 | — |
| `ObjectGenerations` | 83.07 | **0** | **−83.07** |
| reachable addresses → **bitmap + rank/select** | 222.99 | **10.70** | **−212.29** |
| `DominatorIdomRows` (4 B) | 222.55 | 222.55 | — |
| `DominatorRetainedBytes` (8 B → 2 B) | 445.10 | **112.51** | **−332.59** |
| `ReverseEdgeChildren` (4 B/edge) | 522.74 | 522.74 | — |
| `ReverseEdgeOffsets` → **degrees + checkpoints** | 222.55 | **62.59** | **−159.96** |
| satellites, dictionary, metadata | 34.55 | 34.55 | — |
| **Total** | **2,418.1** | **1,630.2** | **−787.9 (−33%)** |

`ReverseEdgeChildren` at 4 B/edge is the irreducible core before compression.

**Container mechanics stay exactly as they are** — a from-zero design reproduces them: 64-byte header
with content-addressed `DumpContentHash`, fixed-width TOC, atomic `.tmp` → rename, per-section
`XxHash32` computed *in flight* then verified lazily and memoised per session, mmap section views,
zero-copy streaming reads, optional sections degrade rather than fail, and a `SectionManifest`
distinguishing "not requested" from "write failed".

---

## 6. Target pipeline

```
Phase 0   load dump, resolve segments, SORT SEGMENTS BY START                      [O1]
Phase 1a  parallel heap scan  ─┐  per object in row order:
          (DOP = cores)        │    addr | typeId | size -> columnar scratch
                               │    (parentRow, childAddr) -> edge staging
                               │    per-type row histogram
Phase 1b  GC root enumeration ─┘  CONCURRENT with 1a                               [O5]
Phase 2   Pass A: range-partition by childAddr, sort, merge-join -> REVERSE CSR     [R2]
Phase 3   Pass B: re-partition by parentRow, sort -> FORWARD CSR                    [R2]
Phase 4   semi-external BFS over the forward CSR -> reachability bitmap             [R3]
Phase 5   LeafFolder + Lengauer-Tarjan over rank(bitmap) space
Phase 6   satellites, TypeAggregates last
```

**Phase memory budgets [D].** Phases are sequential, so peak is the max, not the sum.

| Phase | Budget |
|---|---:|
| Scan (DOP 8) | ~220 MB |
| Pass A / Pass B | ~256 MB |
| Reachability walk | ~64 MB |
| **Dominator (LeafFolder + LT)** | **~1,000–1,700 MB** |
| Retained rollup | ~470 MB |
| **Peak** | **≈1.7 GB + GC headroom ≈ 2,600 MB** |

The dominator stage becomes the floor and everything else becomes noise. That is the correct shape:
exact Lengauer–Tarjan over 58.3M nodes genuinely needs ~7 dense arrays; nothing else does.

**Projected cold build, 27.5 GB.**

| Phase | Today **[M]** | Target | Basis |
|---|---:|---:|---|
| Heap scan | 335.5 s | 335.5 s | DAC-bound, untouched |
| Root enumeration | 200.3 s | ~150 s | **[D]** ~25% overlaps (§7.5) |
| Forward bucket sort | 28.6 s | — | replaced by R2 |
| Pass A + Pass B | — | +10–40 s | CSR half **2.59 s [M]**; sort half **[U]** |
| Reachability walk | 213.7 s | **~2 s** | **[M]** §7.3 |
| Dominator metadata resolve | 46.7 s | ~5 s | **[D]** rows are indexed |
| Reverse CSR build + write | 50.3 s | ~15 s | **[D]** write only |
| Writer checksums | 35.9 s | 0 s | already shipped |
| EventLeak registry | 127.3 s | ~122 s | **[M]** only 5.3 s addressable |
| Report build | 81.4 s | 81.4 s | out of scope |
| Unattributed | ~190 s | ~190 s | |
| **Total** | **1,310.5 s** | **≈940 s** | **−28%** |

---

## 7. Evidence

Six gating measurements, all closed. Harnesses: `tools/AddressLookupBench`,
`tools/SemiExternalBfsBench`, `tools/ProfileRootPhase`, `tools/ProfileDacConcurrency`, plus an
offline numpy decoder for `cache.bin` columns. **Five of the six needed no dump load at all** — the
persisted containers carry enough to settle them.

**Workloads.** Machine: 8 cores, **15.7 GiB RAM**, .NET 10, Release.

| | 3.3 GB reference | 27.5 GB `21-04` |
|---|---:|---:|
| Objects (**O**) | 14,620,162 | 87,104,236 |
| Reachable nodes (**R**) | 6,686,490 | 58,339,936 |
| Edges (**E**) | 17,367,740 | 137,033,360 |
| Distinct types | 14,003 | 12,376 |
| GC segments | 8 | 63 |

### 7.1 `DominatorRetainedBytes` narrows to 2 bytes → **O4**

Decoded both columns offline and merge-joined the address columns to map reachable row → object row.

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Rows where `retained == ownSize` | 74.52% | 69.31% |
| Escape rate at 2 B / at 4 B | 0.0968% / 0.0000% | 0.1853% / 0.000010% |
| `NarrowColumnWidth.Choose` picks | **2 B** | **2 B** |
| Today → target | 51.01 → **12.83 MiB** | 445.10 → **112.51 MiB** |
| **Saved** | **38.19 MiB (74.9%)** | **332.59 MiB (74.7%)** |

The proposed `retained − ownSize` subtraction is unnecessary: narrowing the raw column costs
112.56 MiB against 112.51 for the difference. Leaves are small absolutely, not just relative to
themselves.

### 7.2 Rank lookup vs. `Dictionary<ulong,int>` → **R1 confirmed**

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| `Dictionary<ulong,int>` resident | 390.4 MB | **2,325.9 MB** (28.0 B/entry) |
| Two-level rank resident | 0.11 MB | **0.65 MB** (0.008 B/entry) |
| **Ratio** | 3,584× | **3,584×** |

| Probe order, 27.5 GB | Dictionary | Rank | Ratio |
|---|---:|---:|---:|
| sequential | 68.8 ns | **63.9 ns** | **0.93×** |
| edge (real reference locality) | 41.5 ns | 66.2 ns | 1.60× |
| random | 135.3 ns | 450.5 ns | 3.33× |

The scaling is the point: the Dictionary degrades as its table outgrows cache (44.8 → 68.8 ns
sequential, from 3.3 GB to 27.5 GB) while the rank index is flat, because its resident part is
0.65 MiB and stays in L2. Across E = 137M edges the realistic cost is **+3.4 s for −2.3 GB**.

Also verified here: the address column is **strictly ascending on both dumps**, element-by-element.

### 7.3 The walk is ~1 second, and sorting the frontier is a loss → **R2, R3**

Transposed the persisted reverse CSR into a forward CSR — which measures R2's counting-sort
primitive on the real edge set — then ran a full forward BFS from the graph's real sources.

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Counting sort → CSR | **0.35 s** | **2.59 s** |
| Resident (offsets + targets) | 117 MB | 968 MB |

| BFS arm (58,324,726 rows, 136,993,584 edges) | 3.3 GB | 27.5 GB | ns/edge | faults |
|---|---:|---:|---:|---:|
| in-memory CSR, FIFO | 0.10 s | **1.04 s** | 7.6 | 44,279 |
| mmap'd CSR, FIFO | 0.16 s | **1.63 s** | 11.9 | 228,830 |
| mmap'd CSR, **sorted** frontier | 0.29 s | **2.72 s** | 19.8 | 218,319 |

Against the current walk phase's **213.7 s**. The difference is not traversal — it is the 2.3 GB
Dictionary, the per-node loose-file parse, the 137M individually-locked `RecordEdge` calls and the
`ChunkedBuffer` appends. Sorting the frontier costs 1.67× for a 4.6% fault reduction, so it is out.

*Limit:* the temp CSR was written immediately before the mmap'd arms ran, so it was page-cache
resident. That is the regime R3 argues for, and **not** a cold-storage BFS.

### 7.4 EventLeak is per-type DAC work → **O7 rejected, O8 found**

`DD_PERF_EVENTLEAK_REGISTRY=1`, warm cache, that analyzer alone.

| Pass | 3.3 GB | 27.5 GB |
|---|---:|---:|
| **total** | 18.11 s | **105.66 s** |
| 1 — typedef walk + `DescribeStaticFields` | 8.47 s (46.8%) | 7.51 s (7.1%) |
| 2a — full index scan → distinct MethodTables | 1.51 s (8.3%) | **5.28 s (5.0%)** |
| 2b — `DescribeInstanceFields` | 8.13 s (44.9%) | **92.87 s (87.9%)** |

Pass 2b went from 8.13 s over **14,003** MethodTables to 92.87 s over **12,376** — *fewer* types,
11× slower. It is not per-type-count work: `DescribeInstanceFields` touches every field's `ClrType`,
and those are DAC metadata reads whose cost scales with dump size.

Pass 2a's answer is already on disk:

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| `ObjectTypeDictionary` entries | 14,003 | 12,376 |
| Distinct `TypeId`s actually used | 14,003 | 12,376 |
| Bytes read to derive it / bytes it occupies | 27.9 / 0.11 MiB | **166.1 / 0.09 MiB** |
| Amplification | 261× | **1,760×** |

*The general lesson, worth more than the item:* **"analyzer does a full scan" does not imply "the
scan is the cost."** Measure what the scan's consumer does per record before adding an access path.

### 7.5 The root phase → **O5 downgraded, O6 rejected**

Split into its two halves (`tools/ProfileRootPhase`):

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| `heap.EnumerateRoots()` | 12.11 s (87.6%) | **124.33 s (98.4%)** |
| `BuildMapByRootAddress` | 1.71 s (12.4%) | **1.99 s (1.6%)** |

The suspicion that the trailer's filter ordering was costly was structurally accurate and
quantitatively wrong: the typedef universe is 38,372 against 12,376 live types — 3.1×, not orders of
magnitude — and the whole thing is 1.99 s. Both proposed fixes measured *slower*, all three
producing byte-identical maps:

| Variant, 27.5 GB | Time |
|---|---:|
| current implementation | **0.03 s** |
| defer `type.Name` past the address test | 0.67 s (**22×**) |
| skip framework modules by name | 0.10 s (3×) |

The name filter is a **pruning step**, not overhead ahead of the real work: it cuts 38,222 resolved
types to 11,572 before `type.StaticFields` is enumerated.

Concurrency (`tools/ProfileDacConcurrency`, 3.3 GB, **n=5** medians; every arm on a fresh
`DataTarget`, because ClrMD caches root enumeration per runtime):

| | Overlap achieved | Range |
|---|---:|---:|
| DOP 8 | **25.4%** | 8.7 – 29.2% |
| DOP 4 | 32.0% | 2.2 – 52.3% |
| Split `DataTarget`s | **−8.2%** | −42.1 – 17.1% |

It is a lock, not the scheduler: at DOP 4 four cores sit idle and overlap barely moves. Roots run
1.54× slower while scanning; the scan runs 2.02× slower while roots enumerate. So O5 is worth
**≈50 s, not ≈200 s**.

*Two limits, both of which could move it upward:* the scan here is a proxy running 5.40 s where the
real scan phase is far heavier and plausibly holds the lock a smaller fraction of the time; and the
27.5 GB regime differs enough that 25% should not be assumed to transfer.

### 7.6 Where estimates were wrong

Recorded because the pattern matters more than the individual items.

| Original claim | Outcome |
|---|---|
| O4 saves ~222 MiB at 4 B, via subtraction | **Larger and simpler** — 332.59 MiB at 2 B, subtraction unnecessary |
| Walk Dictionary is "≈2.0 GB" | Understated — **2,325.9 MB** measured directly |
| Sorted frontier recovers locality | **Refuted** — 1.67× slower |
| EventLeak's 127.3 s is scan volume | **Refuted** — 87.9% is unreachable DAC work |
| Root trailer is a large share of 200.3 s | **Refuted** — 1.6%, and the proposed fix is 22× slower |
| Root overlap is worth ~200 s | **Downgraded 4×**, to ≈50 s |
| Pass A + Pass B cost +40–70 s | CSR half is 2.59 s; revised to +10–40 s |

Three of six gates were negative. The projected runtime fell from −48% to **−28%** as estimates
became measurements. **The RAM result never moved.**

---

## 8. Measurement protocol

Non-negotiable for anything on this page. Each of these produced a wrong number first.

- **Cold wall clock and peak private are comparable only within one alternating A/B session.**
  Ambient free memory moving 7,243 → 5,794 MB between sessions shifted peak private ~500 MB and wall
  clock ~14 s with *identical* allocation totals and GC counts **[M]**. Run `A, B, A, B` in one
  session; never compare against a figure recorded earlier.
- **ClrMD caches root enumeration per runtime.** A second `EnumerateRoots()` in the same process
  returns in ~0.02 s against ~6 s for the first. Every arm must reload the `DataTarget`. Ignoring
  this produced a meaningless "349% overlap" before it was caught.
- **`MemoryMappedViewAccessor.AcquirePointer` returns the granularity-aligned view base**, not the
  requested section offset. Add `PointerOffset`, or the decode is silently garbage.
- **Peak *private* bytes, not working set.** A run that memory-maps a multi-GB dump has a working set
  dominated by file-backed pages it never touched.
- **Keep measurement cost proportional.** Five of six gates cost nothing because the persisted
  `cache.bin` already held the answer. Don't spend a 22-minute rebuild on a question an offline
  histogram settles.

---

## 9. Open and out of scope

**Open — measure before building:**

- O5's 27.5 GB re-probe, against the real scan rather than the proxy (§7.5).
- Pass A/Pass B's external-sort half; only the CSR-construction half is measured **[U]**.
- Whether the other seven type-filtered scan sites justify anything. O8 may cover several for free.

**Out of scope, noted so it is not mistaken for an oversight:**

- **The dominator stage's ~1.0–1.7 GB is a real floor.** R3 removes the walk's resident structures,
  but anyone chasing "make large dumps comfortable" should read §7.3 item 2 of the dominator
  integration doc first: `DenseIdMap` was already tried and came back 2.6× slower with no peak win.
- **Report building (81.4 s)** and analyzer-internal costs other than EventLeak's registry.
- **The static-root bug** — see [backlog.md](backlog.md). A correctness defect rather than a cache
  design question, but it voids format v7's evidence and will change what the dominator child index
  is worth.

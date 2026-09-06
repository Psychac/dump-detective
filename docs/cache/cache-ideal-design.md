# Cache — Ideal Design, From Zero

A ground-up redesign of the caching/indexing subsystem, derived from what the analysis layer
actually asks for rather than from what exists on disk today. The current implementation is not
consulted until §6, which is where the ideal is diffed against it.

**Optimisation order, as set by the user and applied throughout: (1) peak RAM, (2) wall clock,
(3) disk size.** Where two of these conflict the earlier one wins. That order is not the one the
shipped design was optimised under — the entire v5–v8 sequence optimised disk with a byte count as
its only metric ([measurements](cache-redesign-measurements.md) §16 says so explicitly) — and it is
the single biggest reason a from-zero design lands somewhere different.

**Evidence labels.** Every quantitative claim carries one:

| | |
|---|---|
| **[M]** | Measured. Sourced from [runtime-rebalance](cache-redesign-runtime-rebalance.md) Part A-R/D, [measurements](cache-redesign-measurements.md), or a `cache.bin` TOC parsed for this document |
| **[D]** | Derived — arithmetic over **[M]** inputs. No new measurement, but no new assumption either |
| **[U]** | Unverified. A design claim that needs a measurement before it is spent |

**Reference workloads.** Two real dumps, both **[M]**:

| | 3.3 GB reference | 27.5 GB `21-04` |
|---|---:|---:|
| Objects (**O**) | 14,620,162 | 87,104,236 |
| Reachable nodes (**R**) | 6,686,490 | 58,339,936 |
| Edges (**E**) | 17,367,740 | 137,033,360 |
| Distinct types | 14,003 | 12,376 |
| GC segments | 8 | 63 |
| `cache.bin` today (v8) | 342.50 MiB | 2,418.1 MiB |
| Cold build today | 94.8 s | 1,310.5 s |
| **Cold peak private today** | **4,131.6 MB** | **12,976.3 MB** |
| Machine | 8 cores, **15.7 GiB RAM** | same |

---

## 1. What the cache is for

Not "what does it store" — what does the analysis layer *ask*. Enumerated from the real consumers,
not from the interface surface. Nine questions, in descending order of how much they cost:

| # | Question | Asked by | Volume per run |
|---|---|---|---|
| Q1 | Enumerate every object as `(address, type, size)` | dispatcher + 8 analyzers independently | ~20 full passes **[M]** |
| Q2 | Every object **of type T** | `EventLeakAnalyzer`, `PublisherRegistry`, `TimerLeak`, `WeakReference`, `AsyncStateMachine`, `ReferenceChain`, `Dominator`, `QueryEngine` | 8 type-filtered full passes **[M]** |
| Q3 | `address → (type, size)` point lookup | BFS frontier, handle records, reverse-index neighbours | thousands **[M]** |
| Q4 | Who points at this object? | `CollectionAnalyzer`, `IndexBackedBidirectionalSearch`, root-path search | 8,851 calls on the 3.3 GB dump **[M]** |
| Q5 | Is this object reachable from a root? | reachability provider | point queries |
| Q6 | What would freeing this object free? | `GCRootAnalyzer`, `DominatorAnalyzer`, `FinalizableObject`, `StaticRootLeak` | point queries |
| Q7 | The canonical GC root set, with static-field and thread attribution | `RootSetCache`, `ThreadAnalyzer` | 1 pass |
| Q8 | Per-type aggregate statistics | `StatisticsCache` and most report sections | 1 pass |
| Q9 | Small satellites — handles, tasks, LOH free blocks, string dedup | assorted | 1 pass each |

Two things fall out immediately, and both are structural.

**Q2 is the workload, not Q1.** Every one of the eight sites in Q2 is implemented as a Q1 full scan
with a `MethodTable` compare in the loop **[M — verified by reading all eight call sites]**. There is
no type-partitioned access path at all. The largest single analyzer cost in the whole run —
`EventLeakAnalyzer`'s publisher registry at **127.3 s, 9.7% of the 27.5 GB run [M]** — is one of
these.

**Q4/Q5/Q6 all key off a different identity space than Q1/Q2/Q3.** Q1–Q3 are per-*object*
(O = 87.1M). Q4–Q6 are per-*reachable-node* (R = 58.3M). Today those are two independently
materialised address universes. §2 says they should be one.

---

## 2. First principle: one identity, and it is free

Everything expensive in this subsystem is a consequence of *not* having a single cheap object
identity. There are currently three mechanisms answering "which object is this address?", each with
its own cost:

| Mechanism | Where | Cost |
|---|---|---|
| `Dictionary<ulong,int>` idMap | `ReachableGraphWalker.WalkWithCsr` | **≈2.0 GB resident at R = 58.3M [M, Part D.3]** |
| `Array.BinarySearch` over `ulong[R]` | `ReverseEdgeCsrBuilder.ResolveRow` | 0.43 GB resident + **~10.3 billion random probes [D, Part B.2]** |
| segment table + per-segment binary search | `ObjectAddressLookup` | small — it is the only one that is right |

### 2.1 The ordinal already exists

During the heap scan every object is written to the columnar file at a definite record index. **That
index is a dense, gapless, zero-cost object id.** Nothing needs to be built to obtain it.

For it to be *useful* as an identity, `row → address` must be monotone — otherwise `address → row`
is not a rank query. Three independent lines of evidence say it already is:

1. `ObjectAddresses` is stored as a 4-byte delta from a per-1024-record block base, and **any**
   descending step inside a block escapes to an overflow table. The 27.5 GB dump has
   **`ObjectAddressOverflow` = 0 records over 87,104,236 objects [M — TOC parse]**; the 3.3 GB dump
   has 16, consistent with blocks straddling a >4 GB LOH gap rather than with any descending step.
2. Segments are internally address-ascending, and `ClrHeap.Segments` is enumerated once into an
   array. Sorting that array by `Start` makes global monotonicity a *construction guarantee* instead
   of an observation — and on both reference dumps it changes nothing, because the order already is
   ascending.
3. `docs/cache/cache-architecture.md` §7 constraint 6 preserves segment-iteration order "because
   capped-scan analyzers depend on *which* objects populate a partial scan." **There are no
   capped-scan analyzers left [M — all 8 `EnumerateIndexedEntries*` consumers grepped; every one is
   a full pass, no `Take`, no scan cap].** The project deliberately removed every top-K/capped-sample
   pattern. Constraint 6's premise has expired; the constraint outlived it.

### 2.2 The address→row index is 0.65 MiB and already on disk

With a monotone address column, `address → row` is:

```
block = binarySearch(blockBases, address)              // 85,063 entries, 0.65 MiB, resident
row   = block*1024 + search(deltas[block], address)    // one 4 KiB page of the mmap'd column
```

`ObjectAddressBlockBases` is **0.65 MiB on the 27.5 GB dump [M — TOC parse]** and is already
written. It is a complete two-level index; nothing else is needed.

**This one substitution replaces a 2.0 GB hash table with 0.65 MiB plus one page touch [D].**
Whether it is also *faster* is **[U]** and matters: the Dictionary probe is a guaranteed
TLB+cache miss into a 2 GB table; the replacement is ~17 in-L2 comparisons plus one page. The prior
art cuts both ways — Part B.2 condemned binary search over a 664 MB `ulong[]`, but that search had
no resident top level, which is exactly the difference. **Measure before spending.**

### 2.3 The second identity space is a rank-select bitmap, not a second address column

Q4–Q6 need a dense `0..R` space (dominator/LT array sizes scale with it, so using `0..O` would
inflate them by 1.49×). Derive it instead of storing it:

```
reachable : bitmap over object rows        87,104,236 bits =  10.38 MiB
rank      : uint32 per 1024-bit superblock                     0.32 MiB
reachableRow(objRow) = rank(objRow)         O(1)
objRow(reachableRow) = select(reachableRow) O(1) with a sampled select index
```

**11 MiB, resident and persisted, replaces `DominatorReachableAddresses` + its block bases +
overflow = 222.99 MiB on disk [M] and the walk's `ulong[R]` reachable-address array = 0.43 GB in
RAM [M, Part D.2].** The bitmap *is* the reachability answer for Q5, so nothing is lost.

### 2.4 Consequence

| Structure | Today | Ideal |
|---|---|---|
| address → id, build path | `Dictionary<ulong,int>`, 2.0 GB **[M]** | mmap column + 0.65 MiB bases **[D]** |
| address → id, edge resolution | 10.3 G random binary-search probes **[D]** | sequential merge-join, §3.2 |
| address → id, read path | segment table + 2-level search **[M]** | same search, one level shallower |
| reachable-node identity | `ulong[R]` in RAM + 223 MiB on disk **[M]** | 11 MiB rank-select bitmap **[D]** |
| walk-local node identity | discovery-order ids, needs `addresses[]` 0.47 GB **[M]** | *does not exist* — object rows throughout |

---

## 3. First principle: swizzle once, sort instead of search

### 3.1 The edge set is written and re-read four times today

Following one edge through the current cold build **[M — code path + Part D.5 timings]**:

1. Heap scan emits `(parentAddr, childAddr)` — 16 B — to hash-bucketed forward scratch.
2. `SortForwardIndexBuckets` re-reads and sorts it — **28.6 s**.
3. The walk reads it back to get successors, resolves both endpoints through the 2.0 GB Dictionary,
   and *simultaneously* streams the same edge to `ReverseEdgeExtractor`, which writes it to a second
   set of hash-bucketed scratch files at 16 B/edge — **2.19 GB of scratch on the 27.5 GB dump [M]**.
4. `ReverseEdgeCsrBuilder` re-reads those, resolves each endpoint with two binary searches, and
   rebuilds — **39.6 s + 2.62 GB resident [M]**.

The walk also builds a complete in-memory reverse CSR of its own (`revOffsets`/`revTargets`) from
the identical edge set, which step 4 then duplicates on disk. Part B.3 already identified this;
the ideal design deletes the *reason* for it rather than bypassing it.

### 3.2 Ideal: two range-partitioned sorts, no search anywhere

The scan knows the parent's **row** (it is the record index being written). So emit
`(parentRow:u32, childAddr:u64)` — **12 B, down from 16 B [D]**. Then:

```
Pass A — swizzle + reverse CSR
  range-partition the edge file by childAddr into K partitions whose boundaries are
    row boundaries taken from ObjectAddressBlockBases (partition p covers rows [r_p, r_p+1))
  for each partition, in parallel:
    sort by childAddr                                    (in memory, bounded — see §4)
    merge-join against ObjectAddresses[r_p .. r_p+1)     (sequential, both sides sorted)
      -> (parentRow, childRow)
  the output is already grouped by childRow, ascending
  => REVERSE CSR falls out of the sort. No degree array, no prefix sum, no second pass.

Pass B — forward CSR
  range-partition the swizzled (parentRow, childRow) list by parentRow, sort, emit
  => FORWARD CSR falls out the same way.
```

Two properties do the work:

- **Range partitioning, not hash partitioning.** Because rows are address-ordered, an address range
  *is* a contiguous row range, so each partition merge-joins against a contiguous 5.4 MiB slice of
  the address column. Hash partitioning (what the current extractor does) scatters child addresses
  uniformly and makes this impossible — which is precisely why the current builder has to binary
  search instead.
- **A sort by key *is* a CSR.** Group boundaries are run boundaries in the sorted output. The
  degree/prefix-sum/cursor arrays that cost 0.65 GB in `ReverseEdgeCsrBuilder` **[M, Part D.2]** are
  an artefact of not having sorted.

This is the same technique the project already validated once and then regressed away from:
`DiskBackedObjectIndexWriter.cs:1176` records that Stage B replaced "one random-access binary search
per node" with a sort + sequential merge, and Part B.2 notes v8 reintroduced the abandoned pattern
per *edge*, at ~5× the volume. The ideal design applies the validated technique to the case that
needs it most.

**Cost [D]:** two external sorts of 137M records (1.6 GB and 1.1 GB). **Benefit [D]:** deletes the
39.6 s CSR build, the 2.2 s flush, the 2.19 GB reverse scratch round-trip, the 28.6 s forward bucket
sort, the 10.3 G binary-search probes, and the 2.62 GB of builder residency. Net wall clock is
**[U]** — sorting is not free — but every term it removes is measured and every term it adds is
sequential I/O.

---

## 4. First principle: every phase has a stated memory budget

The current build has none. `ReachableGraphWalker`'s own doc comment says so ("no memory budget is
enforced here"), and `ReverseEdgeCsrBuilder`'s remarks defend all-buckets residency as "a size cap,
not a memory-safety requirement, at the scale measured so far" — where, as Part B.1 observes, "the
scale measured so far was zero."

An ideal design makes the budget a parameter, and every phase either fits it or spills.

### 4.1 Ideal phase budget, 27.5 GB dump [D]

Phases are sequential, so peak is the max, not the sum.

| Phase | Resident structures | Budget |
|---|---|---:|
| Scan (DOP 8) | per-worker column buffers (100 MB **[M]**), K partition write buffers, string dedup (490,728 entries) | **~220 MB** |
| Pass A swizzle | 4 × (partition 25.7 MB + address slice 5.4 MB) + sort scratch | **~256 MB** |
| Pass B forward CSR | same shape, 8 B/edge | **~192 MB** |
| Reachability walk | visited bitmap 10.4 MB + two frontier bitmaps 20.8 MB; CSR is mmap'd | **~64 MB** |
| Dominator (LeafFolder + LT) | ~7 × `int[N]` over the folded reachable set; CSR mmap'd | **~1,000–1,700 MB** |
| Retained rollup | post-order accumulation, streamed | **~470 MB** |
| Section writes | pooled 64 KiB buffers | **~64 MB** |
| **Peak** | | **≈1.7 GB + GC headroom** |

Against **12,976 MB measured today [M]**, allowing 1.5× for GC headroom gives **≈2.6 GB, a ~5×
reduction [D]**. Even at 2× pessimism on the dominator term it is ~4 GB. **This is the headline
result of the whole exercise, and it is the axis the shipped design never measured.**

The dominator stage becomes the floor and everything else becomes noise. That is the correct shape:
exact Lengauer–Tarjan over 58.3M nodes genuinely needs ~7 dense arrays. Nothing else in the pipeline
does.

### 4.2 The walk stops being a memory consumer

Today the walk is **~6 GB of the 13 GB peak [M, Part D.3]**: `idMap` 2.0 GB, `edgeFrom`/`edgeTo`
1.1 GB, four CSR arrays 1.6 GB, `addresses`/`outDegree`/`isRoot` ~1.0 GB, sorted reachable set
0.43 GB.

In the ideal design the walk is a **semi-external level-synchronous BFS over the mmap'd forward CSR,
keyed by object row**:

- visited/frontier are bitmaps over rows (31 MB total, not 2 GB)
- successors are an array slice at `fwdTargets[fwdOffsets[r] .. fwdOffsets[r+1]]` — no hashing, no
  parsing, no bucket seek
- no edge capture: the CSR is already on disk, which is where it was going anyway
- the visited bitmap *is* the persisted reachability section (§2.3)

**Every one of the walk's six resident structures disappears [D].**

The one thing this trades away is locality: BFS touches CSR rows in frontier order, which is random.
Two mitigations, both standard and both suited to this data:

1. **Sort each frontier level before expanding it.** Rows are address-ordered and heap objects
   overwhelmingly reference nearby objects, so a sorted frontier walks the CSR in near-ascending
   order. This converts most random access into sequential.
2. **The freed memory pays for the page cache.** The forward CSR is ~870 MiB **[D]**. Not holding
   6 GB of walk state on a 15.7 GiB machine is exactly what lets those pages stay resident. The
   27.5 GB run currently bottoms out at **356 MB available and pages 3.9 GB [M]** — it has no page
   cache to give the mmap.

Whether (1) and (2) together keep the walk at or below its current 213.7 s is **[U]** and is the
single most important open measurement in this design.

---

## 5. First principle: the physical layout serves Q2, not just Q1

§1 established that eight of the nine hot consumers are type-filtered full scans, and that the
largest analyzer in the run (`EventLeakAnalyzer`, **127.3 s [M]**) is one of them.

The cost is not I/O. A full column pass is 700 MB at the measured **10.49 GB/s zero-copy scan rate
[M]** — 0.07 s. The cost is the per-record *work* each analyzer does, multiplied by 87.1M records,
eight times over, to reach a few thousand matches.

**Ideal: a persisted `TypeId → object rows` index.** Rows grouped by type, one `uint32` per object:

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Row lists | 55.8 MiB | **332.3 MiB [D]** |
| Type offsets | 0.06 MiB | 0.05 MiB |

That is real disk — the largest single addition in this design — and it buys Q2 the same asymptotic
change the reverse CSR bought Q4: `O(objects)` becomes `O(matches)`. Under the stated priority order
(RAM > runtime > disk) this trade is in-bounds, and it is the only lever available against the
127.3 s **[U — that the registry's cost is dominated by scan volume rather than by per-match work
has not been measured; one stopwatch settles it, and it gates the whole item]**.

It also composes: the index is built by a counting sort over the `TypeId` column during the same
pass that writes it, needs no address resolution, and needs no extra memory beyond one
`int[typeCount]` histogram (12,376 entries).

**Explicitly rejected: clustering the object table physically by type.** It would make Q2 a range
scan for free, but it destroys the address monotonicity that §2 and §3 are both built on. Address
order is load-bearing; type order is an index.

---

## 6. The ideal container

Sections, sized for the 27.5 GB dump, with the current file as the comparison (**[M]** for "today",
**[D]** for "ideal"):

| Section | Today | Ideal | Δ | Basis |
|---|---:|---:|---:|---|
| `ObjectAddresses` (4 B block-delta) | 332.28 | 332.28 | — | already right |
| `ObjectTypeIds` (2 B) | 166.14 | 166.14 | — | already right |
| `ObjectSizes` (2 B + escape) | 166.14 | 166.14 | — | already right |
| `ObjectGenerations` (1 B) | 83.07 | **0** | **−83.07** | §6.1 — derivable |
| `TypeRowIndex` (**new**) | 0 | **+332.28** | **+332.28** | §5 |
| `ReachableBitmap` + rank/select | 222.99 | **10.70** | **−212.29** | §2.3 |
| `DominatorIdomRows` (4 B) | 222.55 | 222.55 | — | |
| `DominatorRetainedBytes` (8 B) | 445.10 | **222.55** | **−222.55** | §6.2 |
| `ReverseEdgeChildren` (4 B/edge) | 522.74 | 522.74 | — | irreducible pre-compression |
| `ReverseEdgeOffsets` (4 B/row) | 222.55 | **62.59** | **−159.96** | §6.3 |
| Satellites, dictionary, metadata | 34.6 | 34.6 | — | |
| **Total** | **2,418.1** | **2,072.6** | **−345.5** | |
| *without the §5 type index* | | *1,740.3* | *−677.8* | |

Applying the same shape to the 3.3 GB dump: **342.50 → 314.3 MiB with the type index, 258.5 without
[D]**.

### 6.1 `ObjectGenerations` is 83 MiB of a pure function

Generation is `f(segment, address)` — `segment.Kind` under regions GC, or `segment.GetGeneration(addr)`
for classic ephemeral segments, which is itself a range compare against the segment's
`Generation0`/`Generation1`/`Generation2` sub-ranges **[M — that is exactly what
`DiskBackedObjectIndexWriter.cs:256-259` and `:2054-2057` do]**. A 63-row table of segment ranges
answers it in O(log segments). **Storing one byte per object to memoise a range compare is 83.07 MiB
of pure waste [D]**, and the table it needs is `SegmentIndex`, which already exists at 1.8 KB.

### 6.2 `DominatorRetainedBytes` should store `retained − ownSize`

445.10 MiB, the second-largest section, at a flat 8 B/row. For any node that is a leaf of the
dominator tree — and `LeafFolder` exists precisely because there are a great many — `retained` is
exactly `ownSize`, which is already stored 2 bytes wide in `ObjectSizes`. Storing the difference
makes those rows zero, and lets the column narrow to 4 B with an escape table like `ObjectSizes`
already has. The independent corroboration is the **measured 24.69× zstd ratio on this exact section
[M]** — that much redundancy is a distribution, not noise.

**[U]:** the escape rate. This is measurable *without loading a dump* — read the existing section
out of `cache.bin` and histogram it, the same method [measurements](cache-redesign-measurements.md)
§1–§2 used throughout. Until then, 222.55 MiB is an estimate, not a number.

### 6.3 `ReverseEdgeOffsets` should store degrees, not offsets

A monotone `int32[R+1]` whose successive differences are almost all 0, 1 or 2 (mean degree
E/R = **2.35 [M]**) is being stored at full width. Store **1-byte degrees with an escape, plus an
absolute checkpoint every 64 rows**:

```
degrees      58,339,936 × 1 B      = 55.64 MiB
checkpoints  58,339,936/64 × 8 B   =  6.95 MiB
                             total   62.59 MiB   (was 222.55)
```

Lookup becomes: read one checkpoint, sum ≤64 bytes — one cache line — then slice. **−159.96 MiB for
one cache-line scan per query, at 8,851 queries per run [M]. [D]**

Critically, this keeps the offsets **on disk**. The tempting alternative — store degrees, rebuild the
prefix sum in RAM at open time — costs 222 MB resident and would be the priority order applied
backwards.

### 6.4 What stays exactly as it is

The container mechanics are good and a from-zero design reproduces them:

- 64-byte header, magic, format version, content-addressed `DumpContentHash`, fixed-width TOC
- one file, atomic `.tmp` → rename, `.tmp` deleted on any failure
- per-section `XxHash32`, computed **in flight** while writing (never by re-reading — that cost
  35.9 s on the 27.5 GB dump before §E.1 fixed it **[M]**), verified lazily on first open,
  **memoised per session** (78.5% of per-run hashing eliminated **[M]**)
- mmap section views; zero-copy `Unsafe.ReadUnaligned` for streaming, bounds-checked accessor reads
  for point queries
- optional sections degrade, never fail; a `SectionManifest` distinguishes "not requested" from
  "write failed"
- **base object columns are never block-compressed** — the measured penalty is 22.2× on the
  10.49 GB/s streaming path **[M]**, and no I/O saving covers it

---

## 7. The ideal build pipeline

```
  ┌─ Phase 0 ── load dump, resolve segments, SORT SEGMENTS BY START ───┐
  │                                                                     │
  ├─ Phase 1a ── parallel heap scan (DOP = cores)  ───────┐             │
  │    per object, in row order:                          │  CONCURRENT │
  │      addr | typeId | size                             │  with 1b    │
  │      -> columnar scratch (row = record index)         │             │
  │      -> edge staging (parentRow:u32, childAddr:u64)   │             │
  │      -> per-type row histogram                        │             │
  │                                                       │             │
  ├─ Phase 1b ── GC root enumeration ─────────────────────┘             │
  │    heap.EnumerateRoots + stack-thread attribution + static fields    │
  │                                                                      │
  ├─ Phase 2 ── Pass A: range-partition edges by childAddr, sort,        │
  │             merge-join against the address column                    │
  │             => swizzled (parentRow, childRow), grouped by child      │
  │             => REVERSE CSR, written directly                         │
  │                                                                      │
  ├─ Phase 3 ── Pass B: re-partition by parentRow, sort                  │
  │             => FORWARD CSR, written directly                         │
  │                                                                      │
  ├─ Phase 4 ── semi-external BFS over the forward CSR from the roots    │
  │             => reachability bitmap (10.4 MB) => persisted            │
  │                                                                      │
  ├─ Phase 5 ── LeafFolder + Lengauer-Tarjan over rank(bitmap) space     │
  │             => idom rows, retained-bytes deltas                      │
  │                                                                      │
  └─ Phase 6 ── satellites, type-row index, TypeAggregates last ─────────┘
```

### 7.1 Root enumeration moves off the critical path

**200.3 s — 15.3% of the 27.5 GB run [M]** — spent in a phase that runs strictly serially, after the
scan, before the walk. Its cost is native DAC stack unwinding, and
`cache-architecture.md` §8 documents it as intrinsic and closed to further investigation.

Intrinsic does not mean unschedulable. Root enumeration walks **thread stacks**; the scan walks
**heap segments**. Nothing in the data flow orders them — Phase 1b's output is consumed by Phase 4,
two phases later.

**Running 1a and 1b concurrently is worth up to 200 s of the 1,310 s run [D], and costs one
`Task.Run` [U — whether the DAC serialises the two readers on a shared lock is unknown, and if it
does the win is zero. One run with a stopwatch settles it; this is the cheapest large item in the
design].**

A second, independent item sits inside the same 200.3 s: `WriteFieldNameTrailer` →
`StaticFieldResolver.BuildMapByRootAddress` materialises `type.Name` — a DAC call plus a string
allocation — for **every typedef in every module in every appdomain**, before filtering. §E.3 of the
rebalance doc already flagged it and its fix (filter on module name first, order the cheap predicate
ahead of the expensive one). It has never been split out of the 200.3 s, so its share is **[U]**.

### 7.2 What the pipeline no longer contains

Not "optimised" — absent, because nothing in the ideal design creates the need:

| Gone | Why | Recovered |
|---|---|---|
| `ReverseEdgeExtractor` + bucket scratch | reverse CSR is Pass A's output | 2.19 GB scratch I/O **[M]** |
| `ReverseEdgeCsrBuilder` | ditto | 39.6 s + 2.62 GB **[M]** |
| `ForwardEdgeSorter` bucket pipeline | forward CSR is Pass B's output | 28.6 s **[M]** |
| `ScratchFileObjectMetadataLookup` | reachable rows *are* object rows | 46.7 s **[M]** |
| walk `idMap` / `edgeFrom` / `edgeTo` / 4 CSR arrays / `addresses` | §4.2 | ~6 GB **[M]** |
| `DominatorReachableAddresses` (+bases, +overflow) | §2.3 | 222.99 MiB **[M]** |
| `ObjectGenerations` | §6.1 | 83.07 MiB **[M]** |

### 7.3 Projected cold build, 27.5 GB [D, with the error bars stated]

| Phase | Today **[M]** | Ideal | Confidence |
|---|---:|---:|---|
| Heap scan | 335.5 s | 335.5 s | DAC-bound, unchanged |
| Root enumeration | 200.3 s | **0 s** (overlapped) | **[U]** §7.1 |
| Forward bucket sort | 28.6 s | — | replaced |
| Pass A + Pass B sorts | — | **+40–70 s** | **[U]** |
| Reachability walk | 213.7 s | **~110 s** | **[U]** §4.2 |
| Dominator metadata resolve | 46.7 s | **~5 s** | **[D]** — indexed read |
| Reverse CSR build + flush + write | 50.3 s | **~15 s** | **[D]** — write only |
| Writer checksums | 35.9 s | 0 s | already shipped (§E.1) |
| EventLeak publisher registry | 127.3 s | **~10 s** | **[U]** §5 |
| Report build | 81.4 s | 81.4 s | out of scope |
| Unattributed | ~190 s | ~190 s | |
| **Total** | **1,310.5 s** | **≈800 s** | **−39%** |

Three of the five wins are **[U]**. The two that are **[D]** are worth 77 s on their own.

---

## 8. Ideal vs. current — the three-axis summary

| Axis | Today **[M]** | Ideal **[D]** | Change |
|---|---:|---:|---|
| **Cold peak RAM, 27.5 GB** | **12,976 MB** | **≈2,600 MB** | **−80%** |
| Cold peak RAM, 3.3 GB | 4,132 MB | ≈1,400 MB | −66% |
| Cold wall clock, 27.5 GB | 1,310.5 s | ≈800 s | −39% |
| `cache.bin`, 27.5 GB | 2,418.1 MiB | 2,072.6 MiB *(1,740.3 without §5)* | −14% *(−28%)* |
| `cache.bin`, 3.3 GB | 342.50 MiB | 314.3 MiB *(258.5 without §5)* | −8% *(−25%)* |

**The result is lopsided on purpose, and it is the finding.** Disk barely moves — the v5–v8
sequence already took it to 24.5% of where it started and there is not much left in it. RAM moves
by 5×. That inversion is exactly what should be expected when the optimisation order changes from
"disk only" to "RAM first", and it says the shipped redesign optimised the axis with the least
remaining headroom.

The one place the ideal design *spends* is disk (§5's type index, +332 MiB), to buy runtime. Under
the stated priority order that is the correct direction, and it is the only such trade in the
document.

---

## 9. Rewrite, optimise, or delete

The comparison against what exists, made concrete.

### 9.1 Rewrite — structural, no incremental path

| # | Component | Why no increment exists |
|---|---|---|
| **R1** | **Object identity** — `row = address rank`, replacing the walk Dictionary, the CSR builder's binary searches, and the separate reachable-address universe | Every consumer's id space changes. Cannot be half-done |
| **R2** | **Edge pipeline** — range-partitioned swizzle + sort-as-CSR, replacing hash buckets + per-edge search | The partitioning scheme itself is the change; keeping hash buckets forbids the merge-join |
| **R3** | **Reachability walk** — semi-external BFS over the row-keyed disk CSR | Depends on R1 and R2 both. This is where the ~6 GB is |

R1 → R2 → R3, in that order; each is a prerequisite for the next. **This is the entire high-value
core, and all three are one coherent change.** Attempting them separately means building the walk
twice.

Note this **subsumes and supersedes C.2 / the Part F "C.2-variant"**
([runtime-rebalance](cache-redesign-runtime-rebalance.md) §C.2, §F). Part F costed that item at
−1.5 GB and −41.8 s and concluded it must ship as a *bypass with a permanent second path*, because
`buildCsr: false` still needs a reverse index (§F.2). Under R1–R3 that objection dissolves: the
reverse CSR comes from the edge sort, not from the walk, so it exists on both paths and there is
nothing to bypass. **If R1–R3 are on the table, C.2 should not be built — it is 1.5 GB of a 10.4 GB
opportunity, at the cost of a second permanent code path.**

### 9.2 Optimise — the shape is right, the encoding is not

| # | Item | Saving | Depends on |
|---|---|---:|---|
| **O1** | Sort `heap.Segments` by `Start` at Phase 0 | 0 bytes — makes R1's precondition a guarantee | nothing |
| **O2** | Delete the `ObjectGenerations` column; derive from `SegmentIndex` | **83.07 MiB [D]** | nothing |
| **O3** | `ReverseEdgeOffsets` → 1-byte degrees + 64-row checkpoints | **159.96 MiB [D]** | nothing |
| **O4** | `DominatorRetainedBytes` → `retained − ownSize`, narrowed | **≈222 MiB [U]** | one offline histogram |
| **O5** | Overlap root enumeration with the heap scan | **up to 200.3 s [U]** | nothing |
| **O6** | `StaticFieldResolver` — filter by module before materialising `type.Name` | unknown, ≤200.3 s **[U]** | one stopwatch |
| **O7** | `TypeId → rows` index | **−~120 s, +332 MiB [U]** | one stopwatch |

**O1–O3 and O5–O6 are independent of the rewrite and of each other.** O2 and O3 are pure format
changes worth 243 MiB together and should ride one version bump (measurements §10.2's batching rule).
O5 is the largest single item in this document by expected value and costs a `Task.Run` plus one
measured run.

### 9.3 Keep unchanged

The container mechanics (§6.4), the parallel scan's structure and DOP tiering, `LeafFolder` +
Lengauer–Tarjan, the satellite sections, `BoundedGraphWalk`'s 20-depth cap, the sub-cache facade with
its degrade-never-fail contract, and the address/size/typeId column encodings from v5/v6. A from-zero
design reproduces all of them.

### 9.4 Do not do

- **v9 block compression on the base object columns.** Ruled out on measurement: 22.2× penalty
  against a 10.49 GB/s zero-copy path **[M]**. Still true from zero.
- **v9 block compression at all, before R1–R3.** It is a pure disk lever — priority 3 — and it would
  be measured against a pipeline this document proposes to replace.
  [measurements](cache-redesign-measurements.md) §16's standing lesson ("v9 must be costed on all
  three axes") applies with more force, not less, once RAM is the stated priority.
- **C.2 / the Part F variant**, if R1–R3 proceed. §9.1.
- **Reintroducing any cap.** `MaxParentsPerChild`, top-K type sampling, bounded scan windows: the
  project removed these deliberately and the ideal design needs none of them. Every structure above
  is bounded by *streaming*, not by truncation.

---

## 10. What must be measured before any of this is spent

Ordered by (value at risk) × (cheapness to settle). None of the first four needs a code change
beyond instrumentation.

| # | Question | Method | Gates |
|---|---|---|---|
| **1** | Does the DAC serialise root enumeration against the heap scan? | one 27.5 GB run, roots on a `Task` | **O5, ~200 s** |
| **2** | Is `EventLeakAnalyzer`'s 127.3 s scan volume or per-match work? | one stopwatch, warm run | **O7, ~120 s + 332 MiB** |
| **3** | `retained − ownSize` distribution | offline histogram of the existing section — no dump load | **O4, ~222 MiB** |
| **4** | `WriteFieldNameTrailer`'s share of the 200.3 s | one stopwatch | **O6** |
| **5** | Two-level rank lookup vs. `Dictionary<ulong,int>`, per probe | microbenchmark against the real address column | **R1 — the load-bearing assumption of the whole design** |
| **6** | Semi-external BFS with a sorted frontier — page-fault rate and wall clock | prototype over the existing `cache.bin` | **R3, ~6 GB** |

**Measurement protocol is not optional here.** §E.7 of the rebalance doc is binding: cold wall clock
and peak private on this machine are comparable *only within one alternating A/B session*. Ambient
free memory moving 7,243 → 5,794 MB between sessions shifted peak private ~500 MB and wall clock
~14 s with identical allocation totals and GC counts **[M]** — that near-miss nearly produced a
false 14.9% claim. Measurement cost should stay proportional: item 3 costs nothing and is worth
222 MiB; item 1 costs one run and is worth 200 s. Neither justifies a 2×22-minute A/B on its own.

---

## 11. Summary

A from-zero design changes three things and inherits the rest.

1. **One identity.** The object's row in the address-ordered columnar table, established free during
   the scan, resolved by rank over a 0.65 MiB index that is already on disk. The reachable-node space
   is a rank-select bitmap over it, not a second address universe. **Kills 2.4 GB of RAM and 223 MiB
   of disk [D].**
2. **Swizzle once, sort instead of search.** Edges carry the parent's row from birth; one
   range-partitioned sort resolves the child and *is* the reverse CSR; a second *is* the forward CSR.
   **Kills 2.6 GB of RAM, 2.2 GB of scratch I/O, 68 s, and 10.3 billion random probes [M/D].**
3. **A memory budget per phase.** Peak becomes the max of stated budgets rather than an emergent
   property. **≈2.6 GB against 12,976 MB measured [D].**

Everything else — the container, the checksum discipline, the column encodings, the
degrade-never-fail contract, LT, the scan itself — a from-zero design would build the way it is
already built.

The uncomfortable part of the comparison is that the axis with the most headroom was never measured
until Part D, and the axis with the least got four format versions. Under the priority order now in
force, that is inverted.

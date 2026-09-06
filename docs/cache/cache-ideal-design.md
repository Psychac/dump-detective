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
| `Dictionary<ulong,int>` idMap | `ReachableGraphWalker.WalkWithCsr` | **2,325.9 MB at O = 87.1M [M, §11.2 — directly measured]** |
| `Array.BinarySearch` over `ulong[R]` | `ReverseEdgeCsrBuilder.ResolveRow` | 0.43 GB resident + **~10.3 billion random probes [D, Part B.2]** |
| segment table + per-segment binary search | `ObjectAddressLookup` | small — it is the only one that is right |

### 2.1 The ordinal already exists

During the heap scan every object is written to the columnar file at a definite record index. **That
index is a dense, gapless, zero-cost object id.** Nothing needs to be built to obtain it.

For it to be *useful* as an identity, `row → address` must be monotone — otherwise `address → row`
is not a rank query. Three independent lines of evidence say it already is:

1. **Directly verified [M, §11.1/§11.2]: the decoded column is strictly ascending on both dumps** —
   every one of 87,104,236 and 14,620,162 records. This was checked element-by-element, not inferred.
   (`ObjectAddressOverflow` is also 0 records on the 27.5 GB dump and 16 on the 3.3 GB one, the
   latter being blocks straddling a >4 GB LOH gap, not descending steps.)
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

**✅ Measured (§11.2): this replaces a 2,325.9 MB hash table with 0.65 MiB — a 3,584× ratio.** On
speed the answer is nuanced and favourable: Arm B is *faster* than the Dictionary in ascending order
at 27.5 GB scale (63.9 vs 68.8 ns) and 1.60× slower in realistic edge order (66.2 vs 41.5 ns),
because Arm A degrades as its table outgrows cache while Arm B's resident 0.65 MiB stays in L2.
Across E = 137M edges that is **+3.4 s of walk time for −2.3 GB**. Part B.2's objection to binary
search does not transfer: it condemned a search over a 664 MB `ulong[]` with no resident top level,
which is exactly what the block-base array supplies.

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
| address → id, build path | `Dictionary<ulong,int>`, **2,325.9 MB [M]** | mmap column + **0.65 MiB** bases **[M]** |
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
part-measured: **§11.3 clocks the CSR-construction half at 2.59 s for all 137M edges**, so what
remains open is only the address-sort half. Every term this removes is measured and every term it
adds is sequential I/O.

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
Two mitigations were proposed. **§11.3 measured both, and only one survives:**

1. ~~**Sort each frontier level before expanding it.**~~ **❌ Refuted (§11.3).** Sorting cost 1.67×
   on the 27.5 GB dump while cutting page faults by only 4.6%. The sort is real work; the locality
   it buys is not. Use a plain FIFO frontier.
2. **The freed memory pays for the page cache.** ✅ This is what carries the design. The forward CSR
   is ~870 MiB **[D]**. Not holding 6 GB of walk state on a 15.7 GiB machine is exactly what lets
   those pages stay resident. The 27.5 GB run currently bottoms out at **356 MB available and pages
   3.9 GB [M]** — it has no page cache to give the mmap.

**Measured (§11.3): the full forward BFS over 137M edges is 1.63 s mmap'd, 1.04 s in memory**,
against the current walk phase's **213.7 s [M]**. The traversal was never the cost; the Dictionary,
the per-node loose-file parse and the 137M locked `RecordEdge` calls were. The visited bitmap at
7.29 MB also fits in L3, which no address-keyed structure can do.

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
change the reverse CSR bought Q4: `O(objects)` becomes `O(matches)`.

**⚠ MEASURED, AND THIS SECTION'S PREMISE DID NOT SURVIVE (§11.6).** The 127.3 s registry build was
the whole justification. Instrumented, it is **87.9% `DescribeInstanceFields`** — per-type DAC
metadata resolution that scales with dump size and that no index in `cache.bin` can reach — and only
**5.0% (5.28 s) index scan**. The claim above that this was "the only lever available against the
127.3 s" was wrong: it was a lever against 5% of it.

What survives:

- **A type-filtered full pass costs ≈5.3 s at 27.5 GB scale [M].** §1 counts eight of them, so the
  index is worth **≈40 s for 332 MiB** — a far weaker trade than the ≈120 s this section assumed, and
  not recommended until the other seven sites are measured individually.
- **The specific case that motivated it is answerable for free.** Pass 2a scans 87.1M objects purely
  to learn *which types exist*, and that set is already persisted as `ObjectTypeDictionary` at
  0.09 MiB — verified identical on both dumps (§11.7). That is **O8**, worth 5.28 s at zero disk
  cost.

The general lesson is worth keeping even though the item is not: **"analyzer does a full scan" does
not imply "the scan is the cost."** Before adding an access path, measure what the scan's consumer
does per record.

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
| `DominatorRetainedBytes` (8 B) | 445.10 | **112.51** | **−332.59** | §6.2 ✅ measured |
| `ReverseEdgeChildren` (4 B/edge) | 522.74 | 522.74 | — | irreducible pre-compression |
| `ReverseEdgeOffsets` (4 B/row) | 222.55 | **62.59** | **−159.96** | §6.3 |
| Satellites, dictionary, metadata | 34.6 | 34.6 | — | |
| **Total** | **2,418.1** | **1,962.5** | **−455.6** | |
| *without the §5 type index* | | *1,630.3* | *−787.8* | |

Applying the same shape to the 3.3 GB dump: **342.50 → 301.6 MiB with the type index, 245.8 without**.

### 6.1 `ObjectGenerations` is 83 MiB of a pure function

Generation is `f(segment, address)` — `segment.Kind` under regions GC, or `segment.GetGeneration(addr)`
for classic ephemeral segments, which is itself a range compare against the segment's
`Generation0`/`Generation1`/`Generation2` sub-ranges **[M — that is exactly what
`DiskBackedObjectIndexWriter.cs:256-259` and `:2054-2057` do]**. A 63-row table of segment ranges
answers it in O(log segments). **Storing one byte per object to memoise a range compare is 83.07 MiB
of pure waste [D]**, and the table it needs is `SegmentIndex`, which already exists at 1.8 KB.

### 6.2 ✅ MEASURED — `DominatorRetainedBytes` narrows to 2 bytes, unchanged

445.10 MiB, the second-largest section, at a flat 8 B/row. **§11.1 settled this offline.** 69–75% of
rows are dominator-tree leaves whose `retained` is exactly `ownSize`, and the whole distribution
fits 2 bytes at a **0.19% escape rate**, so `NarrowColumnWidth.Choose` picks 2 B on both dumps:
**445.10 → 112.51 MiB, −332.59 MiB (74.7%)**.

**This section originally proposed storing `retained − ownSize` so leaf rows become zero. The
measurement says don't bother** — narrowing the raw column costs 112.56 MiB against 112.51 MiB for
the difference. The leaves are small absolutely, not just relative to themselves. Dropping the
subtraction makes O4 a straight reuse of the `NarrowColumnWidth` + `ColumnOverflowTable` path
`ObjectSizes` already runs, with no read-time join against `ObjectSizes` and no new concept.

The 8 B → 2 B narrowing is orthogonal to the **measured 24.69× zstd ratio on this section [M]**;
compression would still apply on top, if v9 ever happens.

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

**✅ Measured (§11.4): the phase is 98.4% `heap.EnumerateRoots()` and 1.6% trailer** on the 27.5 GB
dump. §E.3's second item — `WriteFieldNameTrailer` materialising `type.Name` for every typedef before
filtering — is worth **1.99 s**, and both fixes it proposed measured *slower* than the code they were
meant to replace, because the name filter prunes a more expensive `StaticFields` walk. O6 is dropped.
That leaves overlap as the only lever here, which is the right shape for irreducible DAC work.

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
| Root enumeration | 200.3 s | **0 s** (overlapped) | **[U]** §7.1 — 98.4% irreducible DAC **[M]** |
| Forward bucket sort | 28.6 s | — | replaced |
| Pass A + Pass B sorts | — | **+10–40 s** | ◐ CSR half **2.59 s [M]**, sort half **[U]** |
| Reachability walk | 213.7 s | **~2 s** | **[M]** §11.3 — 1.63 s measured |
| Dominator metadata resolve | 46.7 s | **~5 s** | **[D]** — indexed read |
| Reverse CSR build + flush + write | 50.3 s | **~15 s** | **[D]** — write only |
| Writer checksums | 35.9 s | 0 s | already shipped (§E.1) |
| EventLeak publisher registry | 127.3 s | **~122 s** | **[M]** §11.6 — only Pass 2a (5.3 s) is addressable |
| Report build | 81.4 s | 81.4 s | out of scope |
| Unattributed | ~190 s | ~190 s | |
| **Total** | **1,310.5 s** | **≈790 s** | **−40%** |

After §11 the walk row is measured rather than guessed (~110 s → ~2 s), and the EventLeak row moved
the other way: §11.6 found 87.9% of it is per-type DAC metadata work that no cache index can reach,
so only 5.3 s of the 127.3 s is addressable. **O5 (root overlap, ~200 s) is now the single largest
remaining item and the only one still [U].**

---

## 8. Ideal vs. current — the three-axis summary

| Axis | Today **[M]** | Ideal **[D]** | Change |
|---|---:|---:|---|
| **Cold peak RAM, 27.5 GB** | **12,976 MB** | **≈2,600 MB** | **−80%** |
| Cold peak RAM, 3.3 GB | 4,132 MB | ≈1,400 MB | −66% |
| Cold wall clock, 27.5 GB | 1,310.5 s | ≈680 s | −48% |
| `cache.bin`, 27.5 GB | 2,418.1 MiB | 1,962.5 MiB *(1,630.3 without §5)* | −19% *(−33%)* |
| `cache.bin`, 3.3 GB | 342.50 MiB | 301.6 MiB *(245.8 without §5)* | −12% *(−28%)* |

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
| **R3** | **Reachability walk** — semi-external BFS over the row-keyed disk CSR, plain FIFO frontier | Depends on R1 and R2 both. This is where the ~6 GB *and* ~210 s are (§11.3) |

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
| **O4** | `DominatorRetainedBytes` → 2 B via existing `NarrowColumnWidth` | **332.59 MiB [M]** ✅ | none — measured, §11.1 |
| **O5** | Overlap root enumeration with the heap scan | **up to 200.3 s [U]** | nothing |
| ~~O6~~ | ~~`StaticFieldResolver` filter order~~ — ❌ **DROPPED**, §11.4: worth 1.99 s, and both proposed variants measured *slower* | — | closed |
| ~~O7~~ | ~~`TypeId → rows` index~~ — ❌ **DROPPED as scoped**, §11.6: reaches 5.28 s of 105.66 s. Revised case is ≈40 s for 332 MiB across 8 sites, unmeasured | — | needs the other 7 sites measured first |
| **O8** | `PublisherRegistry` Pass 2a reads `ObjectTypeDictionary` instead of scanning 87.1M objects | **5.28 s [M]**, zero new disk | none — §11.7 |

**O1–O4 and O5–O6 are independent of the rewrite and of each other.** O2, O3 and O4 are pure format
changes worth **575.6 MiB** together and should ride one version bump (measurements §10.2's batching rule).
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
| ~~2~~ | ❌ **CLOSED §11.6** — 87.9% per-type DAC work; a type index reaches 5% of it | `DD_PERF_EVENTLEAK_REGISTRY=1` | **O7 dropped as scoped; O8 found** |
| ~~3~~ | ✅ **CLOSED §11.1** — 2 B/row, subtraction unnecessary | offline histogram | **O4, 332.59 MiB** |
| ~~4~~ | ❌ **CLOSED §11.4** — 1.6% of the phase; the proposed fix is 22× slower | `tools/ProfileRootPhase` | **O6 dropped** |
| ~~5~~ | ✅ **CLOSED §11.2** — R1 GO: +3.4 s of walk, −2,325.9 MB | `tools/AddressLookupBench` | **R1** |
| ~~6~~ | ✅ **CLOSED §11.3** — walk is 1.63 s; **do not sort the frontier** | `tools/SemiExternalBfsBench` | **R3, ~6 GB** |

**Measurement protocol is not optional here.** §E.7 of the rebalance doc is binding: cold wall clock
and peak private on this machine are comparable *only within one alternating A/B session*. Ambient
free memory moving 7,243 → 5,794 MB between sessions shifted peak private ~500 MB and wall clock
~14 s with identical allocation totals and GC counts **[M]** — that near-miss nearly produced a
false 14.9% claim. Measurement cost should stay proportional: item 3 costs nothing and is worth
222 MiB; item 1 costs one run and is worth 200 s. Neither justifies a 2×22-minute A/B on its own.

---

## 11. Measured results (2026-09-06)

Three of §10's six questions are closed. All three were answered **without loading a dump** — the
existing `cache.bin` containers carry enough to settle them — so the whole round cost minutes, not
the 22-minute cold rebuilds items 1/2/4 will need. Harnesses: `tools/AddressLookupBench`,
`tools/SemiExternalBfsBench`, plus an offline numpy decoder for the columns.

### 11.1 ✅ Q3 — `DominatorRetainedBytes` narrows to **2 bytes**, not 4

Method: decode `DominatorRetainedBytes` and `ObjectSizes`, merge-join the two address columns to map
reachable row → object row, histogram the difference. Both dumps:

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Rows where `retained == ownSize` (dominator-tree leaves) | **74.52%** | **69.31%** |
| Escape rate at 2 B | 0.0968% | 0.1853% |
| Escape rate at 4 B | 0.0000% | 0.000010% |
| `NarrowColumnWidth.Choose` picks | **2 B/row** | **2 B/row** |
| Today | 51.01 MiB | 445.10 MiB |
| Ideal | **12.83 MiB** | **112.51 MiB** |
| **Saved** | **38.19 MiB (74.9%)** | **332.59 MiB (74.7%)** |

**§6.2 was 2× too pessimistic** — the estimate was 222.55 MiB, the measurement is 112.51 MiB, so O4
is worth **332.59 MiB**, not 222.55.

**⚠ §6.2's mechanism was also wrong, in a way that makes it simpler.** The section recommended
storing `retained − ownSize` so leaf rows become zero. Measured, the subtraction earns nothing:
narrowing the *raw* `retained` column costs 112.56 MiB against 112.51 MiB for the difference — a
0.05 MiB gap. The leaves are small in absolute terms, not just relative to their own size, so raw
`retained` already fits 2 bytes at the same rate. **Drop the subtraction.** O4 becomes "apply the
existing `NarrowColumnWidth` + `ColumnOverflowTable` machinery to one more column" — the same code
path `ObjectSizes` already uses, no join against `ObjectSizes` at read time, no new concept.

### 11.2 ✅ Q5 — R1 holds. Arm B costs 3.4 s of walk time and saves 2.3 GB

The load-bearing assumption. `tools/AddressLookupBench` runs both mechanisms against the real
address column, with three probe orders.

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Arm A `Dictionary<ulong,int>` resident | 390.4 MB | **2,325.9 MB** (28.0 B/entry) |
| Arm A build time | 0.74 s | 4.39 s |
| Arm B two-level rank resident | 0.11 MB | **0.65 MB** (0.008 B/entry) |
| **Memory ratio** | 3,584× | **3,584×** |

**§2.4's "≈2.0 GB" was itself an underestimate — directly measured, the table is 2,325.9 MB.**

Per-probe cost, and the scaling behaviour is the interesting part:

| Probe order | 3.3 GB A / B / ratio | 27.5 GB A / B / ratio |
|---|---:|---:|
| sequential | 44.8 / 54.9 ns — 1.22× | 68.8 / **63.9** ns — **0.93×** |
| random | 92.9 / 436.1 ns — 4.69× | 135.3 / 450.5 ns — 3.33× |
| edge (real reference locality) | 42.4 / 81.7 ns — 1.93× | 41.5 / **66.2** ns — **1.60×** |

**The scaling prediction held.** Arm A degrades as its table outgrows cache (44.8 → 68.8 ns
sequential, 92.9 → 135.3 random); Arm B is flat, because its resident part is 0.65 MiB and stays in
L2 regardless of dump size. At 27.5 GB, Arm B is already *faster* in sequential order.

Converting to walk cost at E = 137,033,360 edges **[D]**:

| Order | Δ ns/probe | Walk cost of choosing Arm B |
|---|---:|---:|
| sequential | −4.9 | **−0.7 s** (Arm B faster) |
| edge — realistic | +24.7 | **+3.4 s** |
| random — pessimistic | +315.2 | +43.2 s |

**Verdict: R1 is GO.** 3.4 s for 2.3 GB is not a close call. The earlier framing ("it may even be
faster, measure before spending") resolves to: faster in sorted order, ~3 s slower in realistic
order, and the random-order regime must be avoided — which §11.3 shows the swizzle does for free,
since a row-keyed walk performs *no address lookups at all*.

*Harness gap, recorded:* Arm B returns `-1` for the 16 escaped records on the 3.3 GB dump rather
than consulting `ObjectAddressOverflow` (13 of 10M random probes disagreed). Zero escapes on the
27.5 GB dump, so that column verified exactly. A real implementation consults the table; the
benchmark's omission does not affect timing.

### 11.3 ⚠ Q6 — the walk is ~1 second, and sorting the frontier makes it *worse*

`tools/SemiExternalBfsBench` transposes the persisted v8 reverse CSR into a forward CSR and runs a
full forward BFS from the graph's real sources. Two results, and the second contradicts §4.2.

**The transpose is §3.2's counting-sort primitive, measured on the real edge set:**

| | 3.3 GB (E = 17.4M) | 27.5 GB (E = 137.0M) |
|---|---:|---:|
| Counting sort → CSR | **0.35 s** | **2.59 s** |
| Resident (offsets + targets) | 117 MB | 968 MB |

**A CSR over 137M edges is built in 2.59 seconds.** §7.3 budgeted "+40–70 s" for Pass A + Pass B;
the CSR-construction half of that is 2.59 s. The remaining half — the external sort by address plus
the merge-join — is still **[U]**, but the estimate should be revised down.

**Full forward BFS, 58,324,726 rows and 136,993,584 edges visited:**

| Arm | 3.3 GB | 27.5 GB | ns/edge (27.5 GB) | soft faults |
|---|---:|---:|---:|---:|
| A in-memory CSR, FIFO frontier | 0.10 s | **1.04 s** | 7.6 | 44,279 |
| B mmap'd CSR, FIFO frontier | 0.16 s | **1.63 s** | 11.9 | 228,830 |
| C mmap'd CSR, **sorted** frontier | 0.29 s | **2.72 s** | 19.8 | 218,319 |

**The reachability walk over 137M edges is 1.0–1.6 seconds.** The current walk phase measures
**213.7 s [M]**. The difference is not traversal — it is the 2.3 GB Dictionary, the per-node loose-file
parse, the 137M individually-locked `RecordEdge` calls (§E.4) and the `ChunkedBuffer` appends. The
graph walk itself is nearly free once identity is a dense row and the CSR is materialised.

Part of why: the visited bitmap at R = 58.3M is **7.29 MB, which fits in L3**. That is a structural
property of the bitmap representation, not a tuning result, and it is unavailable to any
address-keyed structure.

**⚠ §4.2's mitigation (1) is refuted. Do not sort the frontier.** Sorting cost 1.67× on the 27.5 GB
dump and 1.81× on the 3.3 GB one, while reducing faults by only 4.6% (228,830 → 218,319). The sort
is real work; the locality it buys is not worth it. **§4.2's mitigation (2) — that the freed ~6 GB
keeps the CSR page-cache resident — is what carries the semi-external design**, and arm B's 1.57×
against in-memory is the price.

*Limitation, stated:* the temp CSR was written immediately before arms B and C ran, so it was
page-cache resident. This measures the warm case — which is the case §4.2 argues for — and **not**
a cold-storage BFS. Under genuine memory pressure the faults become hard and the ranking could
change; that regime is exactly what freeing 6 GB is meant to prevent.

### 11.4 ✅ Q4 — the root phase is 98.4% DAC walk. O6 is dropped

`tools/ProfileRootPhase` runs the root phase in isolation — dump load, `heap.EnumerateRoots()`, then
`BuildMapByRootAddress` — instead of the full cold build `tools/ProfileRootEnumeration` costs. One
dump load plus ~2 minutes rather than 22.

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| Phase 1 `heap.EnumerateRoots()` | 12.11 s — **87.6%** | **124.33 s — 98.4%** |
| Phase 2 `BuildMapByRootAddress` | 1.71 s — 12.4% | **1.99 s — 1.6%** |
| Roots enumerated | 1,411 | 5,037 |
| Typedefs walked | 36,646 | 38,372 |

**§E.3's suspicion was structurally accurate and quantitatively wrong.** The loop does walk every
typedef in every module and does materialise `type.Name` before filtering — but the typedef universe
is 38,372 against 12,376 live types, i.e. **3.1×, not the orders of magnitude implied**, and the
whole thing costs **1.99 s of a 126 s phase**. Cost centres inside it: `type.Name` + filters 0.60 s,
`EnumerateTypeDefToMethodTableMap` 0.37 s, `GetTypeByMethodTable` 0.24 s, `StaticFields` 0.15 s.

**⚠ Worse, §E.3's proposed fix is a pessimisation.** Both variants were built and A/B'd warm against
the real implementation, all three producing byte-identical maps:

| Variant | 3.3 GB | 27.5 GB |
|---|---:|---:|
| real `StaticFieldResolver` | **0.03 s** | **0.03 s** |
| "reorder" — defer `type.Name` until after the address test | 0.66 s | 0.67 s (**22× slower**) |
| "module prefilter" — skip framework modules by name | 0.09 s | 0.10 s (3× slower) |

The name filter is not overhead ahead of the real work — it is a **pruning step**. It cuts 38,222
resolved types down to 11,572 before `type.StaticFields` is enumerated, and that enumeration is what
deferring the filter forces on every type. §E.3 read the loop as "expensive predicate first" when it
is actually "cheap predicate that avoids an expensive walk".

**O6 is dropped.** This is exactly what "instrument before touching" was written to catch: the item
had a plausible mechanism, a real code smell behind it, and a negative expected value.

*On the absolute number:* the cold build labels this phase **200.3 s [M]** while this tool measures
124.33 s for the DAC walk. The tool measures the walk alone — the build's label also covers
`RootIndexWriter`'s record packing, section writes and progress reporting — and this run had a warmer
OS page cache after a session of reading the same file. **The 98.4/1.6 split is the robust result;
124.33 s is a lower bound on the walk, not a restatement of the 200.3 s.**

**O5 is unaffected and now stands alone.** With the trailer worth ~2 s, essentially the entire root
phase is native DAC stack unwinding, which `cache-architecture.md` §8 documents as irreducible.
Irreducible work is the right kind to *overlap* (§7.1), and O5 is now the only lever on this phase.

### 11.5 ⚠ Incidental — static-root detection is inert under ClrMD 4

Both dumps report **0 static/thread-static roots**, which is why `BuildMapByRootAddress` returns an
empty map in every measurement above. That is not a property of the dumps.

`RootIndexWriter` selects them with `private const byte ThreadStaticVarKind = 9` /
`StaticVarKind = 10` against `(byte)root.RootKind`. **`ClrRootKind` in ClrMD 4.0.722401 has no such
members** — enumerated directly, it is `None=0, FinalizerQueue=1, StrongHandle=2, PinnedHandle=3,
Stack=4, RefCountedHandle=5, AsyncPinnedHandle=7, SizedRefHandle=8`. Values 9 and 10 are ClrMD 3
values that no longer exist, so the predicate can never fire.

Consequences, unverified beyond the above but following directly from it: `staticRootAddresses` is
always empty, so `WriteFieldNameTrailer` early-returns, so the v2 `Roots` field-name trailer is
always empty, so `RootSetCache.GetStaticFieldsByRootAddress` always returns an empty map — and
`GCRootAnalyzer`, `StaticRootLeakDetector` and `FinalizableObjectAnalyzer` lose static-field
attribution silently.

Note the fix is probably **not** "change the constants". ClrMD 4's root enumeration appears to cover
handles, stacks and the finalizer queue only; if it emits no static-variable roots at all, then the
trailer's whole approach — match a static field's storage address against an *enumerated static
root* — has nothing to match against, and `StaticFieldResolver`'s map would need to be keyed without
that filter. **This is outside the cache redesign's scope and is reported, not fixed.** It belongs to
the `upgrade/clrmd-4` branch's own work and may already be known there.

### 11.6 ❌ Q2 — `EventLeakAnalyzer`'s cost is per-type DAC work, not scan volume. O7 is dropped as scoped

The 127.3 s registry build was §5's entire justification — the largest proposed disk *addition* in
this document rested on it. Instrumented per pass (`DD_PERF_EVENTLEAK_REGISTRY=1`, warm cache,
`--include-analyzers "Event Leak Analysis"`):

| Pass | 3.3 GB | 27.5 GB |
|---|---:|---:|
| **total** | **18.11 s** | **105.66 s** |
| 1 — typedef walk + `DescribeStaticFields` | 8.47 s (46.8%) | 7.51 s (7.1%) |
| 2a — full index scan → distinct MethodTables | 1.51 s (8.3%) | **5.28 s (5.0%)** |
| 2b — `DescribeInstanceFields` over live MTs | 8.13 s (44.9%) | **92.87 s (87.9%)** |

**The answer is per-match work.** Pass 2b is 87.9% of the build, and its scaling is the giveaway: it
went from 8.13 s over **14,003** MethodTables to 92.87 s over **12,376** — *fewer* types, 11× slower.
It is not per-type-count work at all. `DescribeInstanceFields` "touches every field's `ClrType` to
check for a delegate base type" (the class's own doc comment), and those are DAC metadata reads whose
cost scales with dump size. **No index in `cache.bin` can touch that** — it is ClrMD metadata
resolution, not heap-index access.

**O7 is dropped as scoped.** A `TypeId → rows` index would have targeted Pass 2a: **5.28 s of
105.66 s**, bought with 332.28 MiB of new disk. That is not a trade worth making, and §5's
"only lever available against the 127.3 s" was wrong — it was a lever against 5% of it.

*Revised, weaker case for O7:* Pass 2a is a fair measurement of what one type-filtered full pass
costs — **≈5.3 s at 27.5 GB scale** — and §1 counted eight such passes, so the index is worth
**≈40 s, not ≈120 s**, for 332 MiB. That is a much weaker trade and it is no longer recommended
without separately measuring the other seven sites, several of which may be answerable from sections
that already exist (below).

*On the absolute number:* this warm, single-analyzer run measured 105.66 s against the cold build's
**127.3 s [M]**. The cold run had a colder DAC and was under real memory pressure (356 MB available,
3.9 GB paged). The **pass split is the robust result**, not the total.

### 11.7 ✅ Free win found instead — Pass 2a is already persisted

Pass 2a walks all 87,104,236 indexed objects for one reason: to collect the **set of distinct
MethodTables**. `ObjectTypeDictionary` is defined as exactly that set. Verified offline on both
dumps — no dump load:

| | 3.3 GB | 27.5 GB |
|---|---:|---:|
| `ObjectTypeDictionary` entries | 14,003 | 12,376 |
| Distinct `TypeId`s actually used in `ObjectMethodTables` | 14,003 | 12,376 |
| Every dictionary slot used | **yes** | **yes** |
| Dictionary strictly ascending | yes | yes |
| Bytes Pass 2a reads to derive it | 27.9 MiB | **166.1 MiB** |
| Bytes the answer occupies | 0.11 MiB | **0.09 MiB** |
| Amplification | 261× | **1,760×** |

The sets are identical by construction and confirmed empirically. **Replacing Pass 2a's scan with a
read of `ObjectTypeDictionary` removes 5.28 s for a ~10-line change and zero new disk** — strictly
better than what O7 proposed to buy with 332 MiB, and independent of the rewrite.

New item, call it **O8**: expose the type dictionary through `IHeapAnalysisCache` and have
`PublisherRegistry` Pass 2a read it. Any other consumer that scans the index solely to learn *which
types exist* is the same case; §1's Q2 sites should be re-read with that question in mind before O7
is reconsidered.

### 11.8 Incidental — 4–5 reachable rows are not live objects

The Q3 merge-join is not quite total: 5 of 6,686,490 rows on the 3.3 GB dump and 4 of 58,339,936 on
the 27.5 GB dump have no matching object row. Every one sits outside the heap's address range —
`0x000000ffffff`, `0x0084d3b7db20`, `0x1000007ffa899b53`, `0x2000009ac41c4ad1` — i.e. tagged or
garbage pointers that arrive as `root.Object.Address` from conservative stack scanning and are
seeded into the walk without validation.

They occupy real rows in `DominatorReachableAddresses`, `DominatorIdomRows`, `DominatorRetainedBytes`
and the reverse CSR today. Two consequences: R2's merge-join swizzle needs a defined behaviour for
them (drop the node — the rank-select bitmap simply never sets that bit), and filtering root
addresses against the segment ranges at seed time would remove them at source. Tiny, but it is a
correctness detail the swizzle must not trip over.

### 11.9 Revised sizing

Folding §11.1 into §6's table (`DominatorRetainedBytes` 222.55 → 112.51 MiB):

| | Today | Ideal, with §5 type index | Ideal, without |
|---|---:|---:|---:|
| 27.5 GB | 2,418.1 MiB | **1,962.5 MiB (81.2%)** | **1,630.3 MiB (67.4%)** |
| 3.3 GB | 342.50 MiB | **301.6 MiB (88.1%)** | **245.8 MiB (71.8%)** |

### 11.10 What the round changed

| Claim | Status |
|---|---|
| §2.1 address column is monotone | ✅ **directly verified** — strictly ascending on both dumps, not inferred from escape counts |
| §2.2/§2.4 rank index replaces the Dictionary | ✅ confirmed, and the Dictionary is *larger* than stated (2,325.9 MB) |
| §2.2 "may also be faster" | ◐ faster in sorted order, +3.4 s in realistic order — worth it either way |
| §3.2 sort-as-CSR is cheap | ✅ 2.59 s for 137M edges |
| §4.2 the walk stops being a memory consumer | ✅ and it stops being a *time* consumer too — 1.0–1.6 s vs 213.7 s |
| §4.2 mitigation (1), sort the frontier | ❌ **refuted** — 1.67× slower, 4.6% fewer faults |
| §6.2 `retained − ownSize`, ~222 MiB at 4 B | ◐ **saving is larger (332.59 MiB at 2 B); mechanism is unnecessary — drop the subtraction** |
| §7.3 Pass A + Pass B cost +40–70 s | ◐ CSR half measured at 2.59 s; sort half still open |
| §7.1/§E.3 `WriteFieldNameTrailer` is a large share of the 200.3 s | ❌ **refuted** — 1.6%, and both proposed fixes measured slower. **O6 dropped** |
| §7.1 root phase is irreducible DAC work | ✅ confirmed — 98.4% of the phase. O5 (overlap) is the only lever |
| §5/§7.3 EventLeak's 127.3 s is scan volume | ❌ **refuted** — 87.9% is per-type DAC metadata work. **O7 dropped as scoped** |
| §5 a type index is the only lever there | ❌ wrong — it reaches 5.0%. Revised worth: ≈40 s for 332 MiB across 8 sites, unmeasured |
| — | ✅ **new O8**: Pass 2a's 87.1M-object scan is already persisted as `ObjectTypeDictionary` — 5.28 s, zero disk (§11.7) |
| — | ⚠ **new**: static-root detection is inert under ClrMD 4 (§11.5) — outside scope, reported |

---

## 12. Summary

A from-zero design changes three things and inherits the rest.

1. **One identity.** The object's row in the address-ordered columnar table, established free during
   the scan, resolved by rank over a 0.65 MiB index that is already on disk. The reachable-node space
   is a rank-select bitmap over it, not a second address universe. **Kills 2,325.9 MB of RAM and
   223 MiB of disk, for +3.4 s of walk time [M, §11.2].**
2. **Swizzle once, sort instead of search.** Edges carry the parent's row from birth; one
   range-partitioned sort resolves the child and *is* the reverse CSR; a second *is* the forward CSR.
   **Kills 2.6 GB of RAM, 2.2 GB of scratch I/O, 68 s, and 10.3 billion random probes [M/D].**
3. **A memory budget per phase.** Peak becomes the max of stated budgets rather than an emergent
   property. **≈2.6 GB against 12,976 MB measured [D].** The walk, today ~6 GB and 213.7 s, becomes
   ~64 MB and ~2 s **[M, §11.3]**.

Everything else — the container, the checksum discipline, the column encodings, the
degrade-never-fail contract, LT, the scan itself — a from-zero design would build the way it is
already built.

The uncomfortable part of the comparison is that the axis with the most headroom was never measured
until Part D, and the axis with the least got four format versions. Under the priority order now in
force, that is inverted.

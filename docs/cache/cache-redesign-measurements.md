# Cache Redesign — Measured Evidence

Hard measurements backing
[cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md) and
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md).
Both docs previously rested on one dump and, for compression, on an explicitly-flagged guess.
Everything below is measured.

> **Units.** This doc uses **MB = 10⁶ bytes** throughout, because that is what the measurement
> scripts emit. The two redesign docs and
> [cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md) use
> **MiB = 1024²**. Same files, 4.9% apart: the reference `cache.bin` is 1,466,259,011 bytes =
> 1,466.3 MB = 1,398.3 MiB. Ratios, percentages and timings are unaffected. Byte counts are given
> alongside the headline figures so either unit can be re-derived.

**Method.** No dump was loaded. Five `cache.bin` files already existed on disk from prior runs;
all structural numbers come from parsing their headers and TOCs directly, and all compression
numbers come from compressing their actual section bytes. Runtime numbers come from a harness
replicating `CacheContainerReader.VerifyChecksumZeroCopy` and
`ObjectIndexReader.ZeroCopyColumnReader.FillBatch` against the reference `cache.bin`.
Scripts: `toc_dump.py`, `compress_probe.py`, `csumbench/` (scratchpad, not committed).

Two results overturn conclusions the redesign docs currently state. They are marked **⚠ OVERTURNS**.

---

## 1. Structural survey — five real caches, not one

| Dump | Dump size | `cache.bin` | cache/dump | Objects | Types | Fmt |
|---|---:|---:|---:|---:|---:|---:|
| 06-04 11:44 | 0.75 GB | 5.9 MB | 0.8% | 203,411 | 1,507 | v3 |
| 06-04 06:43 | 2.83 GB | 188.4 MB | 6.7% | 7,319,397 | 5,703 | v3 |
| 06-04 12:58 | 3.50 GB | 282.2 MB | 8.1% | 10,847,965 | 7,484 | v3 |
| **Crash_IIS (reference)** | **3.51 GB** | **1,466.3 MB** | **41.7%** | **14,620,162** | **14,003** | **v4** |
| **21-04 w3wp** | **27.52 GB** | **9,881.5 MB** | **35.9%** | **87,104,236** | **12,376** | **v4** |

Group breakdown for the two current-format (v4) caches:

| Group | Reference (3.51 GB dump) | 21-04 (27.52 GB dump) |
|---|---:|---:|
| Edge index (fwd+rev, buckets+directories+meta) | 837.8 MB — **57.1%** | 7,669.6 MB — **77.6%** |
| Base columns (Addresses/MTs/Sizes/Generations) | 365.5 MB — 24.9% | 2,177.6 MB — 22.0% |
| Dominator tree (6 sections) | 239.5 MB — 16.3% | *absent* (Stage B not gated on) |
| StringDedup (+meta) | 21.4 MB — 1.5% | 31.8 MB — 0.3% |
| TypeAggregates | 1.5 MB — 0.1% | 1.2 MB — 0.0% |
| Satellites (7 sections) | 0.6 MB — 0.0% | 1.3 MB — 0.0% |

**The edge index gets worse with scale, not better.** 57.1% → 77.6%. The format doc's whole
analysis is anchored on the reference dump; on the largest real dump available the problem it
targets is substantially bigger.

**Directory overhead alone** — the thing true CSR deletes outright rather than narrows:

| | Reference | 21-04 |
|---|---:|---:|
| `ReverseEdgeDirectories` | 107.0 MB | 1,367.6 MB |
| `ForwardEdgeDirectories` | 122.7 MB | 721.6 MB |
| **Combined** | **229.7 MB (15.7% of file)** | **2,089.2 MB (21.1% of file)** |

Deleting the directory concept is worth 2.09 GB on the 27.52 GB dump before any other technique
applies. §2 of the format doc is, if anything, undersold.

### 1.1 `EventCandidates` — provenance of the stale backlog entry

The three v3 caches **do** contain an `EventCandidates` section (365,496 / 4,440 / 4,226,952 bytes;
22,842 / 276 / 264,183 records). Both v4 caches do not.

This resolves the discrepancy noted when the backlog entry was corrected. The entry was accurate
when written — v3-era code did write the section — and the writer was removed at some point before
the current v4 format. The correction stands for current code (no writer, no reader, no collection
exists today), but the entry was not fabricated. The v3 files are dead artifacts regardless:
`CacheFileHeader.TryRead` rejects any version ≠ 4, so they will be rebuilt on next use.

---

## 2. Compression — measured, replacing §5's "informed guess"

Independent per-block compression (each block decodes standalone, which is what point lookup
requires), sampled across each section. `blockPenalty` = ratio lost versus compressing a 32 MB
contiguous run as one unit.

### Reference dump (3.51 GB / 14.6M objects), zstd level 9, 64 KB blocks

| Section | Raw MB | 64 KB ratio | Block penalty | Notes |
|---|---:|---:|---:|---|
| `ForwardEdgeBuckets` | 362.1 | **4.69x** | **−4.0%** | block *beats* contiguous |
| `ReverseEdgeBuckets` | 245.9 | **4.51x** | 5.3% | |
| `ForwardEdgeDirectories` | 122.7 | **4.95x** | 0.2% | |
| `ReverseEdgeDirectories` | 107.0 | **5.27x** | 0.5% | |
| `ObjectAddresses` | 117.0 | 7.06x | −1.4% | **35.60x with delta** |
| `ObjectMethodTables` | 117.0 | 57.25x | 38.9% | |
| `ObjectSizes` | 117.0 | 44.17x | 18.4% | |
| `ObjectGenerations` | 14.6 | 3419x | 85.4% | already negligible |
| `DominatorImmediateDominatorAddresses` | 53.5 | 9.68x | −5.5% | delta adds nothing (1.06x) |
| `DominatorRetainedBytes` | 53.5 | 24.69x | 27.7% | **delta makes it worse (0.78x)** |
| `DominatorReachableAddresses` | 53.5 | 6.19x | 1.6% | **31.80x with delta** |
| `DominatorChildAddresses` | 51.8 | 4.18x | 4.4% | 9.27x with delta |
| `DominatorChildOffsets` | 26.7 | 7.85x | −0.7% | |
| `StringDedup` | 21.4 | 3.02x | 6.1% | |

**Whole file, measured ratios applied per section: 1,466.3 MB → 239.9 MB (83.6% reduction).**

### 21-04 dump (27.52 GB / 87.1M objects)

| Section | Raw MB | 64 KB ratio | Block penalty |
|---|---:|---:|---:|
| `ReverseEdgeBuckets` | 3,064.2 | 3.67x | 2.5% |
| `ForwardEdgeBuckets` | 2,516.2 | 3.01x | 13.1% |
| `ReverseEdgeDirectories` | 1,367.6 | 4.62x | 0.6% |
| `ForwardEdgeDirectories` | 721.6 | 4.24x | 0.5% |
| `ObjectAddresses` | 696.8 | 5.78x | 1.1% (**22.83x with delta**) |
| `ObjectMethodTables` | 696.8 | 33.03x | 42.8% |
| `ObjectSizes` | 696.8 | 23.01x | 29.7% |
| `StringDedup` | 31.8 | 2.54x | 7.3% |

**Whole file: 9,881.5 MB → 2,324.5 MB (76.5% reduction).**

### What this settles

1. **§5's 2.5–3x guess was low.** Measured whole-file is 4.25x–6.1x. Block compression alone
   delivers more than §2–§4 combined (45.7% projected), on both dumps.
2. **The block penalty is near-zero exactly where it matters.** For the four edge-index sections —
   57.1% and 77.6% of the two files — the penalty is 0.2–5.3%, and negative in two cases (64 KB
   blocks compressed *better* than a 32 MB run, because a smaller window suits this data's local
   regularity). §5's central premise is confirmed, not merely plausible.
3. **Block size: 64 KB is the right default.** 16 KB is consistently worse; 256 KB helps only the
   already-hyper-compressible base columns, which shouldn't be compressed at all (§4 below).
4. **zstd ≥ brotli here at equivalent effort**, and zstd-3 vs. zstd-9 differ little on the edge
   sections (4.29x vs 4.51x) — the cheap level is fine where the bytes actually are.

---

## 3. ⚠ OVERTURNS — delta encoding is not "captured by compression for free"

Format doc §6 lists address delta-encoding as "not recommended as a first cut," reasoning that
"most of the remaining redundancy these techniques target is exactly the kind of local statistical
regularity generic block compression already captures for free."

**Measured, that is false for sorted address columns**, by a large margin:

| Section | zstd9 alone | delta + zstd9 | Further gain |
|---|---:|---:|---:|
| `ObjectAddresses` (reference) | 6.57x | **35.60x** | **5.42x** |
| `ObjectAddresses` (21-04) | 5.38x | **22.83x** | **4.24x** |
| `DominatorReachableAddresses` | 6.21x | **31.80x** | **5.12x** |
| `DominatorChildAddresses` | 4.31x | 9.27x | 2.15x |
| `DominatorImmediateDominatorAddresses` | 7.96x | 8.40x | 1.06x |
| `DominatorRetainedBytes` | 22.78x | 17.72x | **0.78x — worse** |

The split is explained by sortedness, and predicts correctly: the two sorted columns gain ~5x,
the unsorted `idom` column gains nothing, and the heavy-tailed `RetainedBytes` column is actively
harmed. So delta belongs on **sorted address columns only** and should move from §6's
"considered, not recommended" list into the recommended path. Conversely §6's third bullet
(varint/checkpointed `RetainedBytes`) is contradicted by its own measurement — compression alone
already gets 24.69x there.

---

## 4. ⚠ OVERTURNS — compressing the base columns is not viable

Format doc §7 applies §5's compression uniformly to "everything above." The § 5.1 caveat added in
review argued the base object columns are streamed, not point-queried, so compressing them trades
against the zero-copy read path. That is now measured, on the reference `cache.bin` with warm pages:

| Path | Throughput |
|---|---:|
| Zero-copy `Unsafe.ReadUnaligned` scan (today) | **10.49 GB/s** |
| Brotli-5 64 KB block decompress | **0.47 GB/s** |
| **Ratio** | **22.2x slower** |

Even allowing zstd's ~3–5x decompression advantage over brotli, compressed streaming lands ~5x
slower than the current path, and cold-cache arithmetic doesn't rescue it: at 6.9x compression the
I/O saved (365 MB → 53 MB) is worth ~100 ms on a fast NVMe while the decompression added costs
~780 ms.

**Conclusion, as far as *this* measurement goes:** do **not** compress the four base object
columns. For those, the zero-copy-compatible techniques are the right tools: dictionary encoding
for `ObjectMethodTables` (§3) and delta encoding for `ObjectAddresses` (§3 above).

> **⚠ The corollary — "so compress the point-lookup sections instead" — does not follow, and an
> earlier version of this section asserted it.** This measurement establishes only that
> *streaming* access is incompatible with compression. It says nothing about whether *random
> point-lookup* access tolerates it, because that depends on lookup volume and block locality,
> neither of which was measured here. Scrutiny afterwards found both are unfavourable for the edge
> index — the reverse-edge index serves per-node `TryGetParents` calls from BFS traversals, with
> hash-scattered bucket assignment defeating block reuse. See
> [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md) §7.2.1, and § 7
> below. The size numbers in § 2 stand; the *recommendation* that appeared here did not.

This also resolves the §3-versus-§5 double-count in §7: compression would get 57.25x on
`ObjectMethodTables` versus dictionary encoding's 4x, so as *size* levers they are substitutes and
adding them is wrong — but since that column must stay uncompressed to protect streaming,
dictionary encoding is the correct choice there on access-pattern grounds, and §5 simply does not
apply to it.

---

## 5. Implementation side — the per-open checksum, quantified

Replicating `VerifyChecksumZeroCopy` and the column scan against the reference `cache.bin`, warm
pages (so this is CPU cost, not first-touch I/O — the honest figure for the repeated-open case
that [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)
§ 1 is about):

| Column | MB | Verify | Verify throughput | Raw scan |
|---|---:|---:|---:|---:|
| `ObjectAddresses` | 117.0 | 16.9 ms | 6.92 GB/s | 9.8 ms |
| `ObjectMethodTables` | 117.0 | 24.3 ms | 4.81 GB/s | 9.2 ms |
| `ObjectSizes` | 117.0 | 25.2 ms | 4.64 GB/s | 14.2 ms |
| `ObjectGenerations` | 14.6 | 2.3 ms | 6.30 GB/s | 1.7 ms |
| **Total per open** | **365.5** | **68.8 ms** | | **34.8 ms** |

**Every `EnumerateIndexedEntries()` call spends 68.8 ms verifying a checksum — 197% of the 34.8 ms
scan that verification gates.** Verification is not overhead on the read; it is twice the read.
It is recomputed on every open, on a file that cannot have changed since `Finish()` renamed it.

On the 21-04 cache the same four columns total 2,177.6 MB, scaling this to roughly 410 ms per open.

Memoizing per session (implementation doc § 6.1) eliminates all of it after the first open. This
is now the best-evidenced single change across either document.

---

## 6. The run-level multiplier — derived statically, no dump run needed

The missing number was "how many times per run is each section opened?" It turns out this is
answerable statically, because **the pipeline has no analyzer selection**: `CreateAnalyzers()` takes
no filter argument and materializes every registered analyzer, and the `AnalysisProfile`
Fast/Balanced/Full tiers are gone. Every analyzer runs on every dump. So enumerating call sites and
their loop structure gives the real count, not merely a bound.

### 6.1 Full inventory of object-column opens (reference dump, 8-core machine)

Each entry below costs one full `TryOpenColumns` — 365.5 MB checksummed, **68.8 ms**.

| # | Site | Opens | Note |
|---|---|---:|---|
| 1 | `HeapIndexScanDispatcher:284` sequential pass | 1 | |
| 2 | `HeapIndexScanDispatcher:441` parallel pass | **8** | **one per worker** — see § 6.2 |
| 3 | `AsyncStateMachineAnalyzer:215` `.Any()` probe | 1 | reads **one record** |
| 4 | `AsyncStateMachineAnalyzer:217` real pass | 1 | |
| 5 | `TimerLeakAnalyzer:270` `.Any()` probe | 1 | reads **one record** |
| 6 | `TimerLeakAnalyzer:272` real pass | 1 | |
| 7 | `WeakReferenceAnalyzer:392` `.Any()` probe | 1 | reads **one record** |
| 8 | `WeakReferenceAnalyzer:398` real pass | 1 | |
| 9 | `ReferenceChainAnalyzer:586` `.Any()` probe | 1 | reads **one record** |
| 10 | `ReferenceChainAnalyzer:588` real pass | 1 | |
| 11 | `EventLeak/PublisherRegistry:145` | 1 | correct guard |
| 12 | `EventLeakAnalyzer:869` | 1 | correct guard |
| 13 | `DominatorAnalyzer:784` `EnumerateLeakEntries` | 1 | correct guard |
| 14 | `DominatorAnalyzer:353` `ComputeCrossTypeOverlap` | 1 | |
| | **Total** | **≈20** | |

**≈20 opens × 68.8 ms ≈ 1.38 seconds of pure redundant checksum verification per run** on the
reference dump. Scaling by column size on the 27.5 GB dump's cache (2,177.6 MB, ~410 ms/open):
**≈8.2 seconds per run.**

All of it is recomputing the same hashes over a file that cannot have changed since `Finish()`
renamed it into place.

### 6.2 ⚠ The parallel dispatcher checksums the whole file once per worker

`HeapIndexScanDispatcher:441` calls `EnumerateIndexedEntriesRange(rangeStarts[w], rangeCounts[w])`
inside `Parallel.For(0, workerCount, ...)`. Each worker reads a **disjoint** range — but
`ReadDiskEntriesRange` funnels into the same `TryOpenColumns`, which calls
`TryOpenSectionAccessor` and therefore `VerifyChecksumZeroCopy` over the **entire section**,
ignoring the requested range completely.

`workerCount = min(ProcessorCount, objectCount / 250_000)` — on this 8-core machine with 14.6M
objects that is 8, and on a 32-core build agent it would be 32. So the parallel scan phase alone
hashes 8 × 365.5 MB = 2.9 GB to read 365.5 MB of disjoint ranges once.

The eight run concurrently and will saturate memory bandwidth rather than costing 8× wall-clock, so
the true figure is somewhere between 1× and 8× of 68.8 ms — but the work is unambiguously wasted,
and it scales with core count, meaning **a bigger machine makes this worse, not better**.

### 6.3 ⚠ Four opens exist solely to evaluate a boolean

`AsyncStateMachineAnalyzer`, `TimerLeakAnalyzer`, `WeakReferenceAnalyzer` and
`ReferenceChainAnalyzer` all share this idiom:

```csharp
bool hasDiskIndex = cache.EnumerateIndexedEntriesAsTuples().Any();
IEnumerable<...> entries = hasDiskIndex ? cache.EnumerateIndexedEntriesAsTuples() : LiveHeapEntries(heap);
```

`.Any()` opens the container, maps four columns, checksums all 365.5 MB, reads **one record**, and
disposes — then the real enumeration immediately does all of it again. That is **68.8 ms to answer
"does a disk index exist?"**, four times per run (≈275 ms), plus four redundant re-opens.

The correct idiom already exists in this codebase and is used two files away, with a comment
explaining exactly why: `PublisherRegistry:143` and `EventLeakAnalyzer:869` both test
`cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)` — an in-memory null check costing
nothing. `DominatorAnalyzer:782` uses it too. Converting the four `.Any()` sites to that guard is a
one-line-each change requiring no redesign at all, and it is independent of everything else in
either design doc.

### 6.4 Honest limits of this derivation

- Some sites sit behind preconditions (`weakRefTypesByMt.Count > 0`, `periodFieldByMt.Count > 0`,
  a candidate-type count ≥ 2). These are satisfied on any real application heap, so ≈20 is a
  realistic count rather than a purely theoretical ceiling — but it is a worst case in the strict
  sense, as the user framing intended.
- The dominator sites additionally require Stage B to have been gated on for the run.
- `QueryEngine:67` adds one open per query; a standard report run issues none.
- Concurrency means the 8 dispatcher opens do not cost 8× wall-clock (§ 6.2).

None of these caveats change the conclusion: the redundant verification is on the order of a
second per run on a mid-size dump and several seconds on a large one, it grows with core count,
and roughly a fifth of it buys four booleans.

---

## 7. Open questions this pass did *not* close

Recorded so the boundary of the evidence is explicit. Everything in §§ 1–6 is measured or
statically derived; everything here is not, and no plan should assume an answer.

| # | Question | Blocks | Why it isn't answered here |
|---|---|---|---|
| 1 | **What is `TryGetParents` call volume and block hit-rate under a real root-path workload?** | Compression of the edge index — i.e. the single largest size lever | Needs an instrumented traversal against a real dump. § 2 measured compression *ratios*, never lookup *cost*. See [format doc §7.2.1](cache-format-clean-slate-redesign.md) |
| 2 | Does one shared `MemoryMappedFile` serve 8–32 concurrent readers as well as today's per-open mappings? | [impl doc § 6.1](cache-implementation-clean-slate-redesign.md) | Requires the session to exist before it can be benchmarked |
| 3 | Does `DominatorImmediateDominatorAddresses` carry rows for folded leaves? | [format doc §4](cache-format-clean-slate-redesign.md)'s aggressive option | Answerable by reading the writer; not done in this pass |
| 4 | How often is the dominance-chain-tree UI actually exercised per build? | Same | Usage data, not code |
| 5 | What does CSR cost/save *after* compression, rather than instead of it? | [format doc §7.1](cache-format-clean-slate-redesign.md) item 5 | Requires a CSR prototype to compress |

Question 1 is the one that matters. It is cheap to answer relative to what it gates: a counter on
`TryGetParents` plus a simulated block-index histogram over the existing `cache.bin`, on the dump
already used throughout this doc. It should be settled before any compression work starts, not
after.

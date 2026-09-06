# Cache Redesign — Measured Evidence

Hard measurements backing [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md) and [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md). Both docs previously rested on one dump and an explicitly-flagged guess for compression; everything below is measured.

**Note:** This doc uses **MB = 10⁶ bytes**; the redesign docs use **MiB = 1024²** (4.9% apart). Reference `cache.bin` is 1,466.3 MB = 1,398.3 MiB. Ratios and byte counts are given together so either unit can be re-derived. Method: Five real `cache.bin` files parsed directly (no dump loads); compression via offline column scans.

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

**Conclusion:** Do **not** compress the four base object columns. Use dictionary encoding for `ObjectMethodTables` and delta encoding for `ObjectAddresses` instead — both are zero-copy compatible. Compression of point-lookup sections (edge index) is a separate question with different access patterns; see § 8 and format doc §7.2.1.

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

How many times per run is each section opened? All analyzers run by default (opt-in filtering only via `--include-analyzers`/`--exclude-analyzers`), so this is the worst-case count.

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

---

## 7. Verified on a real run (2026-09-04) — § 6.1 measured end-to-end

The predictions in § 5/§ 6 were per-open cost × statically-derived open count. Both have now been
observed directly, on the reference 3.3 GB dump (cache-hit path), by instrumenting
`CacheContainerReader` (`DD_PERF_CACHE_SESSION=1`) and running the real CLI twice — once with the
§ 6.1 memoization active and once with it bypassed, same dump, same build.

| | Section opens | Verified | Skipped | **Bytes hashed** | Wall clock |
|---|---:|---:|---:|---:|---:|
| Before § 6.1 (memoization bypassed) | 82 | 82 | 0 | **5,904.3 MiB** | 51.7 s |
| After § 6.1 | 82 | 28 | **54** | **1,270.8 MiB** | 51.4 s |

**The prediction held.** § 6.1 predicted ≈20 object-column enumerations; the run shows 82 *section*
opens, and since each column enumeration opens four sections that is ≈20.5 enumerations — the static
derivation in § 6 was accurate.

**Redundant hashing is down 78.5%** — 4,633.5 MiB of XxHash32 work eliminated per run. At the § 5
measured verify throughput (4.6–6.9 GB/s, ~5.5 GB/s weighted) that is **≈0.88 s of CPU**, against
the ≈1.38 s predicted. The prediction was ~36% high because it assumed every one of the ≈20 opens
was a full four-column group; several were smaller satellite sections.

**But wall clock moved 0.3 s on a 51 s run, which is inside run-to-run noise at n=1.** The work
eliminated is real, large, and precisely measured; its user-visible effect on this dump is ~1.7% and
cannot be distinguished from variance without repeated runs. Stated plainly rather than rounded up
into a speedup claim.


---

## 8. ⚠ OVERTURNS — the reverse-index compression risk was three orders of magnitude too pessimistic

[format doc § 7.2.1](cache-format-clean-slate-redesign.md) argued that block-compressing the edge
index would be a runtime disaster: "potentially millions of lookups", "a BFS touching 100,000 nodes
with one uncached block each costs **3–14 seconds**", and "locality does not rescue it" because
bucket assignment is by hash of the child address. That was reasoning, not measurement. Measured, on
the reference 3.3 GB dump, with `TryGetParents` instrumented to record which 64 KB block of
`ReverseEdgeBuckets` each lookup lands in (`DD_PERF_REVERSE_BLOCKS=1`):

| | Measured |
|---|---:|
| `TryGetParents` calls in a full run | **8,851** (245 of them misses) |
| Data-block touches | 8,606 |
| **Distinct 64 KB blocks touched** | **710** — 46.5 MB, i.e. **18.9%** of the 245.9 MB section |
| Distinct directory blocks | 7 (0.5 MB of 107.0 MB) |
| **Touches per block** | **12.1** |

**Both halves of the objection were wrong.** Call volume is ~8.8 thousand, not millions — the BFS
does not perform one parent lookup per heap object. And locality is *good*, not poor: the working
set is 710 blocks, so hash-scattering is irrelevant because nearly everything fits in a small cache.

LRU simulation over the real trace:

| Block cache | Cache MB | Misses | Hit rate | Decompressed MB | brotli @0.47 GB/s | zstd @2 GB/s |
|---|---:|---:|---:|---:|---:|---:|
| none | 0 | 8,606 | 0% | 564.0 | 1,200 ms | 282 ms |
| 64 blocks | 4.2 | 2,497 | 71.0% | 163.6 | 348 ms | 82 ms |
| **256 blocks** | **16.8** | 1,042 | **87.9%** | 68.3 | **145 ms** | **34 ms** |
| 710 blocks | 46.5 | 710 | 91.7% | 46.5 | 99 ms | 23 ms |

91.7% is the ceiling — every distinct block must be decompressed once. **A 16.8 MB block cache puts
the cost at 34 ms with zstd**, against the 3–14 s § 7.2.1 predicted. Compressing the reverse-edge
index is viable, and the "unbreakable runtime cost" objection is withdrawn.

### 8.1 What this does *not* settle

- **Scale.** This is the 3.3 GB dump, whose reverse section is 245.9 MB. On the 27.5 GB dump it is
  3,064.2 MB — 12x — and more leak candidates plausibly means more lookups and a larger working set.
  The 710-block figure is not known to hold there. Same instrumentation, one run, would tell.
- **The forward index is a different access pattern and was not traced.** `ForwardEdgeBuckets` is
  362.1 MB here and 2,516.2 MB on the 27.5 GB dump — 33% of both files, i.e. *larger* than the
  reverse index — and its consumer is graph traversal rather than isolated point lookups. If it
  turns out to be streamed end-to-end, § 4's 22x zero-copy penalty applies to it exactly as it does
  to the base columns, and it should not be compressed. This is now the single biggest open question
  about compression, and it displaces the one this section just closed.

---

## 9. ✅ The `ForwardEdge*` sections were write-only — 33% of `cache.bin`, now removed

Open question 1b asked whether `ForwardEdgeBuckets` is streamed or point-queried, so we could decide
whether compression's zero-copy penalty (§ 4) applies to it. **The answer is neither: nothing reads
it.** Found by tracing the consumer chain rather than the access pattern.

The full chain, verified by whole-repo search:

- `ForwardEdgeBuckets` / `ForwardEdgeDirectories` / `ForwardEdgeMetadata` are written into the
  container by Phase C of every build.
- The only way to read them is `ForwardEdgeIndexReader`, reachable only via
  `IForwardReferenceProvider`, reachable only via `IHeapAnalysisCache.TryGetForwardIndexProvider()`.
- **`TryGetForwardIndexProvider()` has exactly three references in the entire repository**: its
  declaration on the interface, its implementation on `HeapAnalysisCache`, and a test stub that
  throws `NotSupportedException`. **Zero production callers.**

Forward-edge *extraction* is essential and is not in question — Stage A's reachability walk consumes
it, and that path measured ~2x faster than a live ClrMD walk on a 25 GB dump. But the walk reads the
**loose `.dat`/`.idx` scratch files**, not the container. `DiskBackedObjectIndexWriter` says so
directly: *"Merging them into the container (Phase C) happens after the walk, once the loose files
are no longer needed as a successors source."* The container copy exists to serve *later* runs, and
no later run asks for it.

| | `ForwardEdge*` total | Share of `cache.bin` |
|---|---:|---:|
| Reference 3.3 GB dump | **462.4 MiB** | **33.1%** |
| 21-04 27.5 GB dump | **3,087.8 MiB** | **32.8%** |

So a third of the cache file — 3.0 GiB on the largest real dump — is written every build, costs
Phase C merge I/O to produce, and is read by nothing. This is the `EventCandidates` situation (§ 1.1)
at roughly 10,000x the size.

### 9.1 Runtime proof, and a second write-only section

The static finding above was checked against a real run rather than left as a search result. The
per-section tally was extended to list every section the run *touched* and every one it never did
(`DD_PERF_CACHE_SESSION=1`, full default analyzer set, cache-hit path, reference dump):

> sections **TOUCHED**: TypeAggregates, Roots, Handles, Tasks, LargeObjects, LohFreeBlocks,
> StringDedup, StringDedupMeta, ObjectAddresses, ObjectMethodTables, ObjectSizes, ObjectGenerations,
> ReverseEdgeBuckets, ReverseEdgeDirectories, ReverseEdgeMetadata, SegmentIndex,
> DominatorReachableAddresses, DominatorImmediateDominatorAddresses, DominatorChildOffsets,
> DominatorChildAddresses, DominatorTreeMetadata, DominatorRetainedBytes
>
> sections **NEVER touched**: Objects, EventCandidates, **ForwardEdgeBuckets**,
> **ForwardEdgeDirectories**, **ForwardEdgeMetadata**, **RootStackThreadAttribution**

All 22 sections that have a reader were opened. `Objects` and `EventCandidates` are expected —
neither is written. That leaves four sections that **are** written and never read, confirming the
static result and adding one it had not flagged:

| Never read | Bytes (reference dump) | MiB |
|---|---:|---:|
| `ForwardEdgeBuckets` | 362,117,408 | 345.3 |
| `ForwardEdgeDirectories` | 122,747,944 | 117.1 |
| `ForwardEdgeMetadata` | 888 | 0.0 |
| `RootStackThreadAttribution` | 15,560 | 0.01 |

`RootStackThreadAttribution` is a different case and **not** a defect: it and its
`IThreadRetentionProvider` shipped for § 12.2, whose `ThreadAnalyzer` report-surface wiring was
*deliberately deferred*. It is 15.5 KB, so it costs nothing — worth leaving as-is, and worth knowing
it is unwired so the provider isn't mistaken for live code.

**Scope of this evidence.** It shows those sections are never opened on a full default run of every
analyzer over this dump. It cannot prove no code path anywhere would open them — but the static
search independently shows `TryGetForwardIndexProvider()` has zero production callers at all, which
covers every CLI mode, so the two lines of evidence agree.

### 9.2 Resolved — the container merge was removed (2026-09-05)

**Shipped.** Phase C no longer merges the loose forward-edge files into the container; the caller
deletes them once Stage A's walk is done, which was already their only consumer. Extraction is
untouched, so the walk keeps its ~2x advantage over a live ClrMD walk.

Verified by a cold rebuild of the reference dump followed by a cache-hit run:

| | Sections | `cache.bin` |
|---|---:|---:|
| Before | 26 | 1,466,259,011 B — 1,398.3 MiB |
| After | **23** | **981,392,729 B — 935.9 MiB** |
| **Saved** | 3 | **484,866,282 B — 462.4 MiB (33.1%)** |

The saving lands within 42 bytes of the 484,866,240 B predicted from the TOC. Also confirmed:
`ForwardEdge*` absent from the new TOC; `ReverseEdge*` and all six `Dominator*` sections still
present and still read, so the reachability walk and Stage B are unaffected; the index directory
contains only `cache.bin`, i.e. no leaked `.dat`/`.idx` scratch; and a subsequent cache-hit run is
behaviourally identical (12 container opens, 82 section opens, 936.2 MiB hashed — unchanged, since
these sections were never verified anyway) with the report rendering normally.

The three ids are now `Unused` in `CacheSectionCatalog`, reserved but never expected.
`ForwardEdgeContainerWriter` stays in place and stays covered by `ForwardEdgeIndexTests`, so
restoring the merge for a future cache-hit-time consumer is one call plus a rebuild — which such a
consumer would need regardless.

Containers written before this change still carry the sections. That is harmless: nothing reads
them, and they are classified `Unused` rather than `Required`, so no fast-path check rejects them.

#### Options considered

Two coherent options, and the choice was a product call:

1. **Stop persisting the three sections.** Immediately removes 33% of `cache.bin` and the Phase C
   merge — a bigger, cheaper, more certain win than anything in the format redesign, with no
   encoding work and no format redesign needed. The `CacheSectionId` slots stay reserved (never
   renumber). Cost: adding a forward-edge consumer later would require a rebuild to repopulate.
2. **Keep them and add the consumer they were built for.** Only if a real analyzer need exists —
   otherwise this is the "no half-finished implementations" convention being violated at 3 GiB.

**Note on sequencing:** deleting `DD_SKIP_FORWARD_INDEX_BUILD` (§ 6.2.1) made this section
unconditional, on the reasoning that the feature is core. That reasoning holds for the *extraction*
— the walk depends on it — but not for the *container persistence*, which this section shows nobody
reads. Worth correcting rather than leaving implied.

Also note this changes the compression arithmetic: if the sections stop being written, the
`ForwardEdge*` rows drop out of § 2's table entirely, and compression's headline applies to a file
that is already a third smaller.

---

## 10. Post-removal composition — the ranking inverted

Every percentage earlier in this doc predates § 9.2. With the `ForwardEdge*` sections gone the
reference `cache.bin` is 935.9 MiB across 23 sections, and **the base columns are now the largest
group, not the edge index**:

| Group | Before (1,398.3 MiB) | After (935.9 MiB) |
|---|---:|---:|
| Base columns | 348.6 MiB — 24.9% | **348.6 MiB — 37.2%** |
| Edge index | 837.8 MiB — 57.1% | 336.6 MiB — 36.0% |
| Dominator tree | 228.4 MiB — 16.3% | 228.4 MiB — 24.4% |
| StringDedup | 20.4 MiB — 1.5% | 20.4 MiB — 2.2% |
| TypeAggregates + satellites | 1.9 MiB — 0.1% | 1.9 MiB — 0.2% |

Largest individual sections now: `ReverseEdgeBuckets` 234.5 (25.1%), then
`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes` at 111.5 each (11.9% each),
then `ReverseEdgeDirectories` 102.0 (10.9%).

### 10.1 Non-compression levers, sized against the current file

> **⚠ Sizing superseded by § 13.1.** This table predates the format-v5 `MethodTable` dictionary
> (§ 11), so its denominator is 935.9 MiB and it still lists that lever as pending. § 13.1 re-sizes
> the whole list against the 852.4 MiB file that exists today, and the two address/size levers below
> are costed there from real column data rather than from record-width arithmetic. Keep this table
> for the CSR and dominator rows; take the base-column rows from § 13.1.

| Lever | Saves | % of 935.9 MiB | Notes |
|---|---:|---:|---|
| CSR for the reverse edge index (§2) | ~245 MiB | ~26% | Largest, but also the largest build; needs the §2.2.1 scratch-file resolver. Deletes `ReverseEdgeDirectories` outright |
| Dominator, aggressive — drop the child list (§4) | ~99 MiB | ~10.6% | Gated on the folded-leaf question and chain-tree UI usage |
| `MethodTable` dictionary encoding (§3) | ~83.6 MiB | ~8.9% | 8 → 2 bytes over 14,003 distinct types; zero-copy compatible, needs no compression decision |
| Dominator, conservative narrowing (§4) | ~48 MiB | ~5.1% | Alternative to the aggressive option, not additive |
| `ObjectAddresses` fixed-width delta | ~55.8 MiB | ~6.0% | 8 → 4 bytes within a segment; needs checkpoints so `ObjectAddressLookup`'s binary search stays O(log n) |

**Careful with the delta figure.** § 3 of this doc measured 35.60x on `ObjectAddresses`, but that was
**delta *plus* zstd**. Standalone, a fixed-width 4-byte delta is a 2x saving — 55.8 MiB, as above.
The 5.42x multiplier only materialises once compression ships, so delta's headline value is tied to
the item being deferred.

### 10.2 Format changes should be batched

`MethodTable` dictionary encoding, `ObjectAddresses` delta, CSR, the § 3 section manifest, and
promoting `RootStackThreadAttribution` to `Required` are all breaking on-disk changes. Each one
alone forces a `CacheFileHeader.CurrentFormatVersion` bump, which invalidates every cache on disk —
a full cold re-index, ~2 minutes on the 3.3 GB dump and considerably more on the 27.5 GB one.

Doing them in separate releases pays that cost repeatedly for no benefit. Whatever is picked first
should carry the bump, and the cheap riders (the manifest, the `RootStackThreadAttribution`
promotion) should go in the same one.

---

## 11. ✅ Format v5: `MethodTable` dictionary encoding

Dictionary-encoded 8-byte `MethodTable` column to 2-byte `TypeId` (14,003 types). `cache.bin` 935.9 → 852.4 MiB (−83.6 MiB, −8.9%). Flat `ulong[]` lookup in hot path; width chosen at write time per distinct-type count. Verified cold rebuild + cache-hit equivalence.

---

## 12. Remaining open: compression vs. CSR trade-off

All non-compression levers are shipped (v5–v8). The last remaining design question is [format doc §7.1](cache-format-clean-slate-redesign.md): **what does CSR cost/save once compression is also applied?** Requires a CSR prototype with block compression to measure, deferred until compression work begins.

---

## 13. ✅ Format v6: Base-column narrowing + manifest

Three measured levers (§ 10.1), batched into one format bump per [format doc §10](cache-format-clean-slate-redesign.md):

- **`ObjectSizes`**: 8 B → 2 B (max value 24 bits on both dumps, 0.026% escape rate) — 83.6 MiB saved
- **`ObjectAddresses`**: 8 B → 4 B block-delta (global monotonicity, N=1,024 checkpoints) — 55.7 MiB saved  
- **`DominatorReachableAddresses`**: 8 B → 4 B block-delta — 25.5 MiB saved
- **Section Manifest** (new): closes fast-path gap for aborted writes

**Result**: 852.4 → 687.7 MiB (−164.7 MiB, −19.3%). Verified cold rebuild + full correctness suite on real dump (14.6M objects, all three widths, overflow tables, escape cursors).

---


---

## 14. ✅ Format v7: Dominator child index derived on demand

Measured `EnumerateRetainedSet` call frequency on 3 real dumps: **zero calls across all three** (0-for-1,411/742/5,037 roots). Aggressive dominator option shipped: drop persisted child index, narrow `idom[]` to row indices, invert on demand. Format version 6 → 7.

**Result**: 687.7 → 587.29 MiB (−100.4 MiB, −14.6%). **Cumulative from start: 1,398.3 → 587.29 MiB (42.0%), no compression yet.** Verified with exhaustive round-trip on real dump (6.69M rows, independent parent/child cross-check).

---

## 15. ✅ Format v8: True CSR reverse-edge index

Replaces hash-bucket-sort-directory with compressed sparse row (offsets + row indices, keyed by reachable-node order). Format version 7 → 8.

**Result**: 587.29 → 342.50 MiB (−244.79 MiB, −41.7%). Matches format doc §2.5 projection exactly. **Final: 1,398.3 → 342.50 MiB (24.5% of start), zero compression applied.** Verified internal consistency (17.37M edges counted exhaustively) and sampled live cross-check (41K edges, 0 mismatches).

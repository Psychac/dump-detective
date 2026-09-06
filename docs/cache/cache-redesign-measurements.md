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

The missing number was "how many times per run is each section opened?" It is answerable
statically, because **every analyzer runs by default**: `CreateAnalyzers()` takes no filter and the
`AnalysisProfile` Fast/Balanced/Full tiers are gone.

Correction to an earlier version of this paragraph, which claimed the pipeline has *no* analyzer
selection at all: it does. `AnalyzerFilterService.Apply` honours `--include-analyzers` and
`--exclude-analyzers`, and both are empty unless the user sets them, so filtering is strictly
opt-in. The count below is therefore the **default-configuration** count, which is also the worst
case — narrowing the analyzer set can only remove opens, never add them. The conclusion is
unchanged; the reasoning behind it was wrong.

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

### 7.1 Follow-up: the residual, and why it is now closed

The first verified run left 1,270.8 MiB still hashed and 14 container opens. That looked like a
large remaining opportunity — **it was not**, and measuring before building is what caught it.

A per-section verification tally (`DD_PERF_CACHE_SESSION=1`) showed only six sections were ever
hashed twice, and three of those were trivial:

| Section verified twice | Redundant MiB |
|---|---:|
| `ObjectAddresses` | 111.54 |
| `ObjectMethodTables` | 111.54 |
| `ObjectSizes` | 111.54 |
| `Handles` | 0.23 |
| `Roots` | 0.03 |
| `LargeObjects` | 0.00 |
| **Total redundant** | **334.89** |

So of the 1,270.8 MiB, only **334.9 MiB (26.4%) was redundant** — worth ≈64 ms at the measured
throughput. The other **935.9 MiB is irreducible**: each section verified exactly once, which is the
floor for this design. An earlier note here implied the whole 1,270.8 MiB was addressable; it wasn't.

**And 99.9% of the redundancy had a single cause** — `ObjectAddressLookup` opened its own
`CacheContainerReader` (two, counting `SegmentIndexWriter.ReadRecords`) and re-verified the three
object columns the run's session had already checked. Routing it through the session fixed it:

| | Container opens | Verified | **Bytes hashed** | Sections hashed twice |
|---|---:|---:|---:|---|
| After § 6.1 | 14 | 28 | 1,270.8 MiB | 6 |
| After routing `ObjectAddressLookup` | **12** | **25** | **936.2 MiB** | 3 (`Roots`, `Handles`, `LargeObjects`) |

936.2 MiB against a predicted floor of 935.9 — the model is exact. **Remaining redundancy is
0.26 MiB across three tiny sections, which is not worth another change.** Twelve container opens
remain, but they no longer cause meaningful duplicate hashing, so the open *count* is now a
tidiness question rather than a cost one.

Wall clock across these runs was 51.4 s / 51.7 s / 57.1 s — dominated by machine variance, with the
slowest run being the one doing the *least* work. At n=1 per configuration it says nothing; bytes
hashed is the deterministic measure and is what the table above reports.

### 7.2 What the run revealed that the static analysis missed

**14 container opens remain.** § 6.1's session covers `HeapIndexCache` and — since § 6.4 — the five
provider caches, but fourteen `CacheContainerReader` instances still exist per run
(`ObjectAddressLookup`, `RootIndexReader`, `TaskIndexReader`, the handle readers,
`LohFragmentationAnalyzer`, `SegmentIndexWriter.ReadRecords`, …). **Memoization is per instance**, so
a section opened through two different readers is verified twice. That is why 28 verifications
occurred for a container holding ~26 sections, and why 1,270.8 MiB is still hashed — roughly the
whole file once, which is the floor for the current design rather than a bug.

Extending the session to the one site that mattered (`ObjectAddressLookup`) is done — see § 7.1.
The other eleven cost 0.26 MiB between them and are deliberately left alone.

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

## 11. ✅ `MethodTable` dictionary encoding shipped (format v5)

`ObjectMethodTables` stored an 8-byte `MethodTable` per object for a column with ~1,044x redundancy
(14,003 distinct types across 14.6M objects). It now stores a narrow `TypeId` indexing a new
`ObjectTypeDictionary` section. Format version bumped 4 → 5.

| | Bytes | MiB | Per record |
|---|---:|---:|---:|
| `ObjectMethodTables` before | 116,961,296 | 111.54 | 8.00 B |
| `ObjectMethodTables` after | 29,240,324 | 27.89 | **2.00 B** |
| `ObjectTypeDictionary` (new) | 112,024 | 0.11 | 8 B × 14,003 types |
| **`cache.bin`** | 981,392,729 → **893,783,813** | 935.9 → **852.4** | **−83.6 MiB (−8.9%)** |

Width is derived from the dictionary's entry count rather than stored in a flag — the writer picks
the narrowest width the distinct-type count allows, so the count determines it unambiguously
(2 bytes up to 65,535 types, 4 beyond).

**Design notes, both from § 3.1 of the format doc and both load-bearing:**

- The dictionary is a flat `ulong[]` indexed by `TypeId`, never a `Dictionary`. It is indexed once
  per object in `ZeroCopyColumnReader.FillBatch`, the hottest loop in the codebase, so a hash lookup
  there would be per-object cost. The width branch is also hoisted out of the batch loop.
- `TryOpenColumns`' cross-column record-count check had to be reworked: the MethodTable column no
  longer shares the 8-byte stride of Addresses/Sizes, so it is divided by its own width.

**Scratch files deliberately keep the full 8-byte pointer.** Narrowing during the parallel scan would
need the final distinct-type count before the scan has finished, and `ScratchFileObjectMetadataLookup`
reads those same files during Stage B. Converting at container-write time costs no extra pass — it
replaces a copy that already read every one of those bytes, and writes a quarter as many.

### 11.1 Correctness verification, and a pre-existing nondeterminism it exposed

Cold rebuild plus cache-hit run, with the full report payload decoded and compared field-by-field
against the pre-change run. Excluding timings, per-run GUIDs and memory counters, 79 fields differed
— all of them rows in *Object Shape Analysis*' "Gen2-retained types" table and two EventLeak
`rootHint` values.

Those are **not** a regression, and the check that establishes it is running the analysis twice
against the *same* v5 cache with the *same* binary: that produces differences in exactly the same two
places. In the table, the row multisets are identical — every value is preserved, only the order of
rows with **tied sort keys** differs (the swapped pair at rows 742/743 both have GC Scan Cost 224).

A second, independent line-level diff of the sorted JSON payloads agreed: 662 differing lines out of
2,172,224, all timings apart from a two-line delta. That delta resolves to the two EventLeak cards,
where `rootHint` is **present in one run and absent in the other** rather than holding a different
value — a sharper statement of the same nondeterminism, and the worse of the two symptoms.

So the encoding round-trips exactly, and separately: **the report is not reproducible across runs.**
Ties in at least two places break arbitrarily. That matters for the trend/diff feature, which would
surface spurious changes between two runs of the same dump. Recorded here rather than fixed — it
predates all of this work.

---

## 12. Open questions this pass did *not* close

Recorded so the boundary of the evidence is explicit. Everything in §§ 1–6 is measured or
statically derived; everything here is not, and no plan should assume an answer.

| # | Question | Blocks | Why it isn't answered here |
|---|---|---|---|
| 1 | ~~`TryGetParents` call volume and block hit-rate~~ **CLOSED — see § 8.** 8,851 calls, 710 distinct blocks, 87.9% hit rate on a 16.8 MB cache, 34 ms with zstd. The objection is withdrawn | — | — |
| 1b | ~~Is `ForwardEdgeBuckets` streamed or point-queried?~~ **CLOSED — § 9. Neither: it has zero production readers.** 33% of `cache.bin` is write-only | — | — |
| 2 | ~~Opens per run~~ **CLOSED** — see § 7. Predicted ≈20 enumerations, observed ≈20.5; hashing down 78.5%, wall clock within noise | — | — |
| 3 | ~~Does `DominatorImmediateDominatorAddresses` carry rows for folded leaves?~~ **CLOSED — § 13.3. Yes, every one of them** | — | — |
| 4 | ~~How often is the dominance-chain-tree UI actually exercised per build?~~ **WRONG QUESTION — § 13.3.** The chain tree never reads the child list; `StaticRootLeakDetector` does | [format doc §4](cache-format-clean-slate-redesign.md)'s aggressive option | Restated as "how many static-root candidates per run?", still open |
| 5 | What does CSR cost/save *after* compression, rather than instead of it? | [format doc §7.1](cache-format-clean-slate-redesign.md) item 5 | Requires a CSR prototype to compress |

Question 1 is the one that matters. It is cheap to answer relative to what it gates: a counter on
`TryGetParents` plus a simulated block-index histogram over the existing `cache.bin`, on the dump
already used throughout this doc. It should be settled before any compression work starts, not
after.

---

## 13. Base-column narrowing measured, and two code findings (2026-09-06)

Same method as §§ 2–4: the TOC and the raw columns were read straight out of the two real
`cache.bin` files with numpy. No dump was loaded, nothing was rebuilt.

### 13.1 Composition after format v5, and the lever list re-sized

The reference `cache.bin` is now **852.4 MiB across 24 sections**. § 10's table was written between
the `ForwardEdge*` removal and the `MethodTable` dictionary, so it is one step stale; this is where
the file actually stands:

| Group | Sections | MiB | Share |
|---|---:|---:|---:|
| Reverse edge index | 3 | 336.6 | 39.5% |
| Base columns (+ `ObjectTypeDictionary`) | 5 | 265.0 | 31.1% |
| Dominator tree | 6 | 228.4 | 26.8% |
| StringDedup (+ meta) | 2 | 20.5 | 2.4% |
| TypeAggregates + satellites | 8 | 1.9 | 0.2% |

Individually: `ReverseEdgeBuckets` 234.5, `ObjectAddresses` 111.5, `ObjectSizes` 111.5,
`ReverseEdgeDirectories` 102.0, then the three 51.0 MiB dominator scalar columns.

Every non-compression lever, re-sized against 852.4 MiB:

| Lever | Saves | % of 852.4 | Status |
|---|---:|---:|---|
| CSR reverse edge index (§2) | ~245 MiB | 28.7% | arithmetic only; largest build |
| Dominator aggressive — drop child list, narrow `idom` (§4) | ~98 MiB | 11.5% | precondition now verified (§ 13.3) |
| `ObjectSizes` narrowing (§ 13.2) | **83.6 MiB** | **9.8%** | **measured on both caches** |
| `ObjectAddresses` block-delta (§ 13.2) | **55.7 MiB** | **6.5%** | **measured on both caches** |
| *Dominator conservative narrowing (§4) — alternative to the aggressive row, not additive* | ~46 MiB | 5.4% | arithmetic only |
| `DominatorReachableAddresses` block-delta (§ 13.2) | **25.5 MiB** | **3.0%** | **measured (reference only)** |
| `ObjectGenerations` 1 byte → 2 bits | ~10.4 MiB | 1.2% | not recommended — breaks the fixed-stride zero-copy read for 1.2% |

The three measured rows are the ones compression can never reach, because § 4 rules compression out
for the streamed base columns and `DominatorReachableAddresses` is binary-searched. They are
therefore additive with everything in § 2, not substitutes for it.

### 13.2 `ObjectSizes`, `ObjectAddresses`, `DominatorReachableAddresses` — measured

**`ObjectSizes` — 8 bytes per object buys a value that never exceeds 24 bits on either dump.**

| Dump | Records | Max size | Not a multiple of 8 | Escapes at 2 B | Saved at 2 B | Escapes at 4 B | Saved at 4 B |
|---|---:|---:|---:|---:|---:|---:|---:|
| Reference (3.51 GB) | 14,620,162 | 19,117,318 | 1,947,966 (13.32%) | 3,843 (0.0263%) | **83.61 MiB** | 0 | 55.77 MiB |
| 21-04 (27.52 GB) | 87,104,236 | 23,340,790 | 17,960,061 (20.62%) | 31,760 (0.0365%) | **498.05 MiB** | 0 | 332.28 MiB |

Two things this settles. First, **no unit-of-8 scaling**: 13–21% of sizes are not multiples of 8, so
`size / 8` is lossy and the column has to store the value as-is. Second, **2 bytes is the right
width, not 4**: the escape rate is 0.026–0.037% on both dumps, three orders of magnitude below the
point where a per-record escape branch stops predicting, and the side table costs 5–372 KB. Choosing
4 bytes to avoid the escape mechanism gives up 27.8 MiB on the reference dump and 165.8 MiB on the
21-04 one for complexity that a sorted 12-byte side table does not actually have.

**`ObjectAddresses` — 8-byte-aligned and globally non-decreasing on both dumps.** Encoded as a raw
4-byte delta from a per-block base, one base per N records:

| Column | Dump | Records | N=256 | N=1,024 | N=4,096 |
|---|---|---:|---:|---:|---:|
| `ObjectAddresses` | Reference | 14,620,162 | 0 escaped / 55.34 MiB | **16 / 55.66 MiB** | 16 / 55.74 MiB |
| `ObjectAddresses` | 21-04 | 87,104,236 | 0 / 329.68 MiB | **0 / 331.63 MiB** | 0 / 332.11 MiB |
| `DominatorReachableAddresses` | Reference | 6,686,490 | 258 / 25.30 MiB | **1,246 / 25.44 MiB** | 4,318 / 25.45 MiB |

Escape counts are records, not blocks. N=1024 is the pick: 0.00011% escapes on `ObjectAddresses`
(all 16 at the column's single 3.94 GB inter-segment gap, the largest gap present), 0.019% on
`DominatorReachableAddresses`, and 114 KB / 664 KB of checkpoints respectively. The N sweep is flat
enough that the choice is not load-bearing.

**Scaling the delta by 8 was measured and rejected.** Both dumps' addresses are entirely 8-aligned,
so `(address − base) / 8` at 4 bytes reaches 34.4 GB per block and takes the reference dump's 16
escapes to zero. But a 32-bit dump's addresses are 4-byte aligned, where every second record would
escape — trading 16 measured records against half a column on an unmeasured but entirely real dump
class. `DominatorReachableAddresses` settles it independently: it is **not** 8-aligned even on this
dump, so a shared primitive cannot scale anyway.

Note the encoding does **not** depend on the column being sorted — a descending step simply produces
a delta that does not fit and escapes; global monotonicity is reported here because it was measured,
not because the design needs it. `DominatorReachableAddresses` is absent from the 21-04 cache
(Stage B was not run there), so that row is reference-dump only.

**Combined.** 83.61 + 55.66 + 25.44 = **164.7 MiB**, taking the reference `cache.bin` from 852.4 to
**687.7 MiB (−19.3%)**. On the 21-04 cache the two applicable levers total 829.7 MiB — 8.8% of that
file as it stands at 9,423.7 MiB, or **13.1% of the 6,335.9 MiB it would be** once the `ForwardEdge*`
removal (§ 9.2) is applied to it.

### 13.3 Two findings from reading the dominator code

**Open question 3 is closed: `DominatorImmediateDominatorAddresses` carries a row for every folded
leaf.** `DiskBackedObjectIndexWriter`'s per-row loop iterates `oldId` over all *n* rows, and the
`newId < 0` branch — the folded-leaf branch — writes the folding parent's address rather than
skipping the row. So inverting `idom[]` reproduces the persisted child list exactly, which is the
precondition [format doc §4](cache-format-clean-slate-redesign.md) flagged as needing confirmation
before the aggressive dominator option could be costed. It holds.

**Open question 4 was aimed at the wrong consumer.** Format doc §4 states that the dominance-chain
tree is the only consumer of the child-list direction. It is not a consumer at all: `DominatorAnalyzer`'s
chain detection walks *upward* via `TryGetImmediateDominator`. The child list's only production
consumer is `IDominatorTreeProvider.EnumerateRetainedSet`, and its only production caller is
`StaticRootLeakDetector`, which enumerates a candidate root's dominator subtree to build the
per-type/per-namespace retained breakdown. That reframes the question the aggressive option is
gated on: not "how often does someone open a UI", but "how many static-root candidates does a run
process, and is an O(R) in-memory inversion of `idom[]` acceptable at that frequency" — roughly
51 MiB of `int[]` at 6.69M rows, which is a bounded-memory question, not a usage question. A counter
on that one call site answers it.

### 13.4 What this makes v6

The three measured levers share one primitive — a narrow fixed-width column with an escape sentinel
and a sorted side table — and none of them touches a structural index, so they batch cleanly into a
single format bump per § 10.2. Spec in [format doc §10](cache-format-clean-slate-redesign.md);
CSR (§2) and the dominator decision (§4) stay out of it and take their own bumps later.

---

## 14. ✅ v6 part 1 — narrowed `ObjectSizes` shipped

The first of § 13.4's three levers is in. `ObjectSizes` stored a fixed 8 bytes per object; it now
stores the narrowest of 2, 4 or 8 that the dump's own size distribution allows, with the values that
don't fit moved to the new `ObjectSizeOverflow` section. Format version bumped 5 → 6.

Measured on a cold rebuild of the reference dump:

| | Bytes | MiB | Per record |
|---|---:|---:|---:|
| `ObjectSizes` before | 116,961,296 | 111.54 | 8.00 B |
| `ObjectSizes` after | 29,240,324 | 27.89 | **2.00 B** |
| `ObjectSizeOverflow` (new) | 46,116 | 0.04 | 3,843 escaped records — **0.0263%** |
| **`cache.bin`** | 893,783,813 → **806,108,957** | 852.4 → **768.8** | **−83.6 MiB (−9.8%)** |

The escape count came back at exactly the 3,843 predicted statically in § 13.2, and the chosen width
matches the prediction too — the writer's own scan counters reproduce what the offline column scan
found.

**Width selection is a writer decision, recovered by readers from the TOC.** Two counters per
segment during the heap scan (values ≥ 2¹⁶−1 and ≥ 2³²−1) feed a cost model at container-write time
that minimises `records × width + escapes × 12`, subject to an escape rate under 1% so the streaming
decoder's escape branch stays predictable. Nothing about the choice is stored: readers recover it as
`Length / RecordCount`, so a dump whose sizes genuinely need 8 bytes writes the pre-v6 column and is
read by the pre-v6 path with no flag anywhere.

### 14.1 Correctness verification

The escaped population is 0.026% of records, which is well under what the existing every-100,000th
sampling in `HeapAnalysisCacheObjectMetadataDiscrepancyTests` would be expected to hit even once.
A dedicated real-dump test (`NarrowSizeColumnRealDumpTests`) therefore checks **every** escaped
record rather than a sample: it builds a fresh index, reads back all 14.6M entries, and for each of
the 3,843 records at or above the sentinel compares the index's size against live
`ClrObject.Size`. Zero mismatches, and the count read back matches the overflow section's record
count exactly — a sentinel silently surviving as a real size is the failure mode that would
otherwise be invisible.

Also re-run green afterwards: `HeapAnalysisCacheObjectMetadataDiscrepancyTests` (disk mode,
in-memory mode and live `heap.GetObject` agreeing) and `ObjectAddressLookupDiscrepancyTests`, plus
1,045 unit tests including round-trip cases at all three widths, a mid-column range enumeration that
exercises cursor seeding, and a narrowed-column-without-its-overflow-section case that must be
rejected rather than degraded.

### 14.2 Still open in v6

`ObjectAddresses` and `DominatorReachableAddresses` block-delta encoding (§ 13.2, a further
55.7 + 25.4 MiB) and the section manifest rider
([format doc §10.5](cache-format-clean-slate-redesign.md)). Both ride this same version bump — the
cache is already invalidated, so they cost nothing extra to land now.

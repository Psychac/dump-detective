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

## 9. ⚠ The `ForwardEdge*` sections are write-only — 33% of `cache.bin` is never read

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

### 9.1 What to do about it — not decided here

Two coherent options, and the choice is a product call:

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

## 10. Open questions this pass did *not* close

Recorded so the boundary of the evidence is explicit. Everything in §§ 1–6 is measured or
statically derived; everything here is not, and no plan should assume an answer.

| # | Question | Blocks | Why it isn't answered here |
|---|---|---|---|
| 1 | ~~`TryGetParents` call volume and block hit-rate~~ **CLOSED — see § 8.** 8,851 calls, 710 distinct blocks, 87.9% hit rate on a 16.8 MB cache, 34 ms with zstd. The objection is withdrawn | — | — |
| 1b | ~~Is `ForwardEdgeBuckets` streamed or point-queried?~~ **CLOSED — § 9. Neither: it has zero production readers.** 33% of `cache.bin` is write-only | — | — |
| 2 | ~~Opens per run~~ **CLOSED** — see § 7. Predicted ≈20 enumerations, observed ≈20.5; hashing down 78.5%, wall clock within noise | — | — |
| 3 | Does `DominatorImmediateDominatorAddresses` carry rows for folded leaves? | [format doc §4](cache-format-clean-slate-redesign.md)'s aggressive option | Answerable by reading the writer; not done in this pass |
| 4 | How often is the dominance-chain-tree UI actually exercised per build? | Same | Usage data, not code |
| 5 | What does CSR cost/save *after* compression, rather than instead of it? | [format doc §7.1](cache-format-clean-slate-redesign.md) item 5 | Requires a CSR prototype to compress |

Question 1 is the one that matters. It is cheap to answer relative to what it gates: a counter on
`TryGetParents` plus a simulated block-index histogram over the existing `cache.bin`, on the dump
already used throughout this doc. It should be settled before any compression work starts, not
after.

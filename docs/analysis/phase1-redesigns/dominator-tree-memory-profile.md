# Dominator Tree — Memory Profile, Measurement Traps, and Allocation Fixes

Companion to [dominator-tree-lengauer-tarjan.md](dominator-tree-lengauer-tarjan.md) (design, D1-D9) and
[dominator-tree-implementation-plan.md](dominator-tree-implementation-plan.md) (phasing). This doc
covers one question the other two don't: **where does the exact dominator-tree path's memory actually
go, and why did the numbers we had been reading mislead us about it.**

Written after a real-dump investigation triggered by a CLI run whose per-analyzer table showed
Dominator Analysis at `+1.3 GB` working set and `-686.4 MB` managed — a combination that looks like
a broken counter and isn't.

**Reference dump throughout:** `Crash_IIS_BALTSTPRD` second-chance-exception dump, 3.3 GB.
`N = 6,686,490` reachable nodes, `E = 17,367,740` edges, `E/N = 2.60`, `2,115,540` leaves folded (D8)
→ `N' = 4,570,950` reduced nodes, `E' = 15,252,200` reduced edges.

---

## TL;DR

1. **The `Managed Δ` column was measuring the wrong thing.** It is a `GC.GetTotalMemory` before/after
   difference — net *heap size*, not allocation. It can and does go negative while the process commits
   over a gigabyte of new pages. Every conclusion drawn from it about this analyzer was unsound,
   including a row in this design's own Measured Numbers table.
2. **Real cost, measured honestly: 3.18 GB allocated / 475 bytes per node** for the exact path on the
   3.3 GB dump. Now **2.00 GB / 300 bytes per node** after the fixes below — a **1.18 GB (-37%)**
   reduction with byte-identical output at every step.
3. **Three allocation bugs accounted for 1.18 GB of that** — two in `LengauerTarjan` (803 MB) and one
   in the forward-index read path (372 MB). All three were pure garbage with no peak-memory benefit.
4. **The budget model was a single bytes-per-node constant, and no such constant exists.** Per-node peak
   cost *falls* as the D8 fold rate rises — 140 B/node on the 3.3 GB dump (32% folded) vs **118 B/node**
   on the 25.6 GB dump (46% folded) — so any constant misprices one dump or the other. It got this wrong
   twice in opposite directions: 76 admitted graphs needing ~18 GB against a 6 GB budget, then a
   corrected 220 rejected a 58.34M-node graph already measured completing successfully. Replaced with a
   two-term model, **`150 B/node + 12 B/edge`**, enforced mid-walk on both terms.
5. **Default budget raised 6 GB → 20 GB — big dumps are explicitly *not* excluded.** The 25.6 GB dump
   projects 9.68 GB (48% of budget); the ceiling is ~119M reachable nodes, about 2x the largest dump
   measured.
6. **Phase 1's cost was wrong twice, and is now measured** (§ 6). The old "600 MB x 4 buckets = 2.4 GB"
   claim was arithmetic coincidence, and worse, *every* prior measurement of this stage was a **cache
   hit** that never ran the heap scan. A forced rebuild allocates **10.5 GB**, of which **78.4% is the
   parallel heap scan at 620 B/object** (ClrMD churn) — the bucket sorters are ~11% each.
   **`segBuf` is now fixed**: a whole-segment staging buffer measured at 512 MB peak resident, replaced
   by chunk-streaming at **12.5 MB** (-97.6%), which also removed 940 MB of allocation as a side effect.
7. **"Allocated" is a flow, not a level, and it is easy to misread.** 10.5 GB allocated against +2.6 GB
   working set is not a contradiction: gen0 recycled the same ~9 MB nursery 950 times. The console
   legend now says so explicitly, because this was misread in review.
8. **The table itself is now fixed** (§ 7) — `Allocated` replaces `Managed Δ`, and both edge sorters'
   fake `PeakMemoryMb` is replaced with an exact figure. This immediately surfaced a second problem
   nobody was looking for: GC Root Analysis allocates 22 MB but grows the working set by 337 MB.

---

## 1. The measurement trap

The CLI's per-analyzer memory table reported, for a single run:

| Analyzer | WS Δ | WS After | Managed Δ |
|---|---:|---:|---:|
| Dominator Analysis | +1.3 GB | 4.2 GB | **-686.4 MB** |

A negative managed delta next to a 1.3 GB working-set gain reads as a bug in the instrumentation. It
isn't. The managed column is `GC.GetTotalMemory(false)` sampled before and after, which yields **net
heap size change**, not bytes allocated. Dominator's own allocation pressure triggers gen2/LOH
collections that reclaim the *preceding* analyzers' garbage — GC Root Analysis (+343.9 MB), Boxing
Analysis (+376.0 MB managed), String, Event Leak, and others. The heap therefore ends *smaller* than
it started, while the process has committed a gigabyte of fresh pages that .NET does not eagerly
decommit (particularly LOH and gen2 segments).

Reproduced directly, in one instrumented run of `DominatorAnalyzer.AnalyzeAsync`:

| Metric | Value |
|---|---:|
| `GC.GetTotalMemory` delta ("Managed Δ") | **-835,374,496** |
| `GC.GetAllocatedBytesForCurrentThread` delta | **+2,677,521,096** |
| `Environment.WorkingSet` delta | +478,302,208 |
| Gen2 collections during the run | 40 |
| LOH size at exit | 874,735,256 |

Three numbers for the same operation, differing by 3.5 GB in range and opposite in sign.

### This trap is already recorded as a finding

[dominator-tree-lengauer-tarjan.md § Measured Numbers](dominator-tree-lengauer-tarjan.md#measured-numbers)
contains the row:

> | Managed memory delta during the analyzer run | 0 (net negative, GC reclaimed) | ~1.95GB … |

"0 (net negative, GC reclaimed)" is this artifact written down as though it were an observation about
the analyzer's cost. It is an observation about GC timing. Corrected in that doc, with a pointer here.

### What to use instead

- **`GC.GetTotalAllocatedBytes(precise: false)`** — monotonic, never reclaims, so it attributes work to
  the code that did it regardless of when collections happen. This is the right primary metric for
  "how much does this analyzer cost," and it is what the table now reports (§ 7). Prefer it over
  `GC.GetAllocatedBytesForCurrentThread()`, which misses anything an analyzer allocates on pool
  threads; use the per-thread variant only inside code you know to be single-threaded.
- **Peak working set** — for the OOM-risk question, which is about *simultaneously live* bytes. Keep it
  alongside allocated bytes rather than instead of it: § 7 shows a case (GC Root Analysis, 22 MB
  allocated / +337 MB working set) that only the two columns together can explain.
- **`GC.GetTotalMemory` deltas** — only meaningful bracketed by forced full collections, and even then
  they answer "what did this retain," not "what did this cost."

The same defect existed in both edge sorters' `PeakMemoryMb` — a `GC.GetTotalMemory(false)` sample
taken once at the end of a bucket sort and labelled a *peak*, when it was neither a peak nor
attributable. **Fixed in § 7.**

---

## 2. Ground-truth instrumentation added

To make the path measurable at all, two permanent diagnostics were added:

- [`ReachableGraph.EdgeCount`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraph.cs) —
  captured at construction so it survives `ReleaseEdgeAndDegreeArrays()`. The path's cost is dominated
  by per-edge arrays, so the budget model cannot be validated without `E`, and after the release
  `FwdTargets.Length` is 0.
- Allocated bytes, average out-degree, fold percentage, and the §D6 budget's *projected* figure against
  what was allowed — all in `DominatorAnalyzer`'s completion log. Drift between the cost model and
  reality is now visible in any ordinary run's output rather than needing a bespoke investigation, which
  is precisely the failure that let the budget constant stay wrong through two attempts (§ 5).

Resulting log line:

```
Exact dominator tree computed in 9,421 ms: 6,686,490 reachable nodes, 17,367,740 edges
(out-degree 2.60), 2,115,540 leaves folded (§D8, 31.6%), total retained bytes at GC roots
1,018,915,128 vs. heuristic top-K estimate 6,901,516. Allocated 2,003,786,272 bytes;
§D6 budget projected 1,211,386,380 of 21,474,836,480 allowed.
```

The out-degree and fold-percentage fields are there specifically because those two numbers are what
make the per-node cost vary between dumps — without them in the log, § 5's diagnosis wasn't derivable
from a run's output.

---

## 3. Where the memory goes

### 3.1 Allocation accounting (before fixes: 3.18 GB measured)

| Source | Bytes | Nature |
|---|---:|---|
| Structural arrays, summed across all stages | ~1.78 GB | necessary; a subset is simultaneously live |
| `LengauerTarjan` `Compress` + `buckets` (§4.1, §4.2) | **803 MB** | **pure garbage — fixed** |
| `ForwardEdgeIndexReader.ReadGroup` — one `ulong[]` per node (§4.3) | **372 MB** | **pure garbage — fixed** |
| `DenseIdMap` doubling 2¹⁶ → 2²⁴ | ~216 MB churn (218 MB final) | rehash garbage — open |
| Misc (`ObjectScanCounter`, ClrMD lookups, `List` doubling, closures) | ~10 MB | |
| **Total** | **~3.18 GB** | matches measured 3,179,359,440 |

Each fix's measured effect matched its predicted line exactly, which is what makes the attribution
trustworthy: 3,179,359,440 → 2,376,226,408 (-803 MB, §4.1+§4.2) → 2,003,768,816 (-372 MB, §4.3).

The `ReadGroup` line came in at 372 MB against a ~235 MB prediction — the estimate undercounted
per-array header overhead across ~5.5M non-leaf parents (24 bytes each is 132 MB on its own, before
the 139 MB of payload).

### 3.2 Peak *live* bytes, by stage (analytic)

Peak live is the number that matters for the cap, and it is much lower than total allocated because
stages release as they go. Analytic sums at `N = 6.69M`, `E = 17.37M`, `N' = 4.57M`, `E' = 15.25M`:

**Stage 1 — `ReachableGraphWalker`, at the CSR fill loop (peak ≈ 716 MB)**

Everything below is alive simultaneously, because the raw edge lists are still being read while the
CSR arrays are being written:

| Array | Size |
|---|---:|
| `addresses` `ChunkedBuffer<ulong>` (8N) | 53.5 MB |
| `edgeFrom` + `edgeTo` `ChunkedBuffer<int>` (4E each) | 139.0 MB |
| `fwdTargets` + `revTargets` `int[E]` | 139.0 MB |
| `outDegree`/`inDegree`/`fwdOffsets`/`revOffsets`/`fwdCursor`/`revCursor` (4N each) | 160.4 MB |
| `isRoot` `ChunkedBuffer<bool>` (1N) | 6.7 MB |
| `DenseIdMap` at final capacity 2²⁴ (13 B/slot) | 218.1 MB |

Then `addresses.ToArray()`, `outDegree.ToArray()`, `isRoot.ToArray()` allocate **full second copies**
(86.9 MB) while the source `ChunkedBuffer`s are still rooted — the buffers exist specifically to avoid
`List<T>`'s double-and-copy spike, and the `ToArray()` at the end partially reintroduces it.

Note every `ChunkedBuffer` chunk is 65,536 elements = **256 KB (`int`) / 512 KB (`ulong`)**, far above
the 85,000-byte LOH threshold. Every chunk of every accumulator lands on the LOH, which is not
compacted by default and only collected on gen2. This is a large part of why LOH sits at ~875 MB at
exit.

**Stage 2 — metadata resolution (+134 MB)**

`methodTables` (8N), `shallowSizes` (8N), `generationTags` (4N). `GenerationTag` is a default `int`
enum; declaring it `: byte` would save 20 MB for no loss — it has 8 members.

**Stage 3 — `LeafFolder.Fold` (peak ≈ 760 MB)**

The critical property: **`Fold` builds the entire reduced CSR while the original CSR is still alive.**
`ReleaseEdgeAndDegreeArrays()` is only called *after* `Fold` returns. So 439.7 MB of original graph
coexists with 319.8 MB of reduced graph and scratch (`isFoldable`, `oldToNewId`, `newToOldId`,
`foldedBytesByNewId`, `reducedOutDegree`, both offset arrays, both target arrays, two cursors).

**Stage 4 — `DominatorTreeComputer` + LT + rollup (peak ≈ 975 MB)**

- A **third** copy of the reverse edge array: `extRevTargets` (`int[E']`, 61 MB) exists only to append
  one virtual-root edge per GC root onto the reduced reverse CSR, because `NeighborsFunc` must return
  a contiguous `ReadOnlySpan<int>`.
- LT internals, ~340 MB: `idomByNode`, `dfsNumByNode`, `vertexByDfs`, `dfsParentByDfs`, `stackNode`,
  `stackCursor` (each `N'+1`), plus `semi`, `label`, `ancestor`, `idomByDfs`, `bucketHead`,
  `bucketNext` (each `n`).
- Rollup, ~183 MB: `shallow`, `childCount`, `childOffsets`, `childTargets`, `childCursor`, `preorder`,
  `depth`, `retained`.

**Overall peak live ≈ 0.87 GB** — stage 4 dominates — i.e. **~140 bytes/node** at this dump's
`E/N = 2.60` and 32% fold rate.

> **Two different numbers are both ≈150; don't conflate them.** The **140 B/node** above is this dump's
> *total* peak divided by its node count, and it moves with fold rate and density (the 25.6 GB dump's is
> 118). The **150 B/node** in § 5's budget model is only the model's *node-term coefficient*, paired with
> a separate 12 B/edge term; it is not a claim that any dump costs 150 bytes per node. The whole point
> of § 5 is that no single per-node figure is stable.

Recomputing this stage model for the 25.6 GB dump (58.34M nodes / 137.03M edges / 46% folded) gives
stage 1 ≈ 5.46 GB, stage 3 ≈ 5.60 GB, stage 4 ≈ 6.42 GB — a **6.42 GB peak, 118 bytes/node**. That is
the figure § 5 calibrates against.

> ⚠️ **The 25.6 GB dump's figures throughout this doc are analytic, not measured.** They come from this
> stage model plus the design doc's original `N`/`E`/fold measurements — that dump has not been re-run
> since the allocation fixes or the new budget model. Tracked as § 8 item 3.

---

## 4. Fixed: three allocation bugs

All three produced garbage that was dead within a few instructions of being created, so none of them
bought any reduction in peak live bytes — they were pure throughput and GC-pressure cost. Every fix
was verified against `total retained bytes at GC roots = 1,018,915,128`, unchanged across all four
measurement runs, plus the full 584-test suite.

### 4.0 A metric that does *not* support these fixes

An earlier draft of this doc claimed "gen2 collections 47 → 29" as evidence for §4.1/§4.2. **That claim
was wrong and is withdrawn.** Across four runs, gen2 count went 47 → 40 → 29 → 46 while allocation
dropped monotonically 3.18 → 2.38 → 2.38 → 2.00 GB. Runs 2 and 3 allocate an identical number of bytes
and differ by 11 collections.

Gen2 count depends on whole-process memory pressure, the GC's dynamic tuning, and what the *preceding*
analyzers left behind — not on this analyzer's allocation in isolation. It is exactly the kind of
noisy secondary metric §1 warns about, and it slipped into this doc's own first draft. Allocated bytes
is the metric that behaves.

### 4.1 and 4.2 — `LengauerTarjan`

Both in [LengauerTarjan.cs](../../../src/DumpDetective.Analysis/Traversal/LengauerTarjan.cs).
Combined effect: **-803 MB allocated (-25.3%)**.

### 4.1 `Compress()` allocated a `Stack<int>` on every call

```csharp
void Compress(int v)
{
    var path = new Stack<int>();   // <-- once per call
    ...
}
```

`Compress` is reached from `Eval`, which runs **once per edge examined** in the semidominator loop —
on the order of 15M times on this dump. Every call allocated a `Stack<int>` plus its backing array,
used it for a short ancestor chain, and dropped it.

Replaced with a single reusable `int[]` buffer allocated once outside the loop and grown only if some
chain is unusually deep. Traversal order is identical (push order, then unwind in reverse), so the
algorithm is unchanged.

### 4.2 `buckets` was `List<int>?[n]`

```csharp
var buckets = new List<int>?[n];
...
(buckets[semi[wDfs]] ??= new List<int>()).Add(wDfs);
```

Two costs: the `n`-element array of *references* (36.6 MB of pointers), and up to `n` individually
allocated `List<int>` objects — at `n ≈ 4.57M`, that is millions of small objects the GC must trace on
every gen2, in an algorithm that is already triggering dozens of gen2s.

Replaced with an intrusive singly-linked list threaded through two `int[]` arrays (`bucketHead`,
`bucketNext`). This is sound because **each DFS number is pushed into exactly one bucket, exactly
once** (once per iteration of the main `for wDfs = n-1 downto 1` loop), so `bucketNext[wDfs]` is always
written before it can be read and needs no initialization pass. Only `bucketHead` requires
`Array.Fill(-1)`.

Two `int[]` allocations replace up to 4.57M objects, and the GC never touches the structure again.

### 4.3 `ReadGroup` allocated a `ulong[]` per node — **-372 MB**

[`ForwardEdgeIndexReader.ReadGroup`](../../../src/DumpDetective.Analysis/Indexing/ForwardIndex/ForwardEdgeIndexReader.cs)
did `var result = new ulong[count]` on every lookup — one array per reachable node, ~6.69M times. The
walk copies those children straight into its CSR and never retains the array, so every one of them was
garbage immediately.

Fixed by adding an allocation-free path that reuses a caller-owned buffer, threaded through four
places:

1. **[`IForwardReferenceProvider.GetChildren(ulong parent, ref ulong[] buffer)`](../../../src/DumpDetective.Core/Abstractions/IForwardReferenceProvider.cs)**
   — returns the count written, growing the buffer only when a parent has more children than it
   currently holds. Ships as a **default interface implementation** delegating to the allocating
   `TryGetChildren`, so it is purely opt-in: an implementation that can't do better stays correct
   without changing.
2. **`ForwardEdgeIndexReader.GetChildren` / `ReadGroupInto`** — copies straight from the mapped view
   into the caller's buffer, sharing the existing directory-lookup path unchanged.
3. **`ForwardIndexForwardReferenceProvider`** — overrides the default with the reader's buffer path.
4. **`ReachableGraphWalker`** — its injected successor lookup changed from
   `Func<ulong, IEnumerable<ulong>>` to a new
   **[`SuccessorsFunc(ulong address, ref ulong[] buffer)`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs)**
   delegate. A plain delegate rather than a `Func<,>` because only the former can carry a `ref`
   parameter — the same reason `LengauerTarjan` defines `NeighborsFunc` instead of using `Func<>`. One
   buffer, allocated once at 64 elements, now serves the entire walk.

The §D4 live-walk fallback was converted from a `yield return` iterator to
`LiveSuccessorsInto(heap, address, ref buffer)` in the same shape, so the fallback path is no longer
*more* allocation-heavy than the indexed path (an iterator is itself a per-call heap object).

**Testability was preserved deliberately** — the walker's injection point is what makes it unit-testable
with synthetic graphs, and that property survives the change. The three test files that each carried a
verbatim copy of the same `BuildSuccessors` helper now share
[`SyntheticSuccessors.Build`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/SyntheticSuccessors.cs),
which deliberately honours the grow contract so the resize path is exercised rather than only the
fits-first-time case.

---

## 5. Fixed: the budget model (twice)

`RetentionOptions.ExactDominatorTreeMemoryBudgetBytes` is translated into a mid-walk abort threshold
(D6). This has now been wrong twice, in **opposite directions**, for the same underlying reason: it was
a single bytes-per-node constant, and no such constant exists.

### Attempt 0 — 76 B/node: admitted graphs that would OOM

The design doc derived it as:

> Measured **~76 bytes/node** (4.14GB structural total ÷ 58.34M nodes, 25GB dump — the conservative
> structural-sum figure …)

That 4.14 GB is the **D4 walk-stage structural sum only**. It omits everything downstream:
`LeafFolder`'s reduced CSR (built while the original is still live), the virtual-root-extended reverse
CSR, LT's ~12 working arrays, the rollup arrays, and all churn. It was described as "conservative" when
it was the opposite — a floor presented as a ceiling. Cross-check confirming the provenance: 527 MB ÷
6.69M = 78.8 B/node, 4.14 GB ÷ 58.34M = 70.9 B/node. The constant is exactly the D4-only figure.

At 76 B/node a 6 GB budget yields `Cap ≈ 85M` nodes, a population needing roughly 18 GB. **The cap
admitted precisely the workloads it existed to reject.**

### Attempt 1 — 220 B/node: rejected a graph already known to work

Correcting to 220 (peak-live ~150 B/node plus headroom) dropped the cap to 29.3M nodes — which
**rejected the 25.6 GB dump's 58.34M-node graph**. That was a real regression, not a safety
improvement: the design doc's own Measured Numbers record that dump completing the exact path
end-to-end in 218.49s. The cap was now lying in the other direction.

### The actual problem: per-node cost is not constant

Measuring peak live stage-by-stage on both dumps shows the per-node figure moving the *opposite* way
from intuition as dumps get bigger:

| Dump | N | E | E/N | D8 fold rate | Peak live | **B/node** |
|---|---:|---:|---:|---:|---:|---:|
| 3.3 GB | 6.69M | 17.37M | 2.60 | 32% | 0.87 GB | **140** |
| 25.6 GB | 58.34M | 137.03M | 2.35 | 46% | 6.42 GB | **118** |

The larger dump is **cheaper per node** because it folds 46% of its leaves away instead of 32%, and is
less dense. Any constant calibrated on one misprices the other — 76 was too low for both, 220 too high
for both, and no single value fixes that because the quantity it models isn't a constant.

### Correction: a two-term model, enforced on both terms

[`ExactDominatorTreeBudget`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ExactDominatorTreeBudget.cs)
replaces the constant with **`150 bytes/node + 12 bytes/edge`**, and
[`ReachableGraphWalker`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs)
now aborts mid-walk on the *projected* figure rather than on node count alone. Estimates deliberately
assume **zero leaf folding**, since the fold rate isn't knowable until the walk finishes; both reference
dumps land at ~66% of their projection, so the bound holds without being wild.

Validation against the same two dumps:

| Dump | Projected (no folding) | Real peak | Ratio |
|---|---:|---:|---:|
| 3.3 GB | 1.13 GB | 0.87 GB | 0.77 |
| 25.6 GB | 9.68 GB | 6.42 GB | 0.66 |

The edge term is not cosmetic: it is the only way to express a **dense** graph whose node count looks
comfortable while its edge arrays blow the budget. The old node-only cap was structurally blind to
that, and there is now a dedicated test for it
([`Walk_DenseGraphExceedsEdgeBudget_ReturnsCappedResult`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/ReachableGraphWalkerTests.cs)).

**Where the check actually fires.** Neither `N` nor `E` is known until the walk completes, so
enforcement is mid-walk at two points:

1. **On every newly-discovered node** — the natural checkpoint, and where the old node cap lived.
2. **Every 4M edges** (`EdgeCheckInterval`) — because edges accumulate *between* node discoveries. A
   single very-high-out-degree node, or a dense tail where almost nothing is new, could otherwise add
   millions of edges without passing checkpoint 1. That interval bounds the worst-case overshoot to
   ~4M edges ≈ 48 MB of edge arrays, rather than paying two multiplies per edge in the hot loop.

Over budget, the walk returns `ReachableGraphWalkResult.Capped()` and **discards all partial state** —
the same honesty contract as every other capped structure in the codebase. The analyzer then falls back
to the top-K heuristic and logs the projected figure, the model's coefficients, and the approximate node
ceiling at out-degree 2.5, so the log line itself tells you what to raise and roughly how far.

### Default budget raised 6 GB → 20 GB

Deliberate policy change: **large dumps should not be excluded from the exact path.** At 20 GB:

| | Projected | % of budget |
|---|---:|---:|
| 3.3 GB dump (6.69M / 17.37M) | 1.21 GB | 6% |
| 25.6 GB dump (58.34M / 137.03M) | 9.68 GB | 48% |
| Ceiling at out-degree 2.5 | 20 GB | **~119M nodes ≈ 2.0x the largest dump measured** |

Real peak at the absolute ceiling would be ~13 GB, since the no-folding assumption overshoots by ~1.5x.
Lower the budget on constrained machines — going over is not a failure, the analyzer falls back to the
top-K heuristic and logs why, including the specific figure to raise.

Both sizing claims are pinned by
[`ExactDominatorTreeBudgetTests`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/ExactDominatorTreeBudgetTests.cs)
— in *both* directions, with the 25.6 GB dump asserted to fit at under 60% of budget. A budget
regression is silent at runtime (the analyzer just quietly downgrades to the heuristic), which is
exactly why it needs to fail loudly in CI instead.

### 5.1 FIXED: releasing Fold's inputs early — and why the obvious version freed nothing

`LeafFolder.Fold` builds a complete reduced CSR (2 x E' ints) while the original CSR (2 x E ints) is
still reachable through the caller. `ReleaseEdgeAndDegreeArrays()` only ran *after* Fold returned, so the
two coexisted at the peak of the whole exact-tree path.

Input lifetimes inside Fold are actually much shorter than that, and strictly ordered:

| Input | Last read | Bytes |
|---|---|---:|
| `outDegree`, `inDegree` | foldable-leaf scan | 8N |
| `revOffsets`, `revTargets` | folded-bytes attribution | 4(N+1) + 4E |
| `fwdOffsets`, `fwdTargets` | reduced forward CSR fill | 4(N+1) + 4E |

The forward CSR's release point matters most: it lands immediately before `reducedRevTargets` — another
E'-sized array — is allocated.

#### The first attempt freed exactly 0 bytes

The natural implementation kept Fold's ten array parameters and added release callbacks the caller wired
to `ReachableGraph`, with Fold also assigning `Array.Empty` to its own parameters. Measured with a
compacting gen2 collection either side of the release:

```
LeafFolder forward-CSR release: live 2,183.5 MB -> 2,183.5 MB (freed 0.0 MB;
  arrays were 4x(N+1)+4xE = 91.8 MB, releaser=on)
```

**Zero.** On x64 most of ten arguments are passed in the caller's outgoing-argument stack slots. Those
slots stay GC-reachable for the duration of the call, and nothing the callee assigns can clear them —
the callee's parameter may be enregistered while the incoming stack home still holds the reference.
Clearing the caller's field and the callee's local left a third reference nobody could reach.

Generalisable lesson: **"drop the reference" only works if you can reach every reference.** An
argument-passing convention is a reference holder.

#### Fixing it: one holder instead of ten parameters

`Fold(IFoldInputs inputs)` — arrays reached through a holder, read into a phase-local, then *both* the
holder's field and the local cleared. Two references, both reachable from inside Fold.
`ReachableGraph` implements `IFoldInputs` directly (it already had every member), so in production the
holder *is* the graph and there is no second owner. `ArrayFoldInputs` plus a convenience overload keeps
`LeafFolder` testable with hand-built arrays, which is what the parameter list existed to provide.

```
LeafFolder forward-CSR release: live 2,040.8 MB -> 1,949.0 MB (freed 91.8 MB;
  arrays were 4x(N+1)+4xE = 91.8 MB, holder=ReachableGraph)
```

91.8 MB freed against 91.8 MB predicted — exact.

| | Array params + callbacks | Holder |
|---|---:|---:|
| Freed at forward-CSR release | 0.0 MB | **91.8 MB** |
| Peak live at structural peak | 2,183.5 MB | **1,949.0 MB** |
| Reduction vs. no early release | — | **-234.5 MB (-10.7%)** |

The 234.5 MB is all three groups (8N + 2x[4(N+1) + 4E]). On the 25.6 GB dump the same expression is
**~2.0 GB** (933 MB of node arrays + 1,096 MB of edge arrays), which is why this raises the effective
ceiling more cheaply than raising the budget does.

`LeafFoldResult.ReleaseReducedReverseArrays()` was added in the same pass: `DominatorTreeComputer` folds
the reduced reverse CSR into the virtual-root-extended copy LT actually queries, after which the original
is dead but stays rooted through the result object for the whole run.

#### Measuring this at all required LOH compaction

The first probe reported identical numbers for both arms even before the design flaw was understood,
because it used `GC.GetTotalMemory(forceFullCollection: true)`. Every array here is far over the 85 KB
LOH threshold, and **the LOH is swept, not compacted, by default** — freed bytes remain counted as
free-list space. Only `GCLargeObjectHeapCompactionMode.CompactOnce` plus a compacting
`GC.Collect` reports true live bytes. A release-shaped change alters *reachability*, not allocation
totals, so it is invisible to every metric except a compacted live-bytes reading. Both probes are
retained behind `DD_PERF_DOMINATOR_PEAK=1`.

Correctness: the real-dump exact tree still reports `total retained bytes at GC roots = 1,018,915,128`,
unchanged. `LeafFolderReleaseTests` pins the release order (load-bearing: the forward CSR must outlive
the reverse one, since the reduced reverse CSR is derived from the reduced forward one) and asserts
output equality against a non-releasing run with every released array **poisoned to -999**, so a
read-after-release corrupts the result rather than passing by luck. That test was mutation-checked —
moving one release a step earlier fails 2 of 4 cases.

---

### Hard ceiling that remains, well above the default

The CSR target arrays are `int[]`, so the 2 GB single-object limit caps any graph at **~537M edges**
(~215M nodes at out-degree 2.5) regardless of budget. Raising the budget past ~36 GB would hit that
wall before the budget. Lifting it needs either chunked CSR arrays or `gcAllowVeryLargeObjects`.

---

## 6. Phase 1 index build — measured, after two wrong guesses

> ⚠️ **This section previously claimed the stage's cost was `MaxBucketSize` 600 MB x parallelism 4 =
> 2.4 GB, "matching the observed +2.4 GB exactly." That was numerology and it was wrong twice over.**
> Both errors are recorded here rather than quietly deleted, because they are the same failure mode
> § 1 and § 5 are about: a plausible arithmetic coincidence accepted in place of a measurement.

### Error 1: the arithmetic was a coincidence

`CalculateBucketCount` is `ceil(dumpMB / 500)`, so bucket **count scales with dump size** and per-bucket
file size stays roughly constant — ~74 MB on the 3.3 GB dump (7 buckets), ~58 MB on a 25.6 GB dump
(53 buckets). Four concurrent sorts hold ~300 MB, not 2.4 GB. `MaxBucketSize = 600 MB` is a guard
against hash skew that is never approached in normal operation. Two numbers agreeing to one decimal
place was chance.

### Error 2: every prior measurement of this stage was a cache hit

`DiskBackedObjectIndexWriter.Build` opens with a fast path: if `cache.bin` carries a valid
`TypeAggregates` section it returns immediately, skipping the heap scan entirely. The cache lives in
`<dump>.dumpindex/`, and every earlier run in this investigation hit it. **The "3.3 GB allocated /
+2.4 GB WS" figures attributed to the index build were from runs that never built an index.** They were
measuring `TryLoadFromCache` deserializing a 1.36 GB `cache.bin`, plus the shared scans.

A forced rebuild (move `cache.bin` aside) allocates **10.5 GB** for the stage — 3.2x the number
previously quoted.

### Measured attribution

`DD_PERF_INDEX_MEMORY=1` now emits per-phase allocation from inside `Build`:

```
[PERF] IndexBuild allocation by phase (total 8.45 GB, 620 B/object over 14,620,162 objects):
[PERF]   parallel heap scan (incl. edge extraction)     6,785.5 MB  (78.4%)
[PERF]   columnar scratch concatenation                     0.1 MB  ( 0.0%)
[PERF]   satellite sections                                 9.0 MB  ( 0.1%)
[PERF]   reverse index (sort + write)                     891.8 MB  (10.3%)
[PERF]   forward index (sort + write) + TypeAggregates     966.3 MB  (11.2%)
[PERF]   gen0=950 gen1=320 gen2=21  managed-heap-now=3,000.8 MB
```

**The bucket sorters are ~11% each, not the dominant cost. 78.4% is the parallel heap scan, at 620
bytes allocated per object** — that is ClrMD churn from ~14.6M `EnumerateReferences(carefully: true)`
calls and per-object type/segment work, not any buffer this codebase owns.

The 8.45 GB accounted here versus 10.5 GB for the whole stage leaves ~2 GB outside `Build`: the
`TryLoadFromCache` probe, `heap.Segments` first access, context construction, and `RunSharedScans`.

### Why 10 GB is a real number, and what it is not

It is **cumulative allocation over ~40 seconds**, not memory held. Three independent cross-checks:

| Check | Value | Verdict |
|---|---:|---|
| Bytes per gen0 collection (8.45 GB / 950) | 9.1 MB | a normal gen0 budget — the collection count independently confirms the byte count |
| Allocation rate (8.45 GB / ~40 s) | 216 MB/s | unremarkable for .NET; gen0 rates above 1 GB/s are routine |
| Working-set growth for the stage | +2.6 GB | nothing is *using* 10 GB |
| Managed heap at end of build | 3.0 GB | " |

Sustained high allocation with low retention is exactly what a streaming, disk-backed indexer should
look like. The number is large because it is a rate integrated over time, and the column is labelled
"Allocated" for that reason — see § 1 on why the honest metric is also the easiest one to misread as
"memory used."

### FIXED: `segBuf` — 512 MB → 12.5 MB peak resident

Separate axis from the allocation numbers above — this one was **peak live**.

`DiskBackedObjectIndexWriter` rented a per-worker `HeapEntry[]` holding **every object in the segment**,
written in the scan loop and read back only in the columnar serialize loop that followed it — strictly
sequentially, same order, no sort or reordering in between. A pure pass-through staging buffer.

Measured before: **8 segments, DOP 4, peak concurrent 448-512 MB** across runs (worker overlap is
timing-dependent), largest single buffer **128 MB**, **8 pool-doubling copies**. `HeapEntry` is 32 bytes
(3x `ulong` + `sbyte`, padded). Three properties made it worse than the headline: the initial rent
capped at 1M entries (32 MB) then **grew by doubling with a full copy**; `ArrayPool` **retains the
largest buffers for process lifetime**; and it scaled with objects-per-segment x DOP, so a 25.6 GB dump
at DOP 8 would have been multiples of it.

Replaced by
[`SegmentColumnWriter`](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs),
which streams each entry straight into the segment's four columnar scratch files, buffering one
fixed-size chunk per column. `using`-scoped so the trailing partial chunk is flushed and buffers/streams
are released even if the scan throws, and streams are created on **first flush** rather than in the
constructor — a segment yielding no entries must still produce no scratch files, because
`ConcatenateScratchFiles` skips missing per-segment files.

| | Before | After |
|---|---:|---:|
| Peak concurrent staging buffer | **512.0 MB** | **12.5 MB** (-97.6%) |
| Largest single buffer | 128 MB | 1 MB/column (131,072 entries) |
| Pool-doubling copies | 8 | 0 (gone by construction) |
| Index build allocated (inside `Build`) | 8.45 GB | **7.51 GB** (-940 MB) |
| — of which parallel heap scan | 6,785.5 MB | 5,824.1 MB (-961 MB) |
| Bytes allocated per object | 620 | **551** |
| Stage allocated (incl. shared scans) | 10.5 GB | 9.6 GB |
| gen2 collections | 21 | 11 |

The ~940 MB *allocation* drop was not predicted — the rationale was purely about peak resident. It comes
from the pool rents themselves: 8 initial rents of up to 128 MB plus 8 doubling copies were real
allocations, not just resident bytes.

> **One number that did not improve:** the stage's working-set delta read **+2.8 GB after vs +2.6 GB
> before**. Working set at this granularity is dominated by ClrMD's mapped dump pages and GC segment
> retention, and moves run to run; a 512 MB buffer removal is not cleanly visible in it. The
> peak-resident win is asserted on the direct measurement of the buffer itself (balanced accounting,
> `leaked-live 0 B`), not on process working set. Claiming a WS win here would be the same mistake as
> § 1.

Correctness: all four index-integrity discrepancy suites pass against the real dump —
`SegmentAddressContiguityDiscrepancyTests`, `SegmentIndexBuildDiscrepancyTests`,
`ObjectAddressLookupDiscrepancyTests`, `HeapAnalysisCacheObjectMetadataDiscrepancyTests`. Those compare
disk-mode against memory-mode entry-by-entry, so a reordering or dropped-entry bug in the new write path
would fail them.

### Checked and *not* worth pursuing

Recorded so nobody re-derives them:

- **Per-worker x per-bucket edge buffers.** `EdgeBatchSize` is 2048, so `DOP x buckets x 2 x 2048 x 16 B`
  = 1.8 MB on the 3.3 GB dump, 27 MB on a 25.6 GB dump. Negligible.
- **Forward and reverse index builds are sequential**, not concurrent (both
  `.GetAwaiter().GetResult()` in `Build`), so their peak is `max`, not sum. Worth confirming because
  overlap would have doubled everything.
- **The two `ulong[]` per bucket sort** are already at the theoretical minimum for an in-memory sort of
  16-byte records. Going below needs external merge sort, and the bucket partitioning already *is*
  that.
- **`ForwardEdgeExtractor`/`ReverseEdgeExtractor` stream straight to disk**, bounded by 64 KB
  `FileStream` buffers. Correct as written.

Remaining minor item in the sorters: `dirEntries` is a `List<(ulong, long)>` with one entry per unique
key, grown by doubling, so it carries a transient 2x spike. Distinct keys can be counted in one cheap
pass over the already-sorted array and the list sized exactly. Tens of MB; free.

---

## 7. Fixed: the diagnostics table that started all of this

The per-analyzer/per-stage memory table was itself the reason this investigation was needed, so it was
the first thing fixed after the allocation work. Its `Managed Δ` column is **replaced** by `Allocated`,
sourced from `GC.GetTotalAllocatedBytes(precise: false)`.

- **[`AnalyzerMemoryStats`](../../../src/DumpDetective.Core/Models/AnalyzerRunResult.cs)** gained
  `AllocatedBefore`/`AllocatedAfter` → `AllocatedDelta`. Made **required** rather than defaulted, so
  the compiler enumerated all eight construction sites instead of letting any silently report zero.
  `ManagedHeapDelta` survives but now carries a warning in its own XML docs; `ManagedHeapBefore`/`After`
  stay as useful absolute readings.
- **Process-wide, not per-thread.** `GC.GetTotalAllocatedBytes` rather than
  `GC.GetAllocatedBytesForCurrentThread`, because an analyzer may fan out to pool threads (see
  `IAnalyzer.IsThreadSafe`) and a per-thread counter would miss everything they allocate.
- **[`ConsoleUx`](../../../src/DumpDetective.Cli/Console/ConsoleUx.cs)** — column order is now
  `Allocated | WS Δ | WS After` (unchanged total width, 78 chars), with a footer legend so the columns
  can't be misread the same way twice.
- **[`ConfidenceSectionBuilder`](../../../src/DumpDetective.Reporting/SectionBuilders/ConfidenceSectionBuilder.cs)**
  — the report's "Analyzer memory impact" table drops `MH Delta` and leads with `Allocated`.
- **Incidental bug found while editing:** the row's severity colour read
  `wsDelta > 50MB ? "yellow" : wsDelta > 200MB ? "red" : "grey"`. Since anything over 200 MB also
  clears 50 MB, the `red` branch was unreachable and every large row rendered yellow. Reordered.

### What the corrected table reveals

Same dump, same pipeline, `--memory-diagnostics`:

| Stage / Analyzer | Allocated | WS Δ | Previously reported as |
|---|---:|---:|---|
| Scan + Index heap | **3.3 GB** | +1.8 GB | +2.2 GB managed |
| Run analyzers | 2.8 GB | +926 MB | -166 MB managed |
| **Dominator Analysis** | **2.2 GB** | +662.8 MB | **-686.4 MB managed** |
| Boxing Analysis | 401.0 MB | +175.5 MB | +376.0 MB managed |
| **GC Root Analysis** | **22.0 MB** | **+336.7 MB** | +8.8 MB managed |

Dominator Analysis is now unambiguously the largest single analyzer cost, where before it appeared to
*release* 686 MB.

**GC Root Analysis is the newly-visible inverse case, and arguably the more interesting one:** 22 MB
allocated against a 336.7 MB working-set gain. It barely allocates managed memory at all — that growth
is native ClrMD structures and memory-mapped dump pages being faulted in. The old table showed
"+8.8 MB managed" and concealed it completely. Neither column alone would have found it; it needs both
side by side, which is why `WS Δ` is kept rather than replaced.

### Also fixed: the sorters' fake "peak"

`ForwardEdgeSorter` and `ReverseEdgeSorter` both reported `PeakMemoryMb` as a single
`GC.GetTotalMemory(false)` sample taken at the end of a bucket sort — neither a peak (one instant,
after the arrays may already be collectible) nor attributable (whole-process heap size, including every
other bucket sorting concurrently). Replaced with `SortArrayBytes`, computed exactly as
`edgeCount * 2 * sizeof(ulong)` — the two `ulong[]` arrays the method actually holds. That is the
number that sets the concurrency ceiling described in § 6, so it is worth having correct.

### Deliberately not changed

[`AnalyzerMemoryDiagnosticRecord`](../../../src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs)
in the serialized report appendix records working-set and managed-heap *absolutes* only, with no delta,
so it is incomplete rather than misleading. Adding an allocated-bytes column there is a JSON schema
addition requiring a version bump per [schema-versioning.md](../../schema-versioning.md) — a deliberate
decision, not a drive-by. Tracked in § 8.

---

## 8. Remaining work, ranked

1. **Investigate GC Root Analysis's 22 MB-allocated / +337 MB-working-set profile** (§ 7). Newly
   visible, and the shape is unlike anything else in the table: essentially all native/mapped growth.
   Worth knowing whether that is unavoidable ClrMD page-faulting or a structure being retained.
2. **Reduce per-object allocation in the parallel heap scan** — 78.4% of the index build's 10.5 GB, at
   620 B/object over 14.6M objects (§ 6). Dominated by ClrMD `EnumerateReferences(carefully: true)`
   churn, one call per object. This is a throughput/GC-pressure problem rather than a footprint one, but
   it is by far the largest allocation source anywhere in the pipeline.
3. **Fix the flaky unit test** `RetainedSizeCandidateSelectorTests.SelectAndCompute_RespectsMaxCandidatesToWalk_RankedByShallowSizeDescending`
   — fails roughly 1 run in 10 with "Did not expect smallAddr to be 0UL". Confirmed **pre-existing**
   (reproduced at the commit before the `segBuf` work, same rate, in a clean worktree), so it is not a
   regression — but a suite that fails 10% of the time erodes the signal every other item here depends on.
4. **Re-measure the 25.6 GB dump end-to-end.** Everything about it in this doc is analytic — derived
   from the stage model and the design doc's original figures, not re-run since the allocation fixes and
   the new budget model. It is the one dump that exercises the parts of § 5 that matter most, and the
   budget was raised specifically so it stays on the exact path.
5. ~~Lower peak live by releasing Fold's inputs early.~~ **DONE — see § 5.1.** Peak live at the
   structural peak dropped **2,183.5 MB → 1,949.0 MB (-234.5 MB, -10.7%)** on the 3.3 GB dump, scaling to
   ~2.0 GB on the 25.6 GB dump. Still open on the same theme: `extRevTargets` is a third full copy of the
   reverse edge array, now released immediately after it is built but still allocated in the first place.
6. **`ChunkedBuffer.ToArray()` second copies (~87 MB)** — the CSR build could consume the chunks
   directly rather than materializing a flat copy while the chunks are still rooted.
7. **`GenerationTag : byte` (~20 MB)** — 8-member enum currently costing 4 bytes/node.
8. **`DenseIdMap` pre-sizing (~216 MB churn).** Guard this one: pre-sizing from total heap object
   count over-allocates badly when much of the heap is unreachable garbage, which is the normal case
   in a crash dump. Only worth doing with a reachability-aware estimate.
9. **Allocated bytes in the serialized report appendix** — `AnalyzerMemoryDiagnosticRecord` still
   carries working-set/managed-heap absolutes only (§ 7, "Deliberately not changed"). Adding an
   allocated column is a JSON schema change needing a version bump per
   [schema-versioning.md](../../schema-versioning.md).

---

## 9. Reproducing

```bash
DD_RUN_DISCREPANCY_TESTS=1 dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj -c Release \
  --filter "FullyQualifiedName~DominatorAnalyzerExactTreeRealDumpTests" \
  --logger "console;verbosity=detailed"
```

[`DominatorAnalyzerExactTreeRealDumpTests`](../../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DominatorAnalyzerExactTreeRealDumpTests.cs)
reports allocated bytes, heap-size delta, working set, gen2 count and LOH size for the analyzer run.
Override the dump with `DD_BENCHMARK_DUMP`, and scratch location with `DD_SCRATCH_DIR`.

> **Run these one at a time, in the foreground.** Each test process memory-maps and loads a full
> multi-GB dump; concurrent or backgrounded runs have repeatedly OOM-crashed development machines.
> See the project CLAUDE.md rule.

**Wall-clock is not a reliable signal here.** The same unchanged work measured between 9.9s and 27.3s
across runs in one session, depending on OS page-cache state for the dump file. Allocated bytes, by
contrast, was stable to within 0.001% between two runs of identical code (2,376,226,408 vs
2,376,247,920 — a 21 KB spread on 2.4 GB). Compare allocations, not seconds, unless you control for
cache state. Gen2 collection count is likewise unreliable — see §4.0.

---

## 10. Measurement log

All runs on the 3.3 GB reference dump, one at a time, foreground.

| Run | Fixes applied | Budget model | Exact-tree allocated | B/node | Δ | Gen2 | LOH at exit | Retained bytes (correctness) |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 1 (baseline) | none | 76 B/node | 3,179,359,440 | 475.5 | — | 47 | 901,707,744 | 1,018,915,128 |
| 2 | §4.1 + §4.2 | 76 B/node | 2,376,226,408 | 355.4 | -803 MB | 40 | 874,735,256 | 1,018,915,128 |
| 3 | ″ | 220 B/node | 2,376,247,920 | 355.4 | ±0 | 29 | 751,331,264 | 1,018,915,128 |
| 4 | + §4.3 | 220 B/node | **2,003,768,816** | **299.7** | **-372 MB** | 46 | 724,476,136 | 1,018,915,128 |
| 5 | ″ | **150 B/node + 12 B/edge, 20 GB** | 2,003,786,272 | 299.7 | ±0 | — | — | 1,018,915,128 |

**Cumulative: -1,175,590,624 bytes (-37.0%)**, from 475.5 to 299.7 bytes/node.

Retained bytes are identical across all five runs — every change is allocation- or policy-only, never
behavioural. Runs 3 and 5 change only the budget model, and the ±0 allocation delta between 4 and 5
confirms that: the model decides *whether* to run, not *how*.

Run 5's projection is 1,211,386,380 of 21,474,836,480 allowed — **5.6% of budget** for this dump, where
run 3's model would have consumed 23% of a 6 GB budget for the identical graph.

**Do not read the Gen2 column as a trend** — see §4.0. Runs 2 and 3 allocate identical bytes and differ
by 11 collections; run 4 allocates the least and collects more than run 3. It is included for
completeness, not as evidence.

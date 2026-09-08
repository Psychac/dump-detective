# DominatorAnalyzer — same `PopulateEvidence` root-path search bottleneck as EventLeakAnalyzer

> Status: root-caused via real single-pass profiling, fix not yet implemented/approved.
> Dump used: `D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp`
> (1,411 GC roots, 14,003 unique MethodTables). Do not run full-dump analysis against this dump —
> a prior full-analysis run crashed the analysis machine. Use targeted single-pass tests only.
>
> See also: [eventleak-populateevidence-root-search-perf.md](eventleak-populateevidence-root-search-perf.md)
> — this doc documents the same underlying bug (`SampleRootPathFinder`'s per-root search budget)
> found via the same investigation technique, applied to a second analyzer.

## Finding

`DominatorAnalyzer.AnalyzeAsync` measured at **26.80s** end-to-end on the reference dump, via a
new single-pass timing test
([DominatorAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HeapIndexScanDispatcherPerfTests.cs)).
Breakdown:

| Stage | Time |
|---|---|
| `LeakSignals` build (reference-counting scan) | 3.91s (no-dispatcher fallback path — see caveat below) |
| `Analyze.GetOrBuildTypeStatistics` (index hydration, 13,884 types) | 0.10s |
| `Analyze.CandidateBuildLoop` (13,884 candidates) | 0.05s |
| `Analyze.PopulateRetainedBytes` (`BoundedGraphWalk.ComputeExclusiveRetained` × 15 objects) | 1.75s |
| **`Analyze.PopulateEvidence`** | **20.90s (78% of total)** |
| `Analyze.BuildTopRetentionTypes` + sum | 0.00s |
| `Analyze.TopKBoundedGraphWalk` (retained-bytes walk on top 15 candidate types) | 0.10s |
| **Total (`AnalyzeAsync`)** | **26.80s** |

## Root cause — identical to EventLeakAnalyzer

[`DominatorAnalyzer.PopulateEvidence`](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs)
(line ~531) calls
[`SampleRootPathFinder.TryFindSampleRootPath`](../../src/DumpDetective.Analysis/Traversal/SampleRootPathFinder.cs)
once per top highly-referenced object (`options.TopHighlyReferencedObjectsToShow`, default 15, capped
at 20 candidates evaluated overall). Per-instance costs on this dump:

- Found early (6 of 15 instances): ~0.00s each — fine.
- Not found — search exhausts most/all of the 1,411 roots' individual 5,000-object BFS budgets
  (9 of 15 instances): ~2.3–2.4s each.

9 × ~2.3s ≈ 20.7s — matches the measured `Analyze.PopulateEvidence` cost almost exactly, and is
the same pathological "search everything, find nothing" per-root-budget behavior documented for
`EventLeakAnalyzer`. Both analyzers hit this because both call the same shared
`SampleRootPathFinder.TryFindSampleRootPath` helper with the same default budget/threshold
constants. Fixing `SampleRootPathFinder` (see the proposed fix in the EventLeak doc) fixes both
analyzers at once.

## Secondary note — `LeakSignals` measured via the no-dispatcher fallback path

Same caveat as documented for `EventLeakAnalyzer`
([details](eventleak-populateevidence-root-search-perf.md)): this test calls
`analyzer.AnalyzeAsync(context, ...)` directly, never through `HeapIndexScanDispatcher`, so
`DominatorAnalyzer`'s `_participantScanSucceeded` stays `false` and the reference-counting scan
takes the `AnalyzeObjectsPass` **no-index fallback** branch
([DominatorAnalyzer.cs:117-119](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs))
— a live `heap.EnumerateObjects()` walk — rather than reusing dispatcher-accumulated
`_referenceCount` state from the fast index-based `OnHeapEntry` participant path.

Measured for comparison, via the pre-existing
[DispatcherPass_PerParticipantBreakdown_SinglePassEach](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HeapIndexScanDispatcherPerfTests.cs)
test (which does drive `DominatorAnalyzer` through the dispatcher):

- Dispatcher (participant/fast) path: **6.88s**
- No-dispatcher fallback path (this doc's measurement): **3.91s**

Interestingly the fallback measured *faster* here than the dispatcher path (unlike
`EventLeakAnalyzer`, where the fallback was much slower). This is plausible — the dispatcher run
in `DispatcherPass_PerParticipantBreakdown_SinglePassEach` scans each analyzer through a *separate*
`dispatcher.Run()` call (one shared pass per analyzer, not batched together), so it isn't
necessarily cheaper than a live heap walk; the two numbers aren't directly comparable without
controlling for machine load/caching between runs. Either way, production always goes through the
dispatcher (real `_participantScanSucceeded = true` path), so **6.88s is the representative
production figure for this stage**, not the 3.91s or 20.80s-total numbers measured here. The real,
representative total for `DominatorAnalyzer` in production is therefore closer to
`6.88s (dispatcher scan) + ~22.89s (Analyze post-scan, dominated by PopulateEvidence) ≈ 29.8s` —
consistent with the ~30s the user observed.

## Proposed fix

Same as [eventleak-populateevidence-root-search-perf.md](eventleak-populateevidence-root-search-perf.md#proposed-fix-not-yet-implemented--pending-approval):
see the full audit in [root-path-search-blast-radius.md](root-path-search-blast-radius.md), which
found this same defect in four call sites (this one, `EventLeakAnalyzer`, `TimerLeakAnalyzer`,
`StaticRootLeakDetector`) plus a structurally identical reimplementation in
`ReferenceChainAnalyzer`'s `Fast` search mode. The proposed fix is to route all of these through
`RootPathFinder` — an already-correct, bounded-candidate-set implementation already in production
use via `ReferenceChainAnalyzer`'s default (`Balanced`) mode — rather than patching
`SampleRootPathFinder`'s budget separately.

## How to reproduce / verify

```
DD_RUN_DISCREPANCY_TESTS=1 dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HeapIndexScanDispatcherPerfTests.DominatorAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown" \
  --logger "console;verbosity=detailed"
```

Temporary `Stopwatch`/`Console.Error.WriteLine` instrumentation was added to
`DominatorAnalyzer.AnalyzeAsync`, `DominatorAnalyzer.Analyze`, and `DominatorAnalyzer.PopulateEvidence`
to produce these numbers — still present in the source as of this writing (plain
`Console.Error.WriteLine`, no flag). Remove once the fix lands and is verified, or keep if judged
generally useful for future perf investigations (same instrumentation already left in place for
`EventLeakAnalyzer.cs` and `StatisticsCache.cs`).

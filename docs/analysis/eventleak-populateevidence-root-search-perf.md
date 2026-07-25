# EventLeakAnalyzer — `PopulateEvidence` root-path search is the dominant cost

> Status: root-caused via real single-pass profiling, fix not yet implemented/approved.
> Dump used: `D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp`
> (1,411 GC roots, 14,003 unique MethodTables). Do not run full-dump analysis against this dump —
> a prior full-analysis run crashed the analysis machine. Use targeted single-pass tests only.

## Finding

`EventLeakAnalyzer.AnalyzeAsync` measured at **77–79s** end-to-end on the reference dump, via a
single-pass timing test
([EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HeapIndexScanDispatcherPerfTests.cs)).
Breakdown:

| Stage | Time |
|---|---|
| `FindEventLeaks` (shared heap-index scan) | ~35–37s |
| `StatisticsCache.GetOrBuildTypeStatistics` (index hydration, 14,003 MTs) | ~0.03–0.06s |
| `BuildTypeSizeMap` | ~0.03–0.07s |
| snapshot/grouping assembly | ~0.05s |
| **`PopulateEvidence`** | **~42s (54% of total)** |
| **Total (`AnalyzeAsync`)** | **~77–79s** |

Every stage above sums to the measured total, but `FindEventLeaks` itself has an **unexplained
~20s gap** worth flagging separately: the shared `HeapIndexScanDispatcher` fan-out pass for
`EventLeakAnalyzer` alone, measured in isolation via
[DispatcherPass_PerParticipantBreakdown_SinglePassEach](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HeapIndexScanDispatcherPerfTests.cs),
was previously measured at **~17.82s** on this same dump (from the dispatcher-migration
verification work — see [phase0-analyzer-heap-scan-migration-status.md](phase-0/phase0-analyzer-heap-scan-migration-status.md)).
But `FindEventLeaks`, measured as part of the full `AnalyzeAsync` single-pass run in this
investigation, costs **~35–37s** — roughly double. That's a ~17–19s gap that is *not* accounted
for by the dispatcher's own `OnHeapEntry` fan-out cost alone.

**Confirmed root cause of the gap** (verified by reading
[EventLeakAnalyzer.cs:495-527](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs)):
the two measurements are not apples-to-apples — they exercise different code paths.

`FindEventLeaks` checks `_participantScanSucceeded` (set by
`IHeapIndexScanParticipant.OnHeapIndexScanCompleted`, which only the pipeline's
`HeapIndexScanDispatcher` calls) to decide whether to reuse dispatcher-accumulated scan state or
re-scan from scratch:

```csharp
if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _)
    && _participantScanSucceeded && _participantGroupAcc is not null)
{
    // reuse dispatcher-accumulated groupAcc/rootHints/etc. — fast path
}
else
{
    // fresh EventLeakFastScanner pass over the index from scratch
}
```

`DispatcherPass_PerParticipantBreakdown_SinglePassEach` drives `EventLeakAnalyzer` through
`HeapIndexScanDispatcher.Run(...)`, so `BeforeHeapIndexScan` → `OnHeapEntry` (per index entry) →
`OnHeapIndexScanCompleted(true)` all fire, `_participantScanSucceeded` is `true`, and
`FindEventLeaks` takes the fast reuse branch — the ~17.82s figure.

`EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown` (and the doc's `PopulateEvidence`
measurements above) call `analyzer.AnalyzeAsync(context, ...)` **directly**, never going through
`HeapIndexScanDispatcher`. `_participantScanSucceeded` stays at its default `false`, so
`FindEventLeaks` always takes the `else` branch — a completely fresh `EventLeakFastScanner` pass
over the disk index, built from scratch, not reusing any dispatcher-accumulated state. That's a
materially different (and more expensive) code path, which is why it measures ~35–37s instead of
~17.82s.

**Implication:** this is not itself a bug — `AnalysisPipeline`'s real production flow always runs
the dispatcher first, so the ~17.82s fast-path figure is what production actually pays, not the
~35–37s fallback figure. But it does mean the "EventLeakAnalyzer_FullAnalyzeAsync_SinglePass"-style
tests (used for the `PopulateEvidence` investigation above) understate how fast `FindEventLeaks`
is in production, because they exercise the slow fallback path. Any future single-analyzer timing
test intended to reflect real pipeline behavior should run the analyzer through
`HeapIndexScanDispatcher` first (as `DispatcherPass_PerParticipantBreakdown_SinglePassEach` does)
rather than calling `AnalyzeAsync` directly.

The earlier hypothesis — that `StatisticsCache.TryHydrateTypeStatisticsFromIndex`'s redundant
double `heap.GetTypeByMethodTable` call per unique MT (once in `ResolveTypeNameFromSample`, once
in `ResolveModuleNameFromSample`,
[StatisticsCache.cs:181-211](../../src/DumpDetective.Analysis/Cache/StatisticsCache.cs)) was the
source of the gap between the ~18s dispatcher-only scan figure and the ~60s+ observed full-stage
time — was **wrong**. Hydration is ~0.06s regardless; it was never the bottleneck. Real
measurement overturned a plausible-looking static-code-reading hypothesis.

## Root cause

[`EventLeakAnalyzer.PopulateEvidence`](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs)
calls
[`SampleRootPathFinder.TryFindSampleRootPath`](../../src/DumpDetective.Analysis/Traversal/SampleRootPathFinder.cs)
once per top-25 leak instance (`MaxEvidenceInstances = 25`). For each instance,
`TryFindSampleRootPath` iterates **every GC root** (1,411 on this dump) and runs an independent
forward BFS from each root (`carefully: true` reference enumeration, capped at 5,000 visited
objects **per root**), stopping only when a root's BFS happens to reach the target address.

Measured per-instance costs on this dump:

- Instances where a path was found early in the root list: ~0.1–0.3s (fine).
- Instances where **no root ever reaches the target** within budget (18 of 25 instances on this
  dump): ~2.2–2.5s each, because the search burns through most/all of the 1,411 roots × up to
  5,000-object BFS before giving up.

18 × ~2.3s ≈ 41s — matches the measured `PopulateEvidence` cost almost exactly.

This is the "unscoped graph traversal" anti-pattern called out in the project's `CLAUDE.md`
(`Root paths: BFS depth limit (20), visited HashSet<ulong>, stop early if found` — the "stop early
if found" half works, but there's no bound on total work across a failed search spanning many
roots).

## Proposed fix (not yet implemented — pending approval)

Superseded by a broader audit: see
[root-path-search-blast-radius.md](root-path-search-blast-radius.md), which found this same defect
in four call sites (this one, `DominatorAnalyzer`, `TimerLeakAnalyzer`,
`StaticRootLeakDetector`) plus a structurally identical reimplementation in
`ReferenceChainAnalyzer`'s `Fast` search mode. Rather than adding a global-budget cap to
`SampleRootPathFinder`, the proposed fix is to route all of these through
`RootPathFinder` — an already-correct, bounded-candidate-set implementation of the same search
that's already in production use via `ReferenceChainAnalyzer`'s default (`Balanced`) mode.

## How to reproduce / verify

Run (never against a fresh unindexed dump without the env var, and never expect a full
`AnalysisPipeline` run — single analyzer only):

```
DD_RUN_DISCREPANCY_TESTS=1 dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown" \
  --logger "console;verbosity=detailed"
```

Temporary `Stopwatch`/`Console.Error.WriteLine` instrumentation was added to
`EventLeakAnalyzer.Analyze`/`PopulateEvidence` and `StatisticsCache.GetOrBuildTypeStatistics` to
produce these numbers — still present in the source as of this writing, guarded by no flag (just
plain `Console.Error.WriteLine`). Remove once the fix lands and is verified, or keep if judged
generally useful for future perf investigations.

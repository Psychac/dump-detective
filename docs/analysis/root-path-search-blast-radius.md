# Blast radius: the "search everything, find nothing" per-root BFS anti-pattern

> Status: audit complete via code-graph traversal (call-site enumeration), fix not yet implemented.
> See also: [eventleak-populateevidence-root-search-perf.md](eventleak-populateevidence-root-search-perf.md),
> [dominatoranalyzer-populateevidence-root-search-perf.md](dominatoranalyzer-populateevidence-root-search-perf.md)
> — the two real, profiled instances of this bug that triggered this audit.
>
> **Superseded note (2026-08-24):** the `AnalysisProfile.Fast` exposure described below (§ "A
> structurally identical bug in a separate implementation") is resolved — see §9.20/D1 of
> [analysis-profile-removal-plan.md](../refactor/analysis-profile-removal-plan.md).
> `ReferenceChainSearchMode` (and `TryFindAnyRootPath_Fast`, the vulnerable per-root-BFS
> implementation) were deleted outright; `ReferenceChainAnalyzer` now uses a single bounded
> bidirectional search strategy unconditionally, with no `AnalysisProfile`/tier dependency left to
> select it. `MaxCandidateNodes`/`MaxCandidateDepth`/`MaxRootExpansionDepth`/`LargeFanoutThreshold`
> are kept as real (non-tier-varying) limits — see `ReferenceChainOptions.cs`'s own comments — which
> is why the analyzer stays AMBER rather than GREEN in that section, independent of this doc's
> concern. The four `SampleRootPathFinder.TryFindSampleRootPath` call sites below are a separate,
> still-open exposure not addressed by that pass.

## Why this audit exists

Both `EventLeakAnalyzer.PopulateEvidence` and `DominatorAnalyzer.PopulateEvidence` were profiled and
found to spend the large majority of their time (~42s and ~20.9s respectively, on a 1,411-root
reference dump) in `SampleRootPathFinder.TryFindSampleRootPath`. The root cause: for each
"not found" instance, the search burns an independent 5,000-object BFS budget **per GC root**,
so a failed search costs up to `roots.Count × 5,000` object visits instead of a single bounded
amount of work. This audit finds every other place in the codebase exposed to the same defect,
either via the same function or a structurally identical reimplementation.

## Call sites of `SampleRootPathFinder.TryFindSampleRootPath`

Found via code-graph caller lookup on the function's definition
([SampleRootPathFinder.cs:21](../../src/DumpDetective.Analysis/Traversal/SampleRootPathFinder.cs#L21)).
Four real callers (plus one unit test that exercises the function directly):

| Caller | Call shape | Status |
|---|---|---|
| [`EventLeakAnalyzer.PopulateEvidence`](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs#L284) | loop over `MaxEvidenceInstances = 25` leak instances | profiled — ~42s, root cause confirmed |
| [`DominatorAnalyzer.PopulateEvidence`](../../src/DumpDetective.Analysis/Analyzers/DominatorAnalyzer.cs#L563) | loop over `TopHighlyReferencedObjectsToShow = 15` candidates | profiled — ~20.9s, root cause confirmed |
| [`TimerLeakAnalyzer.PopulateEvidence`](../../src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs#L186) | loop over `MaxEvidenceTypes = 10` timer-type summaries | **not yet profiled** — identical call shape (one `TryFindSampleRootPath` call per loop iteration, no shared budget), so it should exhibit the same per-instance ~2.2–2.5s cost on "not found" cases on this dump |
| [`StaticRootLeakDetector.BuildSnapshot`](../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs#L59) | called via `.Select()` over up to `options.MaxRootsToReport` top static roots | **not yet profiled** — same call shape |

All four callers pass the same default budget constants (`DefaultMaxPathSearchObjects = 5_000`,
`AbsoluteMaxDepth = 20`, `DefaultLargeFanoutThreshold = 100`), so all four are exposed to the
identical worst case: cost scales with `roots.Count` for every "not found" instance.

## A structurally identical bug in a separate implementation

[`ReferenceChainAnalyzer.TryFindAnyRootPath_Fast`](../../src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs#L173)
is a second, independent implementation of the exact same pattern: loop over every root, run an
independent BFS per root up to `MaxPathSearchObjects` (5,000 default), give up only after
exhausting the root list. It's called once per top-type
(`AnalyzeTopTypes`, up to `options.TopCount` types), inside
[`ReferenceChainAnalyzer.TryFindAnyRootPath`](../../src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs#L150),
which dispatches to `_Fast` when `options.SearchMode == ReferenceChainSearchMode.Fast`.

This is **not just theoretical exposure** — `AnalysisProfile.Fast` explicitly selects this mode:

```csharp
// ReferenceChainOptions.cs:94-107
AnalysisProfile.Fast => new ReferenceChainOptions
{
    ...
    SearchMode = ReferenceChainSearchMode.Fast,
    MaxPathSearchObjects = 2_000,
    ...
}
```

So any run using the `Fast` analysis profile hits this exact anti-pattern in
`ReferenceChainAnalyzer`, independent of the four `SampleRootPathFinder` call sites above.

## Not affected — already implements the correct pattern

[`RootPathFinder`](../../src/DumpDetective.Analysis/Traversal/RootPathFinder.cs) (used by
`ReferenceChainAnalyzer`'s `Balanced` and `Deep` modes — `Balanced` is
`ReferenceChainOptions.Default`) does not have this defect. Its `TryFindAnyRootPath` builds **one
bounded candidate set** (`CandidateSetBuilder`, capped at `MaxCandidateNodes`) via bidirectional
expansion from roots and from the target, shared across the whole call, then runs a per-root BFS
(`BidirectionalPathFinder`) that is constrained to that already-small candidate set — not the
whole heap. Roots outside the candidate set are skipped outright
([RootPathFinder.cs:85](../../src/DumpDetective.Analysis/Traversal/RootPathFinder.cs#L85)). Total
worst-case work per call is bounded by `MaxCandidateNodes`, independent of how many roots exist on
the heap. This is effectively the fix already proposed for `SampleRootPathFinder`, proven out in a
second, working implementation already in production use (`Balanced` mode is the default).

## Proposed fix: use `RootPathFinder` everywhere instead of patching `SampleRootPathFinder`

Rather than adding a global-budget cap to `SampleRootPathFinder` as a parallel fix, route all five
exposed call sites through the already-correct `RootPathFinder`:

- `EventLeakAnalyzer.PopulateEvidence`
- `DominatorAnalyzer.PopulateEvidence`
- `TimerLeakAnalyzer.PopulateEvidence`
- `StaticRootLeakDetector.BuildSnapshot`
- `ReferenceChainAnalyzer.TryFindAnyRootPath_Fast` (or simply delete `Fast` mode and always use the
  bidirectional path, since `RootPathFinder` with a small `MaxCandidateNodes` budget is a strict
  upgrade over the unbounded-relative-to-root-count fast-mode BFS)

`RootPathFinder`'s constructor deliberately takes an injectable `IReferenceProvider`,
`RootPathSearchLimits`, `IPathSearchTelemetry`, and noise/force-expand predicates so each caller can
own its own configuration — this is exactly what it was extracted for, and
`ReferenceChainAnalyzer.TryFindAnyRootPath_Bidirectional` already constructs it inline rather than
through a wrapper. The fix should follow that precedent: each of the four `PopulateEvidence`-style
call sites constructs `RootPathFinder` directly, with its own `RootPathSearchLimits` tuned for a
cheap single-path evidence lookup (a much smaller `MaxCandidateNodes` budget than
`ReferenceChainAnalyzer`'s `Balanced`-mode 50,000 is appropriate here — likely close to
`SampleRootPathFinder`'s existing 5,000 default). Adding a new static facade in front of
`RootPathFinder` would just reintroduce the same one-size-fits-all-utility shape that caused this
bug in the first place, one layer removed.

The only pieces worth actually sharing are the pure, logic-bearing bits that would otherwise be
duplicated verbatim across all four call sites:

- the noise-type filter predicate (currently private `IsNoisyType` in `SampleRootPathFinder`)
- a no-op `IPathSearchTelemetry` implementation (zero logic — just satisfies the interface for
  callers that don't need telemetry)
- the address-list → formatted path string conversion (currently `SampleRootPathFinder.FormatPath`)

These can move to small shared static utilities; the search construction and configuration itself
stays inline per call site.

**Tradeoff to note:** `RootPathFinder` always builds its candidate set before searching (bounded by
`MaxCandidateNodes`, but not free), whereas `SampleRootPathFinder`'s naive per-root BFS returns
almost instantly when a path is found on an early root (~0.0–0.3s measured for found-early cases in
both profiled analyzers). Switching may add a small constant cost to the already-fast found-early
cases. Given the measured costs are overwhelmingly dominated by not-found cases (18/25 and 9/15
respectively on the reference dump), this should still be a large net win.

## How to verify once implemented

Re-run the existing single-pass timing tests and confirm `PopulateEvidence`/`Analyze` totals drop
close to the found-early baseline (~0.1–0.3s × instance count) instead of being dominated by
not-found searches:

```
DD_RUN_DISCREPANCY_TESTS=1 dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HeapIndexScanDispatcherPerfTests" \
  --logger "console;verbosity=detailed"
```

Add equivalent single-pass timing tests for `TimerLeakAnalyzer` and `StaticRootLeakDetector` (none
exist today) before/after the fix, to get real before/after numbers for those two rather than
relying on the "identical call shape" inference in this doc.

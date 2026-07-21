# Heap-Index Single-Pass Scan Dispatcher — Implementation Plan

> Status: **Not started (implementation paused pending re-prioritization)**, 2026-07-21.
> Persisted from an approved plan-mode design so the plan survives context resets. See the
> [Deliverable 10 correction note](phase0-deliverable-10-platform-roadmap.md#correction--2026-07-21-verified-heap-scan-analyzer-count)
> for why this was paused: verification shows the dispatcher addresses **9 of 35** analyzers
> (not 26 of 36 as originally estimated), which changes this item's priority relative to the
> Correctness track (evidence bus / leak-scoring fragmentation) and warrants a fresh call before
> committing implementation effort.

## Context

`phase0-deliverable-10-platform-roadmap.md` (and its supporting `phase0-deliverable-5-shared-infrastructure.md`
item 1 / `phase0-deliverable-8-performance-architecture-review.md` §1) originally cited "26 of 36
analyzers independently stream the full on-disk object index." That figure was explicitly
self-flagged as architectural/estimated, not measured. Verifying it directly by grepping all 35
`IAnalyzer` implementations under `src/DumpDetective.Analysis/Analyzers/` turned up a materially
different, smaller number:

- **9 analyzers** stream the on-disk `HeapEntry` index via `EnumerateIndexedEntries()` /
  `EnumerateIndexedEntriesAsTuples()`: `DbConnectionAnalyzer`, `CrashAnalyzer`,
  `CollectionAnalyzer` (two call sites), `AsyncTaskAnalyzer`, `HangAnalyzer`,
  `EventLeakAnalyzer` (two call sites), `MemoryLeakAnalyzer`, `WcfChannelAnalyzer`,
  `StringAnalyzer`.
- **5 more analyzers** perform a full `ClrHeap.EnumerateObjects()` sweep with no index path at
  all — `TimerLeakAnalyzer`, `HttpObjectAnalyzer`, `FinalizableObjectAnalyzer`, plus
  `LohFragmentationAnalyzer`/`HeapTopologyAnalyzer` (per-segment, not whole-heap). These are
  architecturally distinct from the index-scan problem: `ClrHeap.EnumerateObjects()` is a live
  ClrMD walk, not a read of the on-disk `HeapEntry` index, so a dispatcher built around
  `HeapAnalysisCache.EnumerateIndexedEntries()` cannot help them without a second, separate
  mechanism.

So **14 of 35** analyzers do some form of full/broad heap traversal — not 26, and this plan
targets the **9** that are index-based (the other 5 are called out explicitly as out of scope
below). For a 10GB+ dump this still means up to 9 sequential full-index reads where the
architecture's own design intent (`docs/architecture.md` Phase 1/Phase 2 split) assumes one.

The roadmap doc (Deliverable 5 item 1) explicitly recommends shape **(a)**: add an opt-in
per-object visitor callback that a shared dispatcher invokes once per index record, fanning out
to every registered analyzer that opts in — over shape (b) (handing out a shared,
position-tracked reader), which it flags as riskier because it couples analyzer execution order
to stream position. This plan builds (a).

Given the roadmap rates this item **Difficulty: High**, this plan intentionally scopes to
building the dispatcher infrastructure correctly and proving it end-to-end on one real analyzer,
rather than migrating all 9 in one pass.

## Design

### Why the interface stays internal to `DumpDetective.Analysis`

`HeapEntry` (`src/DumpDetective.Analysis/Indexing/HeapEntry.cs`) is `internal` to
`DumpDetective.Analysis`. `IAnalyzer` lives in `DumpDetective.Core` and must stay
dump-index-agnostic. All concrete analyzers are themselves declared inside
`DumpDetective.Analysis`, and so is `AnalysisPipeline`. So the new opt-in interface and the
dispatcher can both live entirely inside `DumpDetective.Analysis` — no change to the public
`IAnalyzer` contract or to `DumpDetective.Core` is needed for this phase.

### New opt-in interface — `src/DumpDetective.Analysis/Pipeline/IHeapIndexScanParticipant.cs`

```csharp
internal interface IHeapIndexScanParticipant
{
    void BeforeHeapIndexScan(AnalysisContext context);
    void OnHeapEntry(in HeapEntry entry);
}
```

Analyzers implement this alongside `IAnalyzer` when they want to consume the shared index pass
instead of enumerating it themselves. `OnHeapEntry` is called once per index record, in address
order, during a single shared pass. Analyzers accumulate into their own private instance fields.

Two phases, not one — deliberately, to avoid a per-entry "is this the first call" branch on the
hot path (millions of `OnHeapEntry` invocations) and to keep setup independently testable from
per-entry accumulation. `BeforeHeapIndexScan` runs once per analyzer, before the shared pass
starts, so analyzers can seed candidate-filter state (e.g. `DbConnectionAnalyzer`'s
`candidateMts`/`typeStats` built from `TypeAggregates`) without touching the heap index.
`OnHeapEntry` then assumes that setup is already done and does pure filter + accumulate work.

### New dispatcher — `src/DumpDetective.Analysis/Pipeline/HeapIndexScanDispatcher.cs`

```csharp
internal sealed class HeapIndexScanDispatcher
{
    public void Run(HeapAnalysisCache cache, IReadOnlyList<IHeapIndexScanParticipant> participants,
        CancellationToken cancellationToken)
    {
        if (participants.Count == 0 || !cache.TryGetHeapIndex(out _))
            return;

        foreach (HeapEntry entry in cache.EnumerateIndexedEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < participants.Count; i++)
                participants[i].OnHeapEntry(in entry);
        }
    }
}
```

Reuses the existing `HeapAnalysisCache.EnumerateIndexedEntries()` (already streams via
`ObjectIndexReader` + `ArrayPool`) — no new I/O path. Falls back to no-op when there's no
participant or no index (e.g. memory-only/test contexts), leaving non-participating analyzers'
own `heap.EnumerateObjects()` fallback paths untouched.

### Wiring into `AnalysisPipeline.ExecuteAsync`

In `src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`, immediately after the `RunStarted`
diagnostics event and before the `foreach (IAnalyzer analyzer in _analyzers)` loop:

```csharp
var participants = _analyzers.OfType<IHeapIndexScanParticipant>().ToArray();
if (context.Cache is HeapAnalysisCache heapCache)
{
    foreach (var participant in participants)
        participant.BeforeHeapIndexScan(context);

    new HeapIndexScanDispatcher().Run(heapCache, participants, cancellationToken);
}
```

This guarantees the shared pass completes before any participating analyzer's `AnalyzeAsync`
runs, so accumulated state is safe to read unconditionally **from within the pipeline**.

### Open design problem found during implementation review (blocking)

`tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DbConnectionAnalyzerDiscrepancyTests.cs`
(and likely other discrepancy/unit tests) call `analyzer.AnalyzeAsync(context, ...)` **directly**,
bypassing `AnalysisPipeline` entirely. If `DbConnectionAnalyzer` is migrated to rely on the
pipeline calling `BeforeHeapIndexScan`/`OnHeapEntry` before `AnalyzeAsync`, any caller that
invokes `AnalyzeAsync` standalone (tests, and potentially other direct-invocation call sites not
yet audited) will get empty/stale instance-field state, silently breaking correctness rather than
failing loudly.

This must be resolved before migrating `DbConnectionAnalyzer` — options considered, not yet
decided:
1. `AnalyzeAsync` detects whether the dispatcher pass already populated its state this call
   (e.g. a per-call "primed" flag reset at the start of `AnalyzeAsync` and set by
   `BeforeHeapIndexScan`/dispatcher) and falls back to doing its own self-contained scan
   (today's code path) when not primed — preserves standalone-call correctness at the cost of
   the interface having implicit dual-mode behavior.
2. Change the discrepancy/unit tests to always drive analyzers through a (possibly minimal)
   pipeline/dispatcher invocation instead of calling `AnalyzeAsync` directly — cleaner contract,
   but requires auditing every direct-call test site first, and changes an established test
   pattern used elsewhere (see other `*DiscrepancyTests.cs`).
3. Keep `DbConnectionAnalyzer` as-is (no migration) until a decision is made — the dispatcher and
   participant interface can still be built and proven with a synthetic/fake participant in unit
   tests, deferring the real-analyzer migration.

Given this was found while investigating the exact analyzer count (which triggered the priority
re-check above), **the recommendation is to resolve the priority question first** (is this still
P0, or does the corrected 9-of-35 blast radius change the answer?) before spending further design
effort on option 1 vs 2 vs 3.

## Proof-of-concept migration target — `DbConnectionAnalyzer`

Chosen because it's the simplest of the 9: its type-candidate discovery already runs off
`TypeAggregates` (no heap scan), and its only index scan
(`DbConnectionAnalyzer.cs:132-168`) is a single filter-and-accumulate loop with no ordering
dependency — a clean fit for the visitor shape, *once* the standalone-call problem above is
resolved.

Convert `DbConnectionAnalyzer` to implement `IHeapIndexScanParticipant`:
- Move the per-type accumulator dictionaries (`typeStats`, `topOpen`, `perTypeSamples`,
  `stateSamples`, `stateScanCapped`) to instance fields.
- `BeforeHeapIndexScan(context)` builds `candidateMts`/seeds `typeStats` from `TypeAggregates`
  exactly as today's Step 1 does.
- `OnHeapEntry(in entry)` body becomes the existing filter + state-read + tally logic unchanged,
  just operating on instance fields instead of locals.
- `AnalyzeAsync` becomes: run the existing fallback path when there's no index (unchanged),
  otherwise skip straight to Step 3 (build `DbConnectionDomainResult` from the now-populated
  instance fields) — **contingent on resolving the standalone-call problem above**.
- The `heap.EnumerateObjects()` fallback path (no index available) stays exactly as-is in
  `AnalyzeAsync` — the dispatcher is index-only by design.

Keep `DbConnectionAnalyzer`'s public behavior and `DbConnectionDomainResult` output bit-for-bit
identical — this is a scan-mechanism change only, not a behavior change.

## Diagnostics gap (flagged, not solved by this plan)

Today `AnalyzerRunResult.ObjectScanCount` is computed as the delta of
`context.Cache.ObjectScanCount` around each analyzer's own `AnalyzeAsync` call. Once a
participant's scan work moves into the shared dispatcher pass, that analyzer's own reported
`ObjectScanCount` will read as ~0, and the dispatcher's pass isn't attributed to any single
analyzer. Emit a dedicated diagnostics event for the dispatcher pass rather than silently losing
that number — no existing `AnalysisDiagnosticsEventType` value fits without adding one; check
`tests/DumpDetective.Tests/Unit/Analysis/AnalysisPipelineTests.cs` (the actual existing test file
— note earlier drafts of this plan referenced non-existent `AnalysisDiagnosticsTests.cs` /
`HeapIndexCacheTests.cs` / `RootIndexReaderTests.cs` paths that do not exist in this repo) for
what's currently asserted before changing event shapes.

## Tests / verification (once unblocked)

- `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DbConnectionAnalyzerDiscrepancyTests.cs`
  already exists and compares disk-index-mode vs in-memory-mode output — but calls `AnalyzeAsync`
  directly (see blocking problem above), so it must keep passing unmodified, or be deliberately
  and visibly updated as part of resolving that problem.
- Add a `HeapIndexScanDispatcherTests.cs` unit test: register 2+ fake `IHeapIndexScanParticipant`s,
  run the dispatcher once, assert each received every entry exactly once and in the same order.
- Extend `tests/DumpDetective.Tests/Unit/Analysis/AnalysisPipelineTests.cs` with a case asserting
  the index is enumerated exactly once per run when a participant is registered.
- `dotnet test` for the full suite to catch pipeline-ordering regressions in unrelated analyzers.

## Explicitly out of scope for this pass

- Migrating the remaining 8 index-scanning analyzers (`CrashAnalyzer`, `CollectionAnalyzer`,
  `AsyncTaskAnalyzer`, `HangAnalyzer`, `EventLeakAnalyzer`, `MemoryLeakAnalyzer`,
  `WcfChannelAnalyzer`, `StringAnalyzer`) — follow-up work once the `DbConnectionAnalyzer` POC
  validates the pattern.
- The 5 analyzers that scan via `ClrHeap.EnumerateObjects()` with no index path at all
  (`TimerLeakAnalyzer`, `HttpObjectAnalyzer`, `FinalizableObjectAnalyzer`, and the per-segment
  `LohFragmentationAnalyzer`/`HeapTopologyAnalyzer`) — this dispatcher structurally cannot help
  them; would need a second dispatcher variant or index migration first.
- Per-type statistics engine (Deliverable 5 item 2) and object metadata classification (item 5) —
  sequenced after this dispatcher exists per the roadmap.
- Any change to `IAnalyzer`/`AnalysisContext` in `DumpDetective.Core`.

## Next step

Re-evaluate priority against the Correctness track (evidence bus, Deliverable 5 item 11 / item 6)
now that the dispatcher's verified blast radius (9 of 35, not 26 of 36) is known — see the
[Deliverable 10 correction note](phase0-deliverable-10-platform-roadmap.md#correction--2026-07-21-verified-heap-scan-analyzer-count).
If still prioritized, resolve the standalone-call design problem above before writing any code.

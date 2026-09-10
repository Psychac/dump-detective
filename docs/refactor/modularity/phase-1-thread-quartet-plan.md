# Thread-domain quartet retyping plan

Planning-only doc (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md § batch
history references this) for retyping the four analyzers that share
`ThreadStackScanDispatcher`'s single shared stack walk: `ThreadAnalyzer`, `HangAnalyzer`,
`ThreadStackClusterAnalyzer`, `LockGraphAnalyzer`. Written after Batch 6 deferred `ThreadAnalyzer`
on the (at-the-time correct) assumption that retyping it alone would force the pipeline to walk
every thread's stack twice — once for the still-legacy quartet members, once independently for a
capability-based `ThreadAnalyzer`. That assumption is revisited and corrected below.

## 1. The shared-scan mechanism, precisely

`AnalysisPipeline.RunSharedScans` does:

```csharp
IReadOnlyList<IThreadStackScanParticipant> threadStackScanParticipants =
    _analyzers.OfType<IThreadStackScanParticipant>().ToArray();
...
new ThreadStackScanDispatcher().Run(context.Runtime, context, threadStackScanParticipants, maxFramesPerThread, cancellationToken);
```

`_analyzers` is the pipeline's registered `Core.Abstractions.IAnalyzer` list — for an
already-retyped analyzer, that list holds its **`LegacyAnalyzerAdapter<T>` subclass**, not the SDK
analyzer itself (the pipeline never sees the SDK analyzer directly). `OfType<IThreadStackScanParticipant>()`
does not care whether an entry is a raw legacy analyzer or an adapter — it only checks the runtime
interface. This is the key fact Batch 6's deferral decision missed: **an adapter can implement
`IThreadStackScanParticipant` itself**, exactly like `GCRootAnalyzerLegacyAdapter` already implements
`IRequiresReachableGraphIndex`/`IRequiresDominatorTreeIndex` (Batch 3) to keep a different piece of
pipeline-level opt-in behavior working after retyping. Doing this here means the shared walk still
runs exactly once, fanned out to however many of the four are retyped-via-adapter vs. still
directly implementing the interface, at any point during the migration — no regression, no
all-or-nothing requirement to retype all four together.

## 2. Corrected tier classification (verified 2026-09-11 by reading full source, not grepping)

The original plan's grep-derived split only checked for `ReadField`/`.Read<T>`/`TryReadStringField`
substrings in one pass; re-checking each of the four individually found it was wrong twice, in both
directions:

| Analyzer | Original classification | Corrected | Why |
|---|---|---|---|
| `ThreadAnalyzer` | Tier 1 only | **Tier 1 + Tier 2** (corrected again, 2026-09-11) | The shared-scan reason for deferring it is resolved (§ 3's bridge works), but re-reading the full source found `ProcessThread` sets `ThreadWithStackTrace.ExceptionMessage = currentException.Message` — `ClrException.Message` (reflected via ilspycmd against v4.0.732401) reads a raw field offset off the live exception object (`Type.Module.DataReader.ReadPointer(Address + messageOffset)` then resolves the string), real Tier-2 field-value extraction, same category as `Task.m_stateFlags`/`AssemblyLoadContext._name`. (`currentException.Type?.Name`, the only other `ClrException` member it touches, stays Tier 1 — `Type` is pure type resolution, no field read.) Deferred, alongside `HangAnalyzer`. |
| `HangAnalyzer` | Tier 1 + Tier 2 | **Tier 1 + Tier 2** (confirmed) | `stateField.Read<int>(obj, interior: false)` reads a live `Task`'s `m_stateFlags` field directly — real field-value extraction. Also implements a *second* shared-scan interface, `IParallelHeapIndexScanParticipant` (parallel-worker heap-index scan, separate from the thread-stack scan) — more machinery than the other three, and blocked on the same `dump.object-fields` hatch as `FinalizableObjectAnalyzer`/`ModuleAnalyzer`. Stays deferred. |
| `LockGraphAnalyzer` | Tier 1 + Tier 2 | **Tier 1 only** (corrected). Retyped 2026-09-11 — § 7. | No field-value reads found. Uses `heap.EnumerateSyncBlocks()` (`IHeapSyncBlockQuery`, declared, unimplemented), `IHeapObjectLookup` (already built) for type-name-by-address, and structural `ClrThread`/`ClrStackFrame` facts only. |
| `ThreadStackClusterAnalyzer` | Tier 1 + Tier 2 | **Tier 1 only** (corrected). Retyped 2026-09-11 — § 8. | No field-value reads found. Uses only thread state flags (`IsGc`/`IsFinalizer`/`TS_TPWorkerThread`), method signatures, and frame names — all structural. |

Net effect: **two of the four (`LockGraphAnalyzer`, `ThreadStackClusterAnalyzer`) were retyped**;
`ThreadAnalyzer` and `HangAnalyzer` are both genuinely blocked on Tier 2 and stay deferred until the
`dump.object-fields` escape hatch exists. This closes out the thread-domain quartet's Tier-1 work —
see the main plan doc's "Remaining Tier-1-only" note for the project-wide conclusion this implies.

## 3. The adapter-side push/pull bridge

Every other retyped analyzer's `Sdk.Analysis.IAnalyzer.AnalyzeAsync` pulls its data by calling
capability methods on `context` (e.g. `context.RuntimeThreads.EnumerateStackFrames(thread)`), which
the dump-side implementation resolves live, on demand. That pull model is exactly what would cause
the double-walk: if three of the four analyzers still push-accumulate through
`IThreadStackScanParticipant.OnThreadStack` and a fourth, retyped one pulls independently inside
`AnalyzeAsync`, the dispatcher's walk and the fourth analyzer's pull are two separate stack walks.

The fix is for the **retyped analyzer's adapter** to be the thing that implements
`IThreadStackScanParticipant`, not the SDK analyzer, and to bridge push → pull itself:

1. Adapter implements `GetRequiredFrameCount`/`BeforeThreadStackScan`/`OnThreadStack`/
   `OnThreadStackScanCompleted`, exactly mirroring what the pre-retyping analyzer's own participant
   methods did — except instead of accumulating analyzer-specific state, it accumulates a
   **generic, reusable snapshot**: a `Dictionary<uint OsThreadId, PrecomputedThreadStack>` where
   `PrecomputedThreadStack` holds whatever the SDK's `RuntimeThreadRef`/`ThreadStackFrameRef` shapes
   need (translated from `ClrThread`/`ClrStackFrame` once, during `OnThreadStack`, using the same
   translation logic `RuntimeThreadQuery`/`RuntimeThreadQuery.EnumerateStackFrames` already has —
   factor that translation into a shared static helper both the live and precomputed paths call, so
   there is exactly one place that maps `ClrThread`/`ClrStackFrame` → SDK shapes).
2. When the pipeline later calls the adapter's `AnalyzeAsync` (in the normal per-analyzer loop,
   after `RunSharedScans` has already completed), the adapter builds its `Sdk.Analysis.AnalysisContext`
   with `RuntimeThreads` set to a **new `PrecomputedRuntimeThreadQuery`** wrapping the accumulated
   dictionary, instead of `LegacyAnalysisContextTranslator`'s normal live `RuntimeThreadQuery`. The
   inner SDK analyzer's `AnalyzeAsync` calls `context.RuntimeThreads.EnumerateStackFrames(thread)`
   exactly as it always would — it has no idea whether the data came from a live walk or a
   precomputed cache.
3. This generalizes beyond the quartet: **any** future analyzer that needs thread stacks and cares
   about the shared-walk optimization can use the same `PrecomputedRuntimeThreadQuery` +
   participant-adapter pattern. It is not quartet-specific plumbing.
4. Fallback path (direct/benchmark invocation, no pipeline, no shared scan): the adapter's
   `AnalyzeAsync` checks whether `OnThreadStackScanCompleted` ever fired; if not, it falls back to
   constructing a normal live `RuntimeThreadQuery` (today's Batch-5 one) instead of the precomputed
   one — mirroring exactly the `_participantScanSucceeded` fallback branch every one of the four
   analyzers already has today.

This is more adapter-side code than any batch so far (adapters have so far been ~10-line pass-throughs),
but it is bounded, mechanical, and shared across all three retyped members rather than reinvented
per analyzer — likely worth factoring into a small reusable base
(`ThreadStackScanParticipantLegacyAdapter<T>` or similar) once the first one is built.

## 4. Capability gaps found while sizing this (historical — all resolved or moot now)

- **`RuntimeThreadRef` extension.** `IsAlive` (Batch 5), `LockCount` (Batch 8, `LockGraphAnalyzer`),
  and `IsGc`/`IsFinalizer`/`IsThreadpoolWorker`/`IsCompletionPortThread` (Batch 9,
  `ThreadStackClusterAnalyzer`, see § 8) all shipped. `ThreadAnalyzer`'s remaining needs
  (`AppDomainName`, `GcMode`, `TS_Background`, `StackBase`/`StackLimit`, current-exception info) are
  moot now that `ThreadAnalyzer` is confirmed deferred — see the next point.
- **Resolved: `ClrException.Message`/`.Type` Tier question.** Reflected `ClrException` via ilspycmd
  against v4.0.732401 (not guessed): `.Message` reads a raw field offset off the live exception
  object (`Type.Module.DataReader.ReadPointer(Address + messageOffset)`, then resolves the string) —
  genuine Tier 2, same category as `Task.m_stateFlags`/`AssemblyLoadContext._name`. `.Type` is pure
  type resolution (`_object.Type`) — Tier 1. `ThreadAnalyzer`'s `ProcessThread` sets
  `ThreadWithStackTrace.ExceptionMessage = currentException.Message` — a real, reported field, not
  incidental — so `ThreadAnalyzer` is confirmed Tier 1 + Tier 2 and deferred (§ 2's corrected table).
- **`HeapSyncBlockRef` extension for `LockGraphAnalyzer`** — shipped in Batch 8 (§ 7):
  `HasHoldingThread`/`RecursionCount`/`WaitingThreadCount` added; `HoldingOsThreadId` (not a raw
  `ClrThread` address) used to correlate lock ownership against `RuntimeThreadRef.Thread.OsThreadId`.
- **`HeapSyncBlockQuery`** — implemented in Batch 8 (§ 7) as `HeapSyncBlockQuery`.

## 5. Batch order (as executed)

1. **`LockGraphAnalyzer`. Done 2026-09-11** — see § 7 for what actually shipped.
2. **`ThreadStackClusterAnalyzer`. Done 2026-09-11** — see § 8 for what actually shipped.
3. **`ThreadAnalyzer`: confirmed deferred, 2026-09-11** — genuinely Tier 1 + Tier 2 once the
   `ClrException.Message` question above was resolved; not retyped. Joins `HangAnalyzer` (below).
4. **`HangAnalyzer` stays deferred**, grouped with `FinalizableObjectAnalyzer`/`ModuleAnalyzer`/
   `ThreadAnalyzer` for whenever the `dump.object-fields` Tier-2 escape hatch gets built — its
   `IParallelHeapIndexScanParticipant` side is unrelated to this doc's scope entirely (that's the
   *heap-index* shared scan, not the thread-stack one) and would need its own investigation
   regardless.

This closes out the thread-domain quartet's Tier-1 work: 2 of 4 members retyped, 2 confirmed and
deferred to Tier 2 — no member is left unresolved or unscoped.

## 6. Gates (once actual implementation starts)

Same discipline as Batches 1–7: characterization test against a self-attached live process
(cross-checked against direct `ClrThread`/`ClrStackFrame`/`SyncBlock` ground truth, since none of
this data can be hand-crafted via reflection injection), one `[DiscrepancyFact]` real-dump test per
analyzer run standalone in the foreground, full non-real-dump suite green before and after each
batch. Additionally, for this batch specifically: a test proving the shared-scan optimization itself
still holds post-retyping (e.g. instrument or count `ClrThread.EnumerateStackTrace()` calls across a
pipeline run with a mix of retyped-adapter and legacy quartet members, assert exactly one walk per
thread) — this property is the entire reason this migration needed its own plan, so it should be
asserted directly, not just assumed from the design.

## 7. `LockGraphAnalyzer` — done 2026-09-11

Shipped essentially as designed in § 3, with two things found only once actually building it:

- **A third field on `HeapSyncBlockRef` was needed, not anticipated in § 4**: `HasHoldingThread`
  (mirrors `SyncBlock.HoldingThreadAddress != 0`), distinct from `HoldingOsThreadId` being non-null.
  The pre-retyping analyzer's `LocksWithOwnerAddress`/`UnresolvedOwnerCount` domain-result fields
  depend on telling apart "no owner at all" from "owner address present but didn't resolve to a live
  thread" — collapsing both into one nullable `HoldingOsThreadId` (the design § 4 sketched) would
  have made that distinction unrecoverable. Caught before it shipped, while writing the analyzer
  against the interface, not after.
- **`LegacyAnalyzerAdapter<TSdkAnalyzer>` itself needed one small addition**: a
  `protected virtual Sdk.Analysis.AnalysisContext BuildSdkContext(Core.Abstractions.AnalysisContext context)`
  method (default: today's `LegacyAnalysisContextTranslator.Translate` call), which
  `AnalyzeAsync` now calls instead of the static translator directly. This is the actual seam the
  adapter-side bridge hooks into — `LockGraphAnalyzerLegacyAdapter` overrides it to pass a
  `PrecomputedRuntimeThreadQuery` (or `null` to fall back to the normal live one) via a new optional
  `runtimeThreadsOverride` parameter on `LegacyAnalysisContextTranslator.Translate`. Purely additive;
  every other existing adapter is unaffected (uninherited override, same default behavior).
- **`ThreadStackTranslator`** (`DumpDetective.Analysis/Sdk/`) extracted the `ClrThread`/`ClrStackFrame`
  → `RuntimeThreadRef`/`ThreadStackFrameRef` mapping out of `RuntimeThreadQuery` into a shared static
  helper, used by both the live query and the adapter's `OnThreadStack` accumulation — exactly the
  "one translation, two call sites" § 3 point 1 called for.
- **`RuntimeThreadRef` gained `LockCount`** (mirrors `ClrThread.LockCount`) — the only new field this
  analyzer needed from § 4's larger anticipated list (the rest — `AppDomainName`, `GcMode`, `IsGc`,
  `IsFinalizer`, state flags, stack size, exception info — are `ThreadAnalyzer`/
  `ThreadStackClusterAnalyzer`'s needs, not built yet, per hard-need-basis).
- **The owner-thread frame capture simplified**: the pre-retyping analyzer's `CaptureOwnerThreadFrames`
  did a second, independent, unbounded `EnumerateStackTrace()` walk for each deadlock candidate (on
  top of the participant-captured top-frame signature). The retyped version reuses the same
  `FrameScanDepth`-bounded frames the shared scan already captured instead — an accepted, narrow
  simplification (documented in the analyzer itself): if fewer than 3 of a candidate's first 8 frames
  resolve to a method signature, this returns fewer than 3 instead of continuing further down the
  stack, which deadlock candidates being rare makes acceptable.
- **Gate met, and then some**: beyond the usual characterization/real-dump pair
  (`LockGraphAnalyzerRetypingCharacterizationTests`, `LockGraphAnalyzerRealDumpTests`), a
  *pre-existing* test suite (`LockGraphAnalyzerLiveHeapTests`) using real background threads and real
  `lock` statements to force genuine monitor contention and a real deadlock-candidate scenario needed
  only a one-line update (call through the adapter instead of the old analyzer directly) and passed
  unchanged — strong evidence the retyping preserved real behavior, not just structurally-similar
  behavior. Also added `AnalysisPipelineTests.ExecuteAsync_ScansThreadStacksExactlyOnce_WhenRetypedAndLegacyParticipantsAreMixed`
  (§ 6's own called-for gate) — mixes `LockGraphAnalyzerLegacyAdapter` with two dummy legacy
  `IThreadStackScanParticipant`s in a real `AnalysisPipeline` run and asserts each sees every thread
  exactly once, proving the shared-scan property directly rather than by inspection. Full
  non-real-dump suite (1240 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark`, `LockGraphAnalyzerBenchmark` (retargeted at
  `AnalyzerBenchmarkBase<LockGraphAnalyzerLegacyAdapter>` — it calls `AnalyzeAsync` directly, not
  through the pipeline, so it exercises the adapter's live-query fallback path, not the precomputed
  one).

## 8. `ThreadStackClusterAnalyzer` — done 2026-09-11

Shipped exactly the push/pull bridge § 3 designed, reusing `LockGraphAnalyzerLegacyAdapter`'s shape
almost verbatim (`ThreadStackClusterAnalyzerLegacyAdapter` implements `IThreadStackScanParticipant`
and overrides `BuildSdkContext` the same way). A few things found only while building it:

- **`RuntimeThreadRef` gained `IsGc`, `IsFinalizer`, `IsThreadpoolWorker` (`TS_TPWorkerThread`), and
  `IsCompletionPortThread` (`TS_CompletionPortThread`)** — exactly the four facts § 4 anticipated,
  no more. Used both to accumulate per-cluster `ThreadpoolWorkerCount`/`GcCount`/`FinalizerCount` and
  to synthesize the `"<No managed frames> (...)"` signature for threads with no resolvable frames.
- **`ThreadStackFrameRef` needed two new raw fields, not anticipated in § 4**: `FrameName` (raw
  `ClrStackFrame.FrameName`, populated for every frame regardless of `HasMethod`) and
  `RawMethodSignature` (raw `ClrMethod.Signature`, `null` when absent — no fallback). The existing
  `MethodDisplayName` field couldn't be reused for cluster-signature purposes: `JitAnalyzer` already
  depends on its synthesized `Type.Method` fallback when `Signature` is `null`, but the pre-retyping
  analyzer's `BuildSignature` needed the *raw* signature-or-frame-name chain with no synthesis, to
  reproduce cluster identity exactly. Kept the two concerns orthogonal (raw fields vs. a
  business-logic-flavored display field) rather than parameterizing `MethodDisplayName`'s fallback.
- **The address-indirection machinery in the pre-retyping analyzer (`osThreadIdByAddress`,
  `ProjectSampleOsThreadIds`, `StackCluster.SampleThreadAddresses`) disappeared entirely** — it only
  ever existed to map a `ClrThread.Address` back to an `OSThreadId` for display, and
  `RuntimeThreadRef.Thread.OsThreadId` already carries that directly. `StackCluster` now accumulates
  `SampleOsThreadIds` (`List<uint>`) straight from each thread as it's processed; the retyped
  `Analyze` and its NDJSON/JSON export block are correspondingly simpler than the pre-retyping
  version, not just re-typed.
- **All pure-string/pure-logic static members kept their exact pre-retyping signatures**:
  `StackCluster`, `BuildClusterTree`, `ConvertTrieNode`, `ClassifyFrameworkPattern`,
  `BuildTopFrameHotspots` — none of them touch ClrMD types, so none needed to change, and the
  pre-existing `ThreadStackClusterAnalyzerOptionsTests` (23 tests exercising these directly) passed
  unchanged with zero edits.
- Progress reporting (`ObjectScanCounter` ticking during the stack walk) was dropped, matching the
  precedent already set by `LockGraphAnalyzer`'s retyping (§ 7) and every other retyped Tier-1
  analyzer — none of them thread `context.Progress` through their `Analyze` method.
- New gates: `ThreadStackClusterAnalyzerRetypingCharacterizationTests` (self-attached process vs.
  `ClrThread`/`ClrMD` ground truth — alive-thread count, cluster-count-sums-to-alive-threads,
  per-cluster sample-ID-count-equals-cluster-count) and `ThreadStackClusterAnalyzerRealDumpTests`
  (`[DiscrepancyFact]`, run standalone in the foreground, ~21s against the reference 3.5GB dump, same
  invariants). `AnalysisPipelineTests.ExecuteAsync_ScansThreadStacksExactlyOnce_WhenRetypedAndLegacyParticipantsAreMixed`
  (§ 6's gate) extended to mix both retyped quartet adapters (`LockGraphAnalyzerLegacyAdapter` and
  `ThreadStackClusterAnalyzerLegacyAdapter`) alongside the two dummy legacy participants in one
  pipeline run, asserting each still sees every thread exactly once and both adapters' own results
  come back correctly. Full non-real-dump suite (1241 tests) passes; `LockGraphAnalyzerRealDumpTests`
  re-run standalone afterward to confirm the shared `RuntimeThreadRef`/`ThreadStackFrameRef` field
  additions caused no regression there.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark`, `ThreadStackClusterAnalyzerBenchmark` (retargeted at
  `AnalyzerBenchmarkBase<ThreadStackClusterAnalyzerLegacyAdapter>`, same live-query-fallback caveat as
  `LockGraphAnalyzerBenchmark`).

# Thread-domain quartet retyping — status: done (2026-09-11)

Covers the four analyzers that share `ThreadStackScanDispatcher`'s single stack walk:
`LockGraphAnalyzer`, `ThreadStackClusterAnalyzer`, `ThreadAnalyzer`, `HangAnalyzer`. Referenced from
docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.

## 1. Outcome

| Analyzer | Tier | Status |
|---|---|---|
| `LockGraphAnalyzer` | Tier 1 | **Retyped** — § 5 |
| `ThreadStackClusterAnalyzer` | Tier 1 | **Retyped** — § 6 |
| `ThreadAnalyzer` | Tier 1 + Tier 2 | **Deferred** — reports `ClrException.Message` (§ 4) |
| `HangAnalyzer` | Tier 1 + Tier 2 | **Deferred** — reads `Task.m_stateFlags` directly (§ 4) |

2 of the 4 quartet members are retyped onto the SDK's `Sdk.Analysis.IAnalyzer`. The other 2 are
confirmed to need a live object field value that only exists on the heap (Tier 2), and stay on the
legacy `Core.Abstractions.IAnalyzer` until the `dump.object-fields` Tier-2 escape hatch is built —
grouped with `ModuleAnalyzer`/`FinalizableObjectAnalyzer`, which are deferred for the same reason.

This is the entire thread-domain quartet's work; combined with every other originally-Tier-1-only
analyzer already being retyped, **Tier 1 retyping is now fully finished project-wide**.

## 2. Why this needed its own plan: one shared stack walk, four consumers

All four analyzers implement `IThreadStackScanParticipant`, so `AnalysisPipeline.RunSharedScans`
walks every thread's stack **exactly once** and fans each thread out to every participant, instead of
each analyzer independently calling `ClrThread.EnumerateStackTrace()`:

```csharp
IReadOnlyList<IThreadStackScanParticipant> threadStackScanParticipants =
    _analyzers.OfType<IThreadStackScanParticipant>().ToArray();
...
new ThreadStackScanDispatcher().Run(context.Runtime, context, threadStackScanParticipants, maxFramesPerThread, cancellationToken);
```

A naive retyping would break this: the SDK-side analyzer's `AnalyzeAsync` normally *pulls* data on
demand from `context.RuntimeThreads` (a live, independent walk), which would mean the pipeline now
walks every thread's stack **twice** — once via the shared dispatcher for the still-legacy quartet
members, once independently for the retyped one. That double-walk risk is why `ThreadAnalyzer`'s
retyping was deferred back when this was first scoped, and why the quartet needed a dedicated design
rather than the mechanical retype-and-adapt pattern every other analyzer used.

The fix, detailed in § 3: the **adapter**, not the inner SDK analyzer, implements
`IThreadStackScanParticipant`. `_analyzers.OfType<IThreadStackScanParticipant>()` doesn't care whether
an entry is a raw legacy analyzer or a `LegacyAnalyzerAdapter<T>` subclass — only the interface
matters — so an adapter can keep participating in the one shared walk exactly like it did
pre-retyping, no matter how many of the other three quartet members are still legacy.

## 3. The adapter push/pull bridge (design)

1. The **adapter** (not the SDK analyzer) implements `GetRequiredFrameCount`/`BeforeThreadStackScan`/
   `OnThreadStack`/`OnThreadStackScanCompleted`. During `OnThreadStack`, it translates each
   `ClrThread`/`ClrStackFrame` into the SDK's `RuntimeThreadRef`/`ThreadStackFrameRef` shapes (via a
   shared static `ThreadStackTranslator`, one translation used by both the live query and this
   accumulation path) into `Dictionary<uint OsThreadId, ...>` maps.
2. When the pipeline later calls the adapter's `AnalyzeAsync`, it builds the SDK's `AnalysisContext`
   with `RuntimeThreads` set to a `PrecomputedRuntimeThreadQuery` wrapping those accumulated maps,
   instead of a live `RuntimeThreadQuery`. The inner SDK analyzer calls
   `context.RuntimeThreads.EnumerateStackFrames(thread)` exactly as it always would — it has no idea
   whether the data came from a live walk or the precomputed cache.
3. Falls back to a live `RuntimeThreadQuery` when invoked outside the pipeline (tests, benchmarks —
   `OnThreadStackScanCompleted` never fired, so no precomputed data exists).
4. Generalizes beyond this quartet: any future analyzer needing thread stacks under the shared-walk
   optimization can reuse the same `PrecomputedRuntimeThreadQuery` + participant-adapter pattern.

`LegacyAnalyzerAdapter<TSdkAnalyzer>` gained one seam to support this: a
`protected virtual Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context)` method
(default: today's `LegacyAnalysisContextTranslator.Translate` call) that `AnalyzeAsync` now calls
polymorphically. Both quartet adapters override it to inject the precomputed query; every other
existing adapter is unaffected (default behavior unchanged).

## 4. Why `ThreadAnalyzer` and `HangAnalyzer` are deferred

Both read a live object's field value off the heap, not just structural ClrMD facts — genuine Tier 2:

- **`ThreadAnalyzer`** reports `ThreadExceptionSnapshot.ExceptionMessage`, sourced from
  `currentException.Message`. Reflecting `ClrException` (ilspycmd against the installed package,
  v4.0.732401 — not guessed) shows `.Message` reads a raw field offset off the live exception object
  (`Type.Module.DataReader.ReadPointer(Address + messageOffset)`, then resolves the string) — the same
  kind of field-value extraction as `AssemblyLoadContext._name` (`ModuleAnalyzer`) or
  `Task.m_stateFlags` (`HangAnalyzer`, below). `ClrException.Type` — the only other member
  `ThreadAnalyzer` touches — stays Tier 1: it's pure type resolution (`_object.Type`), no field read.
- **`HangAnalyzer`** calls `stateField.Read<int>(obj, interior: false)` to read a live `Task`'s
  `m_stateFlags` field directly. It also implements a second, unrelated shared-scan interface,
  `IParallelHeapIndexScanParticipant` (a parallel-worker heap-index scan, not the thread-stack one) —
  more machinery than the other three quartet members, independent of this doc's scope.

Both stay on `Core.Abstractions.IAnalyzer` until the `dump.object-fields` Tier-2 escape hatch exists,
alongside `ModuleAnalyzer`/`FinalizableObjectAnalyzer` (deferred for the same reason, in the main
retyping plan).

## 5. `LockGraphAnalyzer` — what shipped

- **`HeapSyncBlockRef` gained `HasHoldingThread`** (mirrors `SyncBlock.HoldingThreadAddress != 0`),
  distinct from `HoldingOsThreadId` being non-null — needed to reproduce
  `LockGraphDomainResult.LocksWithOwnerAddress`/`UnresolvedOwnerCount`'s distinction between "no
  owner at all" and "owner address present but didn't resolve to a live thread." Caught while writing
  the analyzer against the interface, before it shipped.
- **New `HeapSyncBlockQuery`** implements `IHeapSyncBlockQuery`, correlating lock ownership by OS
  thread ID (`HoldingOsThreadId`) against `RuntimeThreadRef.Thread.OsThreadId` — not a raw `ClrThread`
  address like the pre-retyping analyzer's `SyncBlock.HoldingThreadAddress`, which also avoids leaking
  a raw dump-local address through the SDK.
- **`RuntimeThreadRef` gained `LockCount`** (mirrors `ClrThread.LockCount`).
- **`ThreadStackTranslator`** extracted the `ClrThread`/`ClrStackFrame` → `RuntimeThreadRef`/
  `ThreadStackFrameRef` mapping out of `RuntimeThreadQuery` into a shared static helper, so there is
  exactly one translation used by both the live query and the adapter's `OnThreadStack` accumulation.
- **One accepted simplification**: the pre-retyping analyzer's `CaptureOwnerThreadFrames` did a
  second, independent, unbounded stack walk per deadlock candidate. The retyped version reuses the
  same `FrameScanDepth`-bounded frames the shared scan already captured instead — deadlock candidates
  are rare enough that returning fewer than 3 frames when fewer than 3 of the first 8 resolve is an
  acceptable, documented narrowing.
- **Gates**: `LockGraphAnalyzerRetypingCharacterizationTests` (self-attached process vs. ClrMD ground
  truth) and `LockGraphAnalyzerRealDumpTests` (`[DiscrepancyFact]`, standalone, foreground). A
  *pre-existing* real-concurrency suite (`LockGraphAnalyzerLiveHeapTests` — real threads, real `lock`
  statements, a real deadlock scenario) needed only a one-line update to call through the adapter and
  passed unchanged. `AnalysisPipelineTests.ExecuteAsync_ScansThreadStacksExactlyOnce_WhenRetypedAndLegacyParticipantsAreMixed`
  proves the shared-scan-runs-once property directly against a real `AnalysisPipeline` run.
- **Call sites**: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark`, `LockGraphAnalyzerBenchmark` (now targets
  `AnalyzerBenchmarkBase<LockGraphAnalyzerLegacyAdapter>` — calls `AnalyzeAsync` directly, not through
  the pipeline, so it exercises the live-query fallback path, not the precomputed one).

## 6. `ThreadStackClusterAnalyzer` — what shipped

Reused `LockGraphAnalyzerLegacyAdapter`'s shape almost verbatim.

- **`RuntimeThreadRef` gained `IsGc`, `IsFinalizer`, `IsThreadpoolWorker` (`TS_TPWorkerThread`), and
  `IsCompletionPortThread` (`TS_CompletionPortThread`)** — used both for per-cluster
  `ThreadpoolWorkerCount`/`GcCount`/`FinalizerCount` counts and to synthesize the
  `"<No managed frames> (...)"` signature for threads with no resolvable frames.
- **`ThreadStackFrameRef` gained two raw fields**: `FrameName` (raw `ClrStackFrame.FrameName`, always
  populated) and `RawMethodSignature` (raw `ClrMethod.Signature`, `null` when absent, no fallback).
  The existing `MethodDisplayName` field couldn't be reused here — `JitAnalyzer` depends on its
  synthesized `Type.Method` fallback when `Signature` is `null`, but cluster-signature identity needs
  the *raw* signature-or-frame-name chain with no synthesis. Kept the two concerns orthogonal rather
  than parameterizing `MethodDisplayName`'s fallback.
- **The pre-retyping analyzer's address-indirection machinery disappeared entirely**
  (`osThreadIdByAddress`, `ProjectSampleOsThreadIds`, `SampleThreadAddresses`) — it only ever existed
  to map a `ClrThread.Address` back to an `OSThreadId` for display, and `RuntimeThreadRef` already
  carries `OsThreadId` directly. `StackCluster` now accumulates `SampleOsThreadIds` straight from each
  thread, making the retyped `Analyze` and its export block simpler than the original, not just
  re-typed.
- **Every pure-string/pure-logic static member kept its exact pre-retyping signature**
  (`StackCluster`, `BuildClusterTree`, `ConvertTrieNode`, `ClassifyFrameworkPattern`,
  `BuildTopFrameHotspots`) — none of them touch ClrMD types, so the pre-existing
  `ThreadStackClusterAnalyzerOptionsTests` (23 tests) passed unchanged with zero edits.
- Progress reporting (`ObjectScanCounter` ticking during the stack walk) was dropped, matching the
  precedent `LockGraphAnalyzer` and every other retyped Tier-1 analyzer already set.
- **Gates**: `ThreadStackClusterAnalyzerRetypingCharacterizationTests` and
  `ThreadStackClusterAnalyzerRealDumpTests` (`[DiscrepancyFact]`, standalone, foreground, ~21s against
  the reference 3.5GB dump). The pipeline mixed-participant test from § 5 was extended to include
  both retyped quartet adapters together, proving neither one broke the other's participation. Full
  non-real-dump suite (1241 tests) passes; `LockGraphAnalyzerRealDumpTests` re-run standalone
  afterward to confirm the shared `RuntimeThreadRef`/`ThreadStackFrameRef` field additions caused no
  regression there.
- **Call sites**: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark`, `ThreadStackClusterAnalyzerBenchmark` (retargeted at
  `AnalyzerBenchmarkBase<ThreadStackClusterAnalyzerLegacyAdapter>`, same live-query-fallback caveat).

## 7. Gates used throughout

Same discipline as every other Tier-1 retyping batch: a characterization test against a self-attached
live process (cross-checked against direct `ClrThread`/`ClrStackFrame`/`SyncBlock` ground truth, since
none of this data can be hand-crafted via reflection injection), one `[DiscrepancyFact]` real-dump
test per analyzer run standalone in the foreground, full non-real-dump suite green before and after
each batch — plus, specific to this quartet, a pipeline-level test proving the shared-scan-runs-once
property directly rather than assuming it from the design.

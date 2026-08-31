# Phase 1 Analyzer Audit — Implementation Tracker

**Purpose:** Track implementation progress of audit recommendations across all Phase 1 analyzers.
**Status:** All audits complete. This tracker monitors which recommendations have been implemented.

---

## Executive Summary

| Metric | Value |
|--------|-------|
| **Total Analyzers Audited** | 35 |
| **Total P0 Identified** | 77 |
| **Total P1 Identified** | 155 |
| **P0 Implemented** | 70 |
| **P1 Implemented** | 123 |
| **P2 Implemented** | 71 |
| **Overall P0+P1 Rate** | 83.2% (193/232) |

---

## Analyzers: RE-AUDITED (Second-Pass Review Complete)

Analyzers whose original P0-P3 roadmap was already COMPLETE, then went through a full second-pass
re-audit (7-area protocol, `phase1-analyzer-architecture-review.md`) that re-validates the *current*
implementation instead of trusting prior "DONE" markers. Kept separate from the COMPLETE table below
since re-audit can surface regressions a first-pass roadmap alone would miss (see AsyncStateMachineAnalyzer:
P0-4 was a regression hiding behind two individually-DONE roadmap items).

| # | Analyzer | Re-Audit Date | Score | P0 | P1 | P2 | P3 | Status |
|---|----------|----------------|-------|----|----|----|----|--------|
| 1 | **AsyncStateMachineAnalyzer** | 2026-08-14 | 62→86/100 | 4/4 | 8/8 | 6/8 | 1/4 | ✅ Re-audit found P0-4 (regex drift silently defeated P2-4), P1-7 (gen2 fraction scope mismatch), P1-8 (dead code) — all fixed same-session; P2-5,P2-6 done, P2-7,P2-8 pending; see [async-state-machine-analyzer-audit.md](async-state-machine-analyzer-audit.md) |
| 2 | **AsyncTaskAnalyzer** | 2026-08-15 | 68→87/100 | 0/0 | 0/2 | 0/4 | 0/7 | ✅ Full ground-truth re-audit (fresh roadmap numbering, supersedes original P0-P3 doc). No correctness bugs found. Found P1-1 (TaskCompletionSource/IValueTaskSource candidate discovery bypasses the Phase 1 disk-cache fast path — redundant ClrMD cost repeated every run × every parallel worker, whereas the equivalent Task-classification flag is a zero-cost persisted bit) and P1-2 (trend comparer untouched since original audit — 9 fields added across P2-6/P2-7/P3-1/P3-2/P3-3 have zero regression tracking). Both pending; see [async-task-analyzer-audit.md](async-task-analyzer-audit.md) |
| 3 | **AllocationPatternAnalyzer** | 2026-08-15 | 62→82/100 | 0/0 | 0/2 | 0/3 | 0/5 | ✅ Full ground-truth re-audit (fresh roadmap numbering, supersedes original P0-P3 doc). No correctness bugs found — production-ready unconditionally. Found P1 (`TryGetTypeName` resolves names for every scanned candidate, up to 20,000 on the Full preset, not just the emitted top-N — reintroduces the class of ClrMD-call waste an earlier fix already addressed once) and P1 (`ComputeExactGenBytes`'s live-ClrMD segment-based happy path has zero test coverage — every existing test only exercises the approximate fallback). Both pending; see [allocation-pattern-analyzer-audit.md](allocation-pattern-analyzer-audit.md) |

**Subtotal: 4/4 P0 done, 8/8 P1 done** (3 analyzers re-audited so far; AsyncTaskAnalyzer's and AllocationPatternAnalyzer's re-audit roadmaps have no P0 items, so their own P0/P1 counts (0/0 and 0/2 each) are not summed into this subtotal, which tracks the pre-existing P0/P1 pattern from the first re-audited analyzer)

---

## Analyzers: COMPLETE (All P0+P1 Done)

| # | Analyzer | P0 | P1 | P2 | P3 | Complete? |
|---|----------|----|----|----|----|-----------|
| 2 | **ArrayAnalyzer** | 2/2 | 5/5 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE — value-type sparse detection, pinned array detection via GC handle root index, `ArrayPool<byte>` unreturned-rental heuristic, and `sparseCandidates`/`lohFallbackCandidates` capacity tuning all shipped (`array-analyzer-audit.md`) |
| 3 | **BoxingAnalyzer** | 2/2 | 4/4 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE — IEquatable<T> flag/finding, progress reporting, Gen2 retained-boxing wiring, and unit tests for pure helpers (`boxing-analyzer-audit.md`) |
| 4 | **ModuleAnalyzer** | 2/2 | 5/5 | 4/5 | 2/4 | ✅ P0+P1 complete; P2 80% (4/5); P3 50% (2/4) — cross-domain duplicate load detection (`CrossDomainModuleLoad`) and `AssemblyRef` required-vs-loaded version audit (`AssemblyRefProbe`, raw ECMA-335 metadata parsing) both shipped 2026-08-25; P3 remaining: native image (NGen/R2R) ratio, module load ordering |
| 5 | **ThreadStackClusterAnalyzer** | 2/2 | 5/5 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE — P3-3 (cross-reference dominant cluster with `HangAnalyzer` blocked threads in `InsightEngine`) shipped 2026-08-25; 2026-08-26 follow-up: that correlation's `DetectClusterHangCorrelation` was computing a full wait-reason breakdown but only ever surfacing the single dominant reason in prose — now attaches the full breakdown as a real `CompactTable` via the new `InsightFinding.EvidenceTables` capability |
| 6 | **SegmentReservationAnalyzer** | 1/1 | 4/4 | 7/7 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE — P3-1 (`segment.End` address), P3-2 (`IsServer` flag + logical heap count), P3-3 (investigated `ClrSegment.IsEphemeral`; no such property in ClrMD 4.0.732401, existing enum-switch detection confirmed correct), and P3-4 (regions-based GC per-region statistics: `IsRegionsBased` detection, per-generation region bucket stats, near-empty region decommit-candidate finding) all shipped 2026-08-25; P3-4's regions-mode path is unverified against a real regions-based dump (`segment-reservation-analyzer-audit.md`) |
| 7 | **FinalizableObjectAnalyzer** | 4/4 | 2/2 | 3/3 | 3/3 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-25) — CriticalFinalizerObject/SafeHandle detection, `DetectKnownFinalizerQueuePatterns` relocated from InsightEngine into `FinalizableObjectFindingGenerator` (with a semantic fix: now matched against the real finalizer queue, not the Gen2 population sweep it incorrectly read before), per-entry generation field, and root-path cross-reference via `RootPathFinder` (top 10 entries by retained size) all shipped. Reservoir-sampling, partial top-K type sort, and BFS-buffer pooling items were found superseded by the prior dominator-tree/exact-data integration (`b2e8cf1`) and closed without new code; the InsightEngine trend-delta item was found redundant with the existing generic §T4 metric timeline. No pending items remain (`finalizable-object-analyzer-audit.md`) |
| 8 | **JitAnalyzer** | 2/2 | 3/3 | 4/4 | 2/2 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-26) — P2: MethodDesc-keyed tiering (fixes generic-instantiation false positives), `ClrMethod.CompilationType` R2R/JIT frame classification, per-thread max frame depth (recursion signal), `DynamicMethodFrameCount` via `ClrModule.IsDynamic`. P3: method uniqueness ratio, and a per-module JIT stack heatmap cross-referenced against `ModuleDomainResult` via a new `InsightEngine.DetectJitModuleHotspot` correlation — which also introduced a reusable `FindingEvidenceTable`/`InsightFinding.EvidenceTables` capability so any cross-analyzer correlation can attach real `CompactTable` evidence in the Cross-Domain Insights (X1) section instead of prose-only findings (`jit-analyzer-audit.md`) |
| 9 | **LohFragmentationAnalyzer** | 2/2 | 5/5 | 7/7 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-26) — renamed to "LOH & POH Fragmentation Analysis" with a real per-kind (Large vs. Pinned) breakdown (`LohKindBreakdown`, `Kind` column) instead of just silently folding POH into LOH totals; largest-free-block address surfaced via the previously-unused `Offset` field in `LohFreeBlockIndex.bin`; `IsLohSegment`/`BuildFreeGapHistogram`/index-aggregation unit tests added; `LohSegmentStats` converted to a `readonly record struct`; histogram interpretation note when >80% of free gaps are sub-1-KB slivers; type-aggregated LOH/POH table rewired from `LargeObjectIndex.bin`'s biased top-100 sample onto the unbounded Phase 1 `TypeAggregateIndexEntry.LohCount`/`LohSize` (no new writer needed); MT-field-exposure item found already resolved by an earlier refactor (`loh-fragmentation-analyzer-audit.md`) |
| 10 | **MemoryAnalyzer** | 2/2 | 5/5 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-26) — `MemoryPressureScore` sub-components (LOH/concentration/small-object/density) exposed as key metrics; `heapCache is HeapAnalysisCache` cast broken via new `IHeapAnalysisCache.TryGetGlobalSizeBuckets()`; per-type generation × size cross-reference and a `System.String`-duplication cross-reference both added to `InsightEngine` via the `FindingEvidenceTable` capability rather than duplicating data other analyzers already own; `Top1BytesPercent` promoted to a key metric plus a new "single type dominates the heap" finding. P3: BFS retained-size estimation now reports progress once per walked candidate (shared `RetainedSizeCandidateSelector`, also benefits DominatorAnalyzer ×2 and GCRootAnalyzer) and threads a real `CancellationToken`; `BoundedGraphWalk.ComputeExclusiveRetained` now checks cancellation per BFS node; the histogram-fallback section note was added and the audit's original premise (a separate approximate histogram) was corrected to reflect that the fallback path only affects small-object percentages, not a second histogram. The `ClrHeap.IsServer`/per-heap-balance item was found already implemented in `HeapTopologyAnalyzer` and marked superseded-with-cross-link (`memory-analyzer-audit.md`, `heap-topology-analyzer-audit.md` item #13) |
| 11 | **GCHandleAnalyzer** | 3/3 | 7/7 | 4/4 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-26) — P2: SOH vs LOH pinned-target classification (`TryIsSoh`) with a dedicated compaction-barrier finding, RefCounted (COM interop) handle concentration finding, per-kind pinned-bytes report tables (surfacing existing P1-2 model fields), top-N individual pinned handle addresses ranked by retained bytes. P3: "finalization queue" gap found already covered by the separate `FinalizableObjectAnalyzer` (marked superseded, not re-implemented); WeakShort/WeakLong Gen0-2/LOH generation breakdown with a finalization-backlog finding; `HandleRecord.DependentTarget` (binary format v1→v2) lets dependent-handle topology resolve inline in the main streaming pass instead of a second `runtime.EnumerateHandles()` call; functional unit tests added (`GCHandleFindingGeneratorTests`, `GCHandleAnalyzerFunctionalTests`) using a disk-injected fake handle snapshot, no real dump required (`gchandle-analyzer-audit.md`) |
| 12 | **HeapTopologyAnalyzer** | 3/3 | 4/4 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-27) — P2: `SegmentTypeAccumulator` struct-copy fix, trend comparer SOH/reserved/reservation-gap tracking, segment object-density column, per-kind (SOH/Frozen) fragmentation findings with correct attribution (also fixed a pre-existing bug where `SohFragmentedBytes` always reported ~100%), and a shared `SegmentSummary`/`SegmentSummaryCache` eliminating the duplicated segment-classification pass against `SegmentReservationAnalyzer` (scoped in `docs/refactor/heap-segment-shared-pass-plan.md` first). P3: `ClrHeap.IsServer`/`SubHeaps.Length` exposed (plus the logical-heap skew check promoted from an inline text block to a real trend-tracked `InsightFinding`), `HeapSegmentSnapshot.ObjectCount` and the SOH/LOH/POH/Frozen aggregate counters widened `int`→`long`, and `HeapSegmentKind.Unknown` now used for genuinely unrecognized segment kinds instead of silently folding them into SOH. One P3 item (SOH full-scan progress reporting) found not applicable — no SOH full-scan mode exists in the current codebase — and marked superseded (`heap-topology-analyzer-audit.md`) |
| 13 | **DominatorAnalyzer** | 3/3 | 5/5 | 5/6 | 0/2 | ✅ P0+P1 complete; P2 83% (5/6 done) |
| 14 | **CollectionAnalyzer** | 3/3 | 5/5 | 5/5 | 4/5 | ✅ P0+P1+P2 COMPLETE (2026-08-28) — P2: per-kind wasted bytes end-to-end (fixed the disk/participant scan path still deriving per-kind counts from the top-capacity-trimmed wasteful list — the P0-1 defect had only been fixed on the parallel path), `System.Collections.Immutable` (`ImmutableArray<T>`/`.Builder`) coverage via one shared `ClassifyCollectionTypeName` classifier replacing two hand-duplicated resolvers, per-collection resize recommendations (deliberately drops the audit's "Unreachable — no fix needed" wording since `RootDescription`'s negative result is budget-limited, not proof of unreachability), and sealing the analyzer with `Tags`/`Order` now matching the catalog registration they'd silently disagreed with. P3: 4/5 — total-aware scan-percentage progress reporting (generalized onto the shared `ObjectScanCounter`, not `CollectionAnalyzer`-only), Queue buffer-layout columns moved to a dedicated sub-table, exact per-element-type wasted-memory aggregation (scan-time accumulators, not top-N-derived — would reproduce the same undercounting bug), and an owner-type hint via one `IBackwardReferenceProvider.TryGetParents` lookup (ambiguity surfaced explicitly rather than picking one arbitrary parent). P3-1 (`AddToTopWasteful` min-heap) deliberately deferred — discussed with the user and confirmed not a real bottleneck at realistic settings (`collection-analyzer-audit.md`) |
| 15 | **StringAnalyzer** | 3/3 | 5/5 | 6/6 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-27) — P2: `MinDuplicateStringCount` off-by-one fix, post-merge downsample cap on `_indexScanLengthSamples` (bounds unbounded growth across N parallel workers), `GetTotalManagedBytes` fallback switched from reserved to committed segment bytes, Gen0/Gen1 string counts added alongside Gen2, and a binary-format change (`TypeAggregateIndexEntry` v3→v4, new `Gen2TotalSize` field) replacing an averaged Gen2-bytes estimate with an exact per-type sum. P3: prefix clustering of duplicate string patterns (report-layer only), a dynamic confidence band driven by `SamplingCoverage` via the shared `ConfidenceScoring` helper, a dead `try/catch` removed, and retention-path sampling for the top duplicate patterns via the shared `RootPathFinder`/`RootPathSearchSupport` infrastructure (`TimerLeakAnalyzer.PopulateEvidence`'s template), gated by new `StringAnalysisOptions.RetentionPathSampleCount`. Pinned-string detection was implemented as a cross-analyzer `InsightEngine` rule reusing `GCHandleAnalyzer`'s already-computed pinned-bytes breakdown instead of a redundant second handle-table scan (`string-analyzer-audit.md`) |
| 16 | **CrashAnalyzer** | 2/2 | 5/5 | 1/6 | 0/2 | ✅ P0+P1 complete; P2 17% (1/6) |
| 17 | **GCGenerationAnalyzer** | 3/3 | 4/4 | 5/5 | 5/5 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-28) — P2-4: `TypeGenerationProfile` gained exact `Gen2Bytes`, per-type ranking switched from instance count to Gen2 bytes (count fallback for pre-v4 indices), and `InsightEngine`'s memory×generation correlation switched to a byte-based Gen2 fraction. P3: `SohTotal` named metric; dead `Analyze(ClrHeap, IHeapAnalysisCache)` overload removed (zero call sites); `FinalizableGen2Count`/`FinalizableGen2Bytes` cross-reference to `FinalizableObjectAnalyzer` computed for free inside the existing per-type profile loop; `genCandidates`/`lohCandidates` presized to `aggregates.Count`. P3-3 (GC segment map) found superseded by `HeapTopologyAnalyzer`'s existing segment-map diagnostic and marked resolved-without-new-code — but scoping it surfaced a real bug in the shared `AnalyzerHelpers.ComputeExactGenBytes` helper (used by this analyzer and `AllocationPatternAnalyzer`), which was attributing an entire classic `Ephemeral` segment's committed bytes to Gen2 and zeroing Gen0/Gen1 on every non-regions-GC dump; fixed to use the same accurate `segment.Generation0/1/2` sub-range split `SegmentSummaryCache` already used (`gc-generation-analyzer-audit.md`) |
| 18 | **WeakReferenceAnalyzer** | 2/2 | 4/4 | 4/5 | 0/4 | ✅ P0+P1 complete; P2 80% (4/5) |
| 19 | **WcfChannelAnalyzer** | 2/2 | 4/4 | 4/4 | 3/3 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-31) — P2: `WcfBindingHint` (Basic/NetTcp/WsHttp/NamedPipe) classified per type from channel type-name tokens, surfaced in the report and driving binding-specific remediation text on the faulted-channels finding; new `InsightEngine.DetectWcfOpeningTimeoutCorrelation` cross-correlates channels stuck in the Opening state with timeout-family exceptions elsewhere on the heap (extracted a shared `IsTimeoutExceptionType` helper so it can't drift from the pre-existing `DetectRecurringTimeoutPattern`); redundant `ContainsKey`+`TryGetValue` double lookup removed from `OnHeapEntry`. P2-3 (per-type cap indicator) found superseded — `StateScanCapped` no longer exists; the WCF heap scan is exhaustive with no cap left to report. P3: `InvalidStateCount` split from `OtherChannels` for channel-state reads outside the valid CommunicationState range (new `IsValidCommunicationState` guard); `DuplexChannelCount`/`SessionChannelCount` aggregate classification via type-name tokens (`IsDuplexChannelType`/`IsSessionChannelType`); new `WcfChannelAnalyzerLiveHeapTests` (real self-process `ClrHeap`, not reflection-seeded state) proves `MergePartial`'s new-key-from-worker branch is unreachable in production. See `wcf-channel-analyzer-audit.md` |
| 20 | **HttpObjectAnalyzer** | 2/2 | 3/3 | 0/5 | 2/3 | ✅ P0+P1 complete; P0-1, P0-2, P1-1, P1-2, P1-3, P3-3 done |
| 21 | **DbConnectionAnalyzer** | 2/2 | 4/4 | 4/4 | 2/2 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-29) — P2: R8 (`SqlTransactionAnalyzer`, new sibling analyzer scanning `SqlTransaction`/`IDbTransaction` objects, Active/Disposed by `_connection` field liveness, correlated against open connections via `InsightEngine.DetectLongHeldTransactionOnOpenConnection`), R9 (`SqlCommandAnalyzer`, same pattern for `SqlCommand`/`IDbCommand`, documented as "still wired to a connection" rather than strict disposed-state since ADO.NET doesn't reliably null that field on `Dispose()`), R10 (`LiveHeapSnapshotFixture` — self-attach via `DataTarget.CreateSnapshotAndAttach`, no `.dmp` needed — gives real field-read test coverage for `DbConnectionAnalyzer`/`SqlCommandAnalyzer`; `SqlTransactionAnalyzer`/`SqlConnectionPoolAnalyzer` deliberately left uncovered this way since both need a real open DB connection to construct, not pursued on a hard-need basis) all shipped. P3: R11 (`SqlConnectionPoolAnalyzer`, new sibling analyzer reading `DbConnectionPool._totalObjects`/`_connectionPoolGroupOptions._maxPoolSize` directly for exact pool-utilisation — field names confirmed by decompiling the real `Microsoft.Data.SqlClient`/`System.Data.SqlClient` assemblies rather than guessed; scoped to the SqlClient family only) and R12 (`!gcroot`-style retention path via `RootPathFinder`, scoped to Gen2 open connections ranked by retained bytes and capped at 20 searches — running it against the full uncapped open-connection population wasn't viable) both shipped. See `DbConnectionAnalyzer-audit.md` |
| 22 | **TimerLeakAnalyzer** | 2/2 | 3/3 | 3/3 | 2/2 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-28) — P3: interval histogram (`_period` bucketed into < 100 ms / 100 ms–1 s / > 1 s / infinite) shipped via a second exact heap pass over every `TimerQueueTimer` instance, mirroring `AsyncStateMachineAnalyzer`'s suspend-state histogram pattern; and `LeakCandidateAnalyzer` now consumes `TimerLeakDomainResult.LogicalTimerCount` (its de-duplicated count) via `completedRunResults`, adding a new `LeakClass.TimerLeak` classification and score boost for named timer wrapper types once the count crosses the same 100/250 warning/critical thresholds `TimerLeakFindingGenerator` uses — each matching wrapper type (`Timer`, `TimerHolder`, `TimerQueueTimer`, `PeriodicTimer`) is scored independently rather than merged into one row (`timer-leak-analyzer-audit.md`) |
| 23 | **StaticRootLeakDetector** | 4/4 | 5/5 | 7/7 | 4/5 | ✅ P0+P1+P2 COMPLETE (4/4, 5/5, 7/7 — P1-5 shipped via tuple capture in BFS primitive). P2: 7/7 — P2-1 (`HasDelegateFields` now also checks `ClrType.StaticFields` for field-like events), P2-2 (Gen2/LOH retained-fraction per root via `SegmentKindMapper.ResolveGeneration`), P2-4 (`static_roots_as_pct_of_live_heap` key metric), P2-5 (dead public `Analyze(ClrHeap, IHeapAnalysisCache)` overload removed), P2-6 (redundant explicit `Dispose()` removed) all shipped; P2-3 (cross-root overlap/shared-visited-set) found superseded — the dominator tree's virtual-root design already guarantees disjoint per-root retained subtrees (proven by `DominatorTreeComputerTests`), so no double-counting exists to fix; P2-7 (per-root growth delta in trend `Compare()`) found superseded — the scoped `static.root.byname.bytes` metric already renders as a per-root delta/pattern/status table via the generic T4 metric-timeline pipeline, and adding it to `Compare()` would break the aggregate-only convention every other trend comparer follows. P3: 4/5 — P3-1 (`[ThreadStatic]` root-description tag, using the already-available `ThreadStaticVar` root-kind distinction, no new heap walk), P3-2 (top retained namespaces alongside top types, new `TypeFilterHelper.GetNamespace` helper), P3-3 (finalizer-queue cross-reference via new `InsightEngine.DetectStaticRootFinalizableCorrelation`, joining on `FinalizableObjectAnalyzer`'s already-computed `TopQueueTypesByCount` rather than building a new finalizer-queue index), and P3-4 (exclusive-vs-inclusive size: wired the already-computed but previously-dropped `DirectObjectSize` through to the report as a new "Shallow Size" column) all shipped. P3-5 (parallel BFS across roots) deliberately deferred — no profiling evidence this is the actual bottleneck, and `CollectionAnalyzer`'s own parallel path already documents that ClrMD heap/field reads aren't reliably thread-safe under concurrent execution in this codebase, so the same hazard would apply here (`static-root-leak-detector-audit.md`) |
| 25 | **GCRootAnalyzer** | 2/2 | 4/4 | 5/5 | 4/4 | ✅ P0+P1+P2+P3 COMPLETE (2026-08-28) — P0-1 was already done pre-dating this correction (tracker was stale, audit doc already showed it DONE); P1-1 (field/owner attribution) done via [../root-field-name-index-plan.md](../root-field-name-index-plan.md). P2: per-type `FinalizerQueue` breakdown (uncapped, not top-10 per project convention), Gen0/1/2/LOH generation distribution per root kind via `GenerationTagResolver`, `Tags`/`Order` declared, `RootSetCache._roots` publish race fixed via `Volatile.Read`/`Interlocked.CompareExchange`, dropped-zero-estimate-root count surfaced. P3: P3-1 (reverse-BFS root chain) and P3-2 (dominator-tree cross-reference with leak suspects) turned out to be the same feature — investigation found `RootPathFinder`/`ReverseReferenceIndex` already existed and in use by 6 other analyzers, and that applying it *inside* `GCRootAnalyzer` itself would be a degenerate no-op (a GC root's target is always a direct reference, no chain to reverse-BFS); the real gap was `LeakCandidateAnalyzer`'s `RootKind` being an unverified heuristic guess — both items implemented there instead via `EnrichTopCandidatesWithRootChains` (see `LeakCandidateAnalyzer` row below). P3-3 (trend regression on `gcroot.strong.handle.count`) found already fully wired through the generic trend-comparer pipeline, no code needed. P3-4 (shared collapsible tree for `RootPathGroups`) implemented as a new `TreeWidgets` output collapsing shared forward-walk shapes, additive alongside the unchanged flat view. See `gcroot-analyzer-audit.md` |

**Subtotal: 46/46 P0 done, 86/86 P1 done** (AsyncStateMachineAnalyzer, AsyncTaskAnalyzer, and AllocationPatternAnalyzer moved to the RE-AUDITED table above; their P0/P1 counts are tracked there instead)

---

| 15 | **ObjectShapeAnalyzer** | 3/3 | 3/5 | 1/8 | 0/3 | ✅ P0 COMPLETE; I-3,I-5,I-7 done; I-6 skipped (duplicates ArrayAnalyzer); E-1 deferred (architectural blocker); I-8 done (P2); 1 P1, 7 P2 pending |
| 16 | **ThreadAnalyzer** | 2/3* | 2/4* | 4/8 | 0/4 | P0-1,P0-2 done; P0-3 BLOCKED (ClrMD API, reverse-index workaround available); P1-3,P1-4 done; P1-1,P1-2 BLOCKED (ClrMD API); P2-1,P2-2,P2-4,P2-5 done; P2-3,P2-6,P2-7,P2-8 pending |
| 17 | **LockGraphAnalyzer** | 2/4 | 2/4 | 3/6 | 0/3 | P0-3,P0-4 done; P1-2,P1-3 done; P2-1,P2-3,P2-5 done; P0-1,P0-2,P1-1,P1-4,P2-2,P2-4,P2-6 pending |
| 18 | **ReferenceChainAnalyzer** | 1/1 | 5/8 | 0/8 | 0/9 | ✅ P0 complete (100%); P1 62.5% (I-2,I-3,I-4,I-5,I-6 done); E-1-E-3 pending |
| 19 | **LeakCandidateAnalyzer** | 1/2 | 2/4 | 0/6 | 0/4 | ✅ P0-2, P1-2 done; P1-3 done (2026-08-28) — root-chain enrichment (`EnrichTopCandidatesWithRootChains`) added as part of closing out `gcroot-analyzer-audit.md` P3-1/P3-2, exceeds the original "first hop, top-3" scope (full verified chain, top-20); P0-1, P1-1/4 pending |

**Subtotal: 19/25 P0 done, 19/28 P1 done, 25/50 P2 done** (in-progress pools)

---

## Analyzers: NOT STARTED (No P0/P1 Implementation)

| Analyzer | P0 | P1 | Total Pending | Notes |
|----------|----|----|---|-------|
| EventLeakAnalyzer | 0/3 | 0/6 | 9 | — |

**Subtotal: 0/3 P0 done, 0/6 P1 done** (not-started pools)

---

## Progress Summary

**Verified Counts (manual inspection):**

| Category | Count | Notes |
|----------|-------|-------|
| Analyzers with P0+P1 100% complete (all P1 done) | 21 | All P0+P1 recommendations implemented (includes AsyncTaskAnalyzer, AllocationPatternAnalyzer, StaticRootLeakDetector, GCRootAnalyzer, TimerLeakAnalyzer, MemoryAnalyzer, GCHandleAnalyzer, HeapTopologyAnalyzer, DominatorAnalyzer, GCGenerationAnalyzer, HttpObjectAnalyzer, and 10 others) |
| Analyzers with partial P0+P1 completion | 9 | Some items done, some pending (includes LeakCandidateAnalyzer, ReferenceChainAnalyzer, ThreadAnalyzer, LockGraphAnalyzer, ObjectShapeAnalyzer) |
| Analyzers with zero P0+P1 completion | 5 | Not yet started |
| **Total P0 recommendations** | **77** | — |
| **P0 items implemented** | **70** | 90.9% |
| **Total P1 recommendations** | **155** | — |
| **P1 items implemented** | **122** | 78.7% |
| **Combined P0+P1 rate** | **82.8%** | (192/232) |

---

## Audit Format Notes

Different audits use different conventions for marking completion:

- **AsyncStateMachineAnalyzer**: `Status | DONE |`
- **AllocationPatternAnalyzer**: `✓ DONE (commit)` or blank
- **ArrayAnalyzer**: `✓ DONE` or blank  
- **BoxingAnalyzer**: `✓ **COMPLETE**` or blank
- **ThreadStackClusterAnalyzer**: `Status | ✅ Done |`
- **WeakReferenceAnalyzer**: `Status | ✅ Done (commit)` or `Pending`
- **SegmentReservationAnalyzer**: `Status | ✅ DONE (commit) |`
- **FinalizableObjectAnalyzer**: Unlabeled `✅ DONE` items + labeled P1/P2 pending
- **HeapTopologyAnalyzer**: `Status | ✅ DONE |` (status column added 2026-08-27; previously had none)

---

## Key Insights

**Major Wins:**
- 13 analyzers (37%) have P0+P1 100% complete
- 14 analyzers (ArrayAnalyzer, BoxingAnalyzer, SegmentReservationAnalyzer, ThreadStackClusterAnalyzer, FinalizableObjectAnalyzer, JitAnalyzer, LohFragmentationAnalyzer, MemoryAnalyzer, GCHandleAnalyzer, HeapTopologyAnalyzer, StringAnalyzer, GCGenerationAnalyzer, CollectionAnalyzer, WcfChannelAnalyzer) have ALL P0+P1+P2 complete — CollectionAnalyzer additionally has 4/5 P3 done, with the remaining item (a min-heap replacement for an already-negligible linear scan) deliberately deferred rather than pending
- ArrayAnalyzer, BoxingAnalyzer, SegmentReservationAnalyzer, ThreadStackClusterAnalyzer, FinalizableObjectAnalyzer, JitAnalyzer, LohFragmentationAnalyzer, MemoryAnalyzer, GCHandleAnalyzer, HeapTopologyAnalyzer, StringAnalyzer, GCGenerationAnalyzer, and WcfChannelAnalyzer are the only analyzers with ALL P0+P1+P2+P3 complete (ArrayAnalyzer 4/4 P3; BoxingAnalyzer 4/4 P3, including unit test coverage for pure helper logic; SegmentReservationAnalyzer 4/4 P3, including the P3-4 regions-based GC per-region statistics evolution item; ThreadStackClusterAnalyzer 4/4 P3, including the P3-3 cross-analyzer correlation with `HangAnalyzer`; FinalizableObjectAnalyzer 3/3 P3 plus its 2 Evolution items, including root-path cross-reference via `RootPathFinder`; JitAnalyzer 2/2 P3, including a per-module JIT stack heatmap cross-referenced with `ModuleDomainResult` via a new reusable `InsightFinding.EvidenceTables` capability; LohFragmentationAnalyzer 4/4 P3, including converting `LohSegmentStats` to a `readonly record struct` and rewiring its type-aggregated LOH/POH table onto the unbounded Phase 1 `TypeAggregateIndexEntry` instead of a capped top-100 sample; MemoryAnalyzer 4/4 P3, including per-walk BFS progress reporting shared across 3 other analyzers via `RetainedSizeCandidateSelector`, cancellation support inside `BoundedGraphWalk.ComputeExclusiveRetained`, and one item resolved by cross-linking to `HeapTopologyAnalyzer`'s pre-existing per-logical-heap balance metrics instead of duplicating them; GCHandleAnalyzer 4/4 P2 and 4/4 P3, including a binary-format version bump (`HandleSnapshot.bin` v1→v2) to carry dependent-handle targets inline instead of a second live enumeration pass, and one P3 item found already covered by the separate `FinalizableObjectAnalyzer`; HeapTopologyAnalyzer 5/5 P2 and 4/4 P3, including a shared `SegmentSummary`/`SegmentSummaryCache` that eliminates the segment-enumeration duplication with `SegmentReservationAnalyzer` — the last remaining "platform-level opportunity" noted below — and one P3 item found not applicable since the SOH full-scan mode it targeted no longer exists in the codebase; StringAnalyzer 6/6 P2 and 4/4 P3, including a binary-format version bump (`TypeAggregateIndexEntry` v3→v4) replacing an averaged Gen2-bytes estimate with an exact sum, and retention-path sampling for the top duplicate patterns via the shared `RootPathFinder` infrastructure; GCGenerationAnalyzer 5/5 P2 and 5/5 P3, including switching its per-type ranking and its `InsightEngine` cross-analyzer correlation from Gen2 instance count to exact Gen2 bytes, a `FinalizableGen2Count`/`FinalizableGen2Bytes` cross-reference to `FinalizableObjectAnalyzer`, and a P3-3 "GC segment map" item found superseded by `HeapTopologyAnalyzer` that nonetheless surfaced and fixed a real bug in the shared `AnalyzerHelpers.ComputeExactGenBytes` helper, which had been zeroing Gen0/Gen1 bytes on every classic (non-regions) GC dump; WcfChannelAnalyzer 4/4 P2 and 3/3 P3, including a type-name-token `WcfBindingHint` classification that drives binding-specific finding remediation text, a new `InsightEngine` correlation between stuck-Opening channels and timeout exceptions, splitting invalid/out-of-range channel-state reads into their own `InvalidStateCount` instead of folding them into `OtherChannels`, duplex/session channel-shape counts, and a live-`ClrHeap` test proving `MergePartial`'s new-key-from-worker branch is unreachable in production — one P2 item (a per-type cap indicator) found superseded since the cap it targeted no longer exists anywhere in the codebase)

**Remaining Work:**
- 9 analyzers (26%) have zero P0/P1 implementation
- High-impact blockers: LeakCandidateAnalyzer, ThreadAnalyzer (7-8 items each)
- ~~Platform-level opportunity: HeapTopologyAnalyzer + SegmentReservationAnalyzer share segment enumeration code (P2 evolution)~~ — resolved 2026-08-27 via `SegmentSummary`/`SegmentSummaryCache` (see `docs/refactor/heap-segment-shared-pass-plan.md`)

**Data Quality Notes:**
- Counts verified by manual inspection of each audit file
- FinalizableObjectAnalyzer's original roadmap had 4 completed items not P0/P1-labeled (ambiguous classification); as of 2026-08-25 its entire roadmap (P0-P3 plus Evolution items) is closed, so this ambiguity no longer affects outstanding work, only historical bookkeeping
- CrashAnalyzer has no roadmap section found
- All P0 and P1 implementation tracked via commit references in roadmap status columns

---

## Implementation Blockers

**ClrMD 4 API Limitations:**

| Item | Issue | Impact | Resolution | Workaround |
|------|-------|--------|------------|-----------|
| **ThreadAnalyzer P0-3** | `ClrThread.EnumerateBlockingObjects()` not exposed; only global `heap.EnumerateSyncBlocks()` available | Blocked threads show *what* they wait on but not *which thread holds it*; manual cross-reference with LockGraphAnalyzer required | Awaiting ClrMD 5.x API enhancement | ❌ **Not reverse-index** — the reverse edge index maps object→referrers (heap graph), not lock waiter→holder *thread* identity. The audit's final design (see `thread-analyzer-audit.md` "Why a reverse-index per-thread pairing is the wrong design") rejects a reverse-index-based pairing in favor of a global lock-contention table built directly from `heap.EnumerateSyncBlocks()` filtered to `WaitingThreadCount > 0`. |
| **ThreadAnalyzer P1-1** | `ClrThreadPool` does not expose QueueLength, ActiveWorkerThreads, IdleWorkerThreads, MinWorkerThreads, MaxWorkerThreads | ThreadPool starvation detection unavailable; high-signal queue depth metric cannot be implemented | Awaiting Microsoft.Diagnostics.Runtime API enhancement | Requires direct runtime memory inspection (complex, risky) |
| **ThreadAnalyzer P1-2** | `ClrThread.Name` property not available | Thread triage acceleration lost; critical context unavailable in hang reports | Awaiting Microsoft.Diagnostics.Runtime API enhancement | Requires managed thread enumeration + TLS parsing (architecture-specific) |

**Status:** All three items marked as BLOCKED (⏳) indicating API limitations. P0-3's substitute is the global lock-contention table (not the reverse edge index — see note above); P1-1 and P1-2 are true API gaps.

---

## Cross-Analyzer / InsightEngine — Needs Re-Evaluation After All Audits Land

Several per-analyzer audits have been closing "cross-analyzer correlation" items piecemeal by
bolting a new `InsightEngine` detector onto whatever pair of analyzers the audit happened to be
looking at (e.g. `ThreadStackClusterAnalyzer` × `HangAnalyzer`, `GCGenerationAnalyzer` ×
`FinalizableObjectAnalyzer`, `StringAnalyzer` × `GCHandleAnalyzer`'s pinned-bytes breakdown,
`JitAnalyzer` × `ModuleDomainResult`, and the `StaticRootLeakDetector` × `FinalizableObjectAnalyzer`
finalizer-queue correlation planned in `static-root-leak-detector-audit.md` P3-3). Each of these was
the right call in isolation, but `InsightEngine` itself — `InsightRuleContext`'s ever-growing field
list, the three `IInsightRuleGroup` buckets, overlapping thresholds, and possible redundant/near-
duplicate detectors across analyzers audited at different times — has not been reviewed holistically.

**Action:** once every Phase 1 analyzer audit in this tracker is complete and its recommendations
implemented, do a dedicated pass over `InsightEngine.cs` as a whole: check for duplicate or
overlapping correlations, confirm `InsightRuleContext` hasn't accumulated dead/unused fields,
and confirm the rule-group split (`BaselineRuleGroup`/`MemoryAndRuntimeRuleGroup`/
`CorrelationRuleGroup`) still makes sense once every planned cross-analyzer detector exists. Do not
treat any individual audit's InsightEngine addition as final/architecturally reviewed until this
pass happens.

---

## Reverse Edge Index — Consumer Opportunities

A disk-backed reverse edge (parent-lookup) index now exists (`ReverseEdgeIndexReader.TryGetParents`), consumed today via `RootPathFinder`. It answers "who references this object" without a full in-memory reverse graph. Analyzers below already use it (directly or through `RootPathFinder`); the rest have **pending** audit recommendations that this index would unblock or simplify.

**Already wired (via `RootPathFinder`):** CollectionAnalyzer (2026-08-28: also queries `IBackwardReferenceProvider.TryGetParents` directly, independent of `RootPathFinder`, for a cheap single-hop owner-type hint — see `collection-analyzer-audit.md` P3-4), DbConnectionAnalyzer (2026-08-29: scoped to Gen2 open connections, ranked by retained bytes, capped at 20 searches — see `DbConnectionAnalyzer-audit.md` R12), DominatorAnalyzer, EventLeakAnalyzer, FinalizableObjectAnalyzer (2026-08-25), ReferenceChainAnalyzer, StaticRootLeakDetector, StringAnalyzer (2026-08-27), TimerLeakAnalyzer.

| # | Analyzer | Pending item | Audit priority | Reference |
|---|----------|---------------|-----------------|-----------|
| 1 | **GCRootAnalyzer** | P3-1: current BFS walks *forward* from the root (audit calls this structurally incorrect for "root path"); needs reverse BFS from target back to a GC root | P3 (flagged as highest-value single fix) | `gcroot-analyzer-audit.md` |
| 2 | **LeakCandidateAnalyzer** | P1-3: surface first GC root hop (field + owner type) for top-3 suspects | P1, pending | `leak-candidate-analyzer-audit.md` |
| 3 | **AsyncTaskAnalyzer** | Item 6: orphaned task GC root path sampling via `RootPathFinder` | P1/P2, pending | `async-task-analyzer-audit.md` |
| 4 | **CrashAnalyzer** | E-1: exception retention paths for Gen2 exceptions via reverse-reference index | P2, pending | `crash-analyzer-audit.md` |
| 5 | **WeakReferenceAnalyzer** | P3-2: "held only via weak reference" flag — join `WeakTarget` addresses against reverse index for strong-incoming-edge check | Pending | `weak-reference-analyzer-audit.md` |
| 6 | **GCHandleAnalyzer** | Retention path from handle to root — currently unsupported | Not an item in the audit's own roadmap table (all 4 P2 + 4 P3 items there are now done); this is a distinct, still-unscoped opportunity noted separately in `gchandle-analyzer-audit.md`'s reverse-index blockquote | `gchandle-analyzer-audit.md` |
| 7 | **ObjectShapeAnalyzer** | Static-field GC-root weight ignored; no retention-path attribution | P1/P2 pending | `object-shape-analyzer-audit.md` |

> **StringAnalyzer resolved (2026-08-27):** P3-2 (retention-path sampling for top duplicate
> patterns via `RootPathFinder`) shipped — see `string-analyzer-audit.md`. Moved to "already wired"
> above.
>
> **DbConnectionAnalyzer resolved (2026-08-29):** R12 (`!gcroot`-style retention path, scoped to
> Gen2 open connections) shipped — see `DbConnectionAnalyzer-audit.md`. Moved to "already wired"
> above.

**Explicitly ruled out:** ThreadAnalyzer P0-3 and LockGraphAnalyzer's wait-for graph — these need lock waiter→holder *thread* identity, which the object-reference reverse index does not provide (see blocker table above).


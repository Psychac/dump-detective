# Phase 6 — Trace Source

Part of [../modularity-plan.md](../modularity-plan.md). First genuinely new *product* capability in
the plan.

**Split into two independently-schedulable sub-phases**, per
[modularity-plan.md § 10 point 5](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned):
streaming ingest only needs the Phase 1/2 substrate (`Observation`, `IObservationSink`, columnar
storage), not the full 30-analyzer migration. Splitting shortens the "no trace value ships until
quite late" critical path
[modularity-plan.md § 4](../modularity-plan.md#why-trace-comes-at-phase-6-not-earlier) names,
without changing anything about *what* gets built — same ingest, same analyzers, same exit bar,
just reordered so the ingest half can start as soon as Phase 2 lands instead of waiting on Phase 5.

- **Phase 6a — Trace Ingest.** Depends on [phase-2-artifact-platform.md](phase-2-artifact-platform.md)
  (SPI + storage primitives) only. Parallelizable with Phase 5 — nothing here emits an observation
  or runs an analyzer.
- **Phase 6b — Trace-Fed Analyzers.** Two sub-items with *different* real dependencies, clarified
  2026-09-08 when [§ 8's minimum-viable path was adopted](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08):
  - **New trace-only analyzers** (`CpuHotspotAnalyzer` etc.) need 6a plus Phase 1's SDK contracts
    (`Observation`, `IObservationSink`, `ISynthesisRule` — all defined in Phase 1, not Phase 5) and
    *some* session router. They do **not** need the full Phase 5 migration — Phase 5 is about
    migrating the *existing* 30 dump analyzers, and these are new code with nothing to migrate.
    The risk this knowingly accepts: Phase 5's own rationale for going first is that it validates
    the observation model against dump findings with an exactly-known expected output before
    anything new bets on it; building trace analyzers directly on the model without that validation
    means the model is unproven the first time it's used for real. Accepted as part of the § 8
    decision, not something to silently forget.
  - **Optional-capability wiring on *existing* dump analyzers** (e.g. `GcPressureAnalyzer` gaining
    `trace.gc-events`) genuinely needs Phase 5 — the existing analyzer has to already emit
    observations before it can be extended with an optional trace capability. This sub-item is
    deferred along with Phase 5 itself in the § 8 path.

## Goal

`DumpDetective.Sources.NetTrace` implementing `IArtifactSource` — streaming `.nettrace` ingest into
a disk-backed index providing `trace.*` capabilities (6a), plus a first set of trace-fed analyzers
(6b). Trace-only sessions work end-to-end; combined sessions produce both sources' findings side by
side (actual *correlation* is Phase 7).

---

## Phase 6a — Trace Ingest

### Status: four slices shipped 2026-09-09 — `trace.methods`, `trace.gcevents`, `trace.contention`, `trace.cpu-samples`

Per the discussion that started this phase (see
[modularity-plan.md § 8](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08)):
concrete trace-ingest code first, `IArtifactSource`/`IIndexStorage` extracted later once real usage
informs their shape, not designed in a vacuum first.

**Only `.etl` (ETW) sample data exists in this environment — no `.nettrace` (EventPipe) sample
anywhere**, the same two files already used for the Phase 1 spikes. `EventPipeEventSource` and
`ETWTraceEventSource` are both `TraceEventDispatcher` subclasses sharing the identical
callback-dispatch API, so `DumpDetective.Sources.NetTrace` is built generically against that shared
base — same code path for both — and verified end-to-end against the real 912.1 MB `.etl`.
`.nettrace` support is architecturally identical but **unverified against a real sample**, the same
kind of gap and the same treatment as
[modularity-plan.md § 10 point 3](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)'s
WCF/EF corpus gap: named, not silently assumed away.

**Scoped down from the full section table before writing code**: building `trace.stacks` needs
mapping raw instruction-pointer addresses (from stack-walk events) to the method owning that address
range, which means building an address-range index from the method-load events — real, separate
design work the raw-event-callback approach doesn't get for free (unlike `TraceLog`, which was
rejected in Phase 1 for its 4.2×/2.73× memory/disk cost). `trace.methods` alone is fully buildable
from event payloads with no address resolution at all — `MethodLoadVerbose` (JIT'd during the trace)
and `MethodDCStartVerboseV2`/`MethodDCStopVerboseV2` (rundown, for methods already loaded when
tracing started/ended — the exact case this doc's own Entity resolution section warns can be missing
or truncated) all carry `MethodID`, `MethodStartAddress`, `MethodSize`, `MethodNamespace`,
`MethodName`, `MethodSignature` directly as fields, verified against the decompiled TraceEvent
package and against real captured data (`tools/MethodEventProbe`, kept in the tree alongside
`EntityJoinSpike`/`TraceEventSpike` for future re-verification, e.g. against a real `.nettrace` once
one exists). `trace.stacks` is deferred as its own next increment.

**Shipped**: `src/DumpDetective.Sources.NetTrace/` (references `Sdk` + `Platform` +
`Microsoft.Diagnostics.Tracing.TraceEvent`). `TraceMethodIndexer` streams the trace once via
`source.Clr.MethodLoadVerbose`/`MethodDCStartVerboseV2`/`MethodDCStopVerboseV2`, dedupes by
`MethodID` (bounded by distinct-method count, not event count — the same "orders of magnitude fewer
distinct X than raw events" discipline already applied to stack interning), canonicalizes the
declaring type via Phase 1's `EntityCanonicalizer`, and writes through `TraceMethodIndexWriter` into
a `trace.methods` section. `TraceIndexBuilder` wraps this in `Platform`'s existing
`CacheContainerWriter`/`TryWriteSection` — genuinely reusing the container machinery, not a bespoke
format — which is the real point of having extracted it in Phase 2.

**A real generalization question resolved simply**: reusing the container for a new artifact kind
first looked like it needed making `CacheContainerWriter`/`Reader`/`CacheTocEntry` generic over the
section-id type (touching ~40 existing declaration sites in `Analysis`). It doesn't — container
files are already one-per-artifact, so a dump's `cache.bin` and a trace's own container file never
collide, and section ids only need to be unique *within* one container. Added `CacheSectionId.TraceMethods`
as one new member to the existing enum instead — the same "purely additive, no format-version bump"
pattern already used a dozen times in that enum's own history — with a matching entry in
`CacheSectionCatalog` (`Conditional`: a dump build never writes it, which the catalog's `Required`/
`Unused` semantics don't fit). Zero changes to `CacheContainerWriter`/`Reader` or any of the ~40
existing `Analysis`-side call sites.

**A real, evidence-grounded fidelity call**: `IsDynamic`-flagged methods (real example from
`tools/MethodEventProbe`: `IL_STUB_PInvoke` under the synthetic `dynamicClass` namespace — 2 of the
first 5 real samples, not a rare case) get `MatchFidelity.None` directly from the trace event's own
flag, rather than relying on `EntityCanonicalizer`'s documented gap (it cannot detect
dynamic/reflection-emitted types from a name string alone). This is domain-specific knowledge applied
at the ingest layer where the flag is available, not a fix to the generic canonicalizer, which still
has no way to know this in general (e.g. for a dump-side name with no such flag).

**Known, named simplification**: `MethodSignature` is stored as the raw IL-notation string (e.g.
`"void  (value class System.Web.EtwTraceConfigType,int)"`, confirmed against real data), not parsed
into individual canonicalized parameter types. That parsing is real IL-signature-grammar work,
deferred rather than guessed at.

**Verified**: end-to-end against the real 912.1 MB `.etl` — `TraceMethodIndexerRealTraceTests`
(gated the same way as `[DiscrepancyFact]` real-dump tests, reusing `DD_RUN_DISCREPANCY_TESTS=1`
rather than adding a second opt-in switch) builds a real container, reads it back through
`CacheContainerReader`, and checks record uniqueness, non-empty names, and the `IsDynamic` →
`MatchFidelity.None` case against real data. 24 s end to end (streaming, not dump-loading, so much
faster than the real-dump test category it's gated alongside). Full suite: 1199 passed, 0 failed.

#### `trace.gcevents` and `trace.contention` — shipped 2026-09-09

Both scoped down from the design table below in the same evidence-grounded way as `trace.methods`
was, verified against the real 912.1 MB `.etl` with a throwaway probe
(`tools/GcContentionEventProbe`) before writing indexer code, not guessed.

**`trace.gcevents` records one GC-suspend/restart pause window, not one GC.** The probe's real
dispatch trace showed why: `GCSuspendEEStart`/`GCRestartEEStop` bracket the actual
application-observable pause, but `GCStartTraceData`/`GCEndTraceData` brackets the GC itself — for
a background GC, that span commonly crosses several separate short pause windows (observed: a
`BackgroundGC`'s `GCStart` at 16672.4954 ms didn't complete (`GCStop`) until 16715.4330 ms, a span
that itself contains two unrelated `SuspendForGCPrep` pause cycles). The two event families also
don't share a correlation id — `GCSuspendEETraceData.Count` and `GCStartTraceData.Count` were
observed non-equal for the same logical pause — so pairing has to be temporal, not by-id.
`GcPauseIndexer` attributes generation/heap-size data to a pause window only when a completed GC
(`GCStart`→`GCStop`→`GCHeapStats`, matched by `GCStartTraceData.Count` within one process) is
*fully temporally contained* in that window. This is exact for blocking GCs, which do nest fully,
and correctly attributes nothing for background GCs — an honest "no GC data" (`HasGcData = false`)
rather than a guessed nearest match, the same treatment `trace.methods` gives `IsDynamic` methods.

**`trace.contention`'s duration is computed from paired `ContentionStart`/`ContentionStop`
timestamps, not read from `ContentionStopTraceData.DurationNs`.** The probe's real data showed why:
across all 64,782 start/stop pairs in the capture — both `Native`- and `Managed`-flagged — every
single `ContentionStop` payload was `Version == 0`, for which `DurationNs` is a hardcoded `0.0` in
the TraceEvent library rather than a real measurement. Pairing by (process, thread) — a thread can
only be blocked on one contention episode at a time — and computing the duration from the two
events' own timestamps was the only reliable source in this data.

Neither section carries a `stackId` yet — `trace.stacks` is still deferred (see Index sections
below), same reason as for `trace.methods`.

**Verified**: `GcPauseAndContentionIndexerRealTraceTests` (same `.etl`, same
`DD_RUN_DISCREPANCY_TESTS=1` gating) builds both sections in one container, checks pause windows
have non-negative duration, at least one pause window has attributed GC data, and every attributed
window's heap size is positive; checks contention records have non-negative duration. 1 m 18 s for
this test alone (three independent streaming passes over the trace — one per section; see
`TraceIndexBuilder`'s remarks for why that's the proportionate choice for now rather than
consolidating into one fan-out pass). Full suite: 1199 passed, 0 failed, unchanged from the
`trace.methods` slice.

#### `trace.cpu-samples` — shipped 2026-09-09

Leaf-frame-only, scoped down the same way as the two sections above, verified against the real
912.1 MB `.etl` with a throwaway probe (`tools/CpuSampleProbe`) before writing any indexer code:
1,959,983 kernel `PerfInfoSample` (CPU-sampling-profiler interrupt) events across 76 processes, one
process (PID 8044) accounting for 501,430 of them.

**No call stack, deliberately — the same `trace.stacks` deferral named when `trace.methods`
shipped applies here too.** The kernel also emits a paired `StackWalkStack` event per sample
(2,807,821 observed in the same capture — more than the sample count, since other kernel event
kinds trigger stack walks too) carrying the full call stack, but resolving it needs frame interning
plus an address-range index built from method-load events — exactly the work
`TraceMethodIndexer` named and deferred when it shipped. `SampledProfileTraceData` already carries
the leaf instruction pointer directly, with no correlation needed, so this section resolves only
that: real per-method attribution, exclusive-only (no inclusive/call-tree time), not a placeholder.

**ETW only — a real capability gap, not just "unverified for `.nettrace`" like the other three
sections.** Checked by decompiling both event shapes, not guessed: ETW's `PerfInfoSample` carries
the leaf instruction pointer directly; EventPipe's equivalent,
`Microsoft.Diagnostics.Tracing.EventPipe.ClrThreadSampleTraceData`, carries none at all — only a
`Type` enum. The address only exists on its separately-paired `ClrThreadStackWalk` event, i.e. a
`.nettrace` capture cannot produce this section via the leaf-only shortcut regardless of source
kind — it needs the deferred stack-walk work either way, not merely a verification gap.
`TraceAnalysisRunner` drops `trace.cpu-samples` from the requested section set for any non-`.etl`
input before calling `TraceIndexBuilder.Build`, rather than letting the build fail loud for a
known, expected, documented gap.

**Cross-process address collision — named, accepted risk.** `trace.methods` carries no `ProcessId`
column, so `CpuHotspotAnalyzer`'s address-range resolution is global across every process in the
trace, not scoped per-process — two different processes' managed code could in principle load at
the same virtual address (ASLR is per-process) and be misattributed. Retrofitting `ProcessId` onto
the already-shipped `trace.methods` format is real, separate work; the probe data above shows why
this is low-severity in practice for the tool's typical single-process-of-interest capture shape —
one process accounts for 97.5% of non-idle samples in the reference capture.

**Real result, not just a passing test**: run against `HighCPU_11.etl` end to end via the actual
CLI, 13,102 of 1,959,983 total samples (0.67% — the rest are native/kernel/idle code `trace.methods`
never indexed) resolved to 1,263 distinct managed methods. The top hit,
`System.Data.DataView.RowExist` at 2,686 samples (~20% of all resolved samples), is a real,
plausible CPU hotspot for a trace file named for a high-CPU investigation — not a synthetic result.

**Verified**: `CpuHotspotIndexerRealTraceTests` (same `.etl`, same `DD_RUN_DISCREPANCY_TESTS=1`
gating) builds the section and checks record count, then runs `CpuHotspotAnalyzer` end to end and
checks every observation is well-formed. `CpuHotspotAnalyzerTests` (fast, synthetic, always-on)
characterizes the address-range resolution's exact boundary behavior — a sample on a method's first
byte, its last valid byte, one byte past the end, and in the gap between two methods — which the
real-trace test can't cheaply exercise. Full suite: 1209 passed, 0 failed.

### Ingest

**Library**: `Microsoft.Diagnostics.Tracing.TraceEvent` (`EventPipeEventSource` for streaming
`.nettrace`). The Phase 1 dependency spike (see
[phase-1-contracts-sdk.md § TraceEvent dependency spike](phase-1-contracts-sdk.md#traceevent-dependency-spike--measured-2026-09-08))
confirmed this: MIT-licensed, and the raw event-callback API streams with flat, bounded memory
independent of trace size (measured: 7 MB constant working-set delta across a 1.5M-event pass).

**Working default: build `IArtifactSource.IndexAsync` on the raw event-callback reader, not on
`TraceLog`/`TraceLog.OpenOrConvert`.** `TraceLog` is a different tool with a different cost profile
— it materializes a full random-access index (measured on a 54.9 MB sample: 4.2× the source trace's
size in working set to build, 2.73× on disk as the `.etlx` file), which is the non-streaming,
size-proportional pattern this project forbids for dumps. `IndexAsync` extracts only the per-event
fields Phase 2's columnar/intern primitives need, straight from the `AllEvents` callback, straight to
disk — the same shape as today's heap scanner, no full-trace index in between.

**Checked against a sibling implementation and against this project's own data — the decision holds.**
A sibling tool (`d:\POC\Rohit_DumpDetective`) ships trace ingest built the opposite way, entirely on
`TraceLog.OpenOrConvert`, and a code comment there cites a 27,687 MB → 13,966 MB (~0.5×, shrinking)
conversion. An earlier version of this section treated that as reason to reopen the decision. It
isn't: this project's own data contradicts it at a comparable scale.
`D:\Dumps\08-05\etls\HighCPU_11.etl` (912.1 MB, real capture) sits next to its own converted
`HighCPU_11.etlx` (2090.1 MB) on disk — **2.29× growth**, the same direction and rough magnitude as
this document's original 54.9 MB sample (2.73×/4.2×), not the sibling's 0.5×. Two real
same-direction measurements from this project's own data outweigh one unverified number in someone
else's code comment. **The original decision stands: build `IndexAsync` on the raw event-callback
reader, not `TraceLog`.** What the sibling's code still legitimately adds: the raw-callback path
must budget for stack/symbol/method-name resolution as real engineering work, since `TraceLog` does
that for free and the raw callback API does not — this was previously assumed away here. See
[phase-1-contracts-sdk.md § Cross-checked against a sibling implementation](phase-1-contracts-sdk.md#cross-checked-against-a-sibling-implementation-rohit_dumpdetective--and-against-our-own-data)
for the full comparison. Still worth doing before committing: measure a real `.nettrace` (EventPipe)
specifically — every measurement on both sides so far has been `.etl` (ETW).

**Whichever API wins, copy the sibling's single-pass fan-out dispatcher shape.** Its
`TraceEventDispatcher.Dispatch` iterates the event stream exactly once and fans out to N
registered consumers, each declaring `WantsEvent(meta)` — evaluated once per unique
(event-name, provider) pair, cached, so a consumer uninterested in an event kind pays nothing per
occurrence. This is the same "one pass, many consumers" discipline as this project's own heap
scanner and should shape `IndexAsync`/analyzer wiring regardless of the underlying trace API
decision above.

**Non-negotiable**: streaming, single-pass, bounded memory — the same discipline as heap scanning. A
trace can be larger than a dump. Never materialize the event stream, and never materialize a full
converted index of it either.

### Index sections

Reusing Phase 2's columnar writer and intern tables directly — this is where that extraction pays
off:

| Section | Columns | Notes |
|---|---|---|
| `trace.methods` | methodId → `MethodRef` | Interned; the cross-source join table. **Shipped 2026-09-09** — see Status above. Not yet "interned" in the dedicated `InternTable` sense (that type doesn't exist — Phase 2 confirmed no equivalent to extract); dedup here is a `HashSet<long>` of seen `MethodID`s during the single pass. |
| `trace.types` | typeId → `TypeRef` | Interned |
| `trace.stacks` | stackId → frame list (methodId[]) | Interned; stacks repeat heavily — dedup is the single biggest size win |
| `trace.samples` | timestamp, threadId, stackId | ~~The CPU sample stream; largest section~~ **Shipped 2026-09-09, as `trace.cpu-samples`** — see Status above and the write-up below. Renamed to match `CapabilityVocabulary.TraceCpuSamples`'s existing string, columns `timestampTicks, processId, threadId, instructionPointer` (leaf frame only, no `stackId` — full-stack `trace.stacks` still not built). |
| `trace.gcevents` | timestampTicks, threadId, reason, pauseTicks, hasGcData, gen, heapBytes | **Shipped 2026-09-09** — see Status above. One record per suspend/restart pause window, not per GC; `gen`/`heapBytes` only meaningful when `hasGcData` is set (temporal-containment attribution, see Status). No `stackId` yet. |
| `trace.allocsamples` | timestamp, typeId, size, stackId | Sampled allocation |
| `trace.contention` | startTicks, durationTicks, threadId, flags | **Shipped 2026-09-09** — see Status above. `durationTicks` computed from paired Start/Stop timestamps, not read from the payload (`DurationNs` was a hardcoded 0 in every real record observed). No `stackId` yet, unlike the design column list above. |
| `trace.exceptions` | timestamp, typeId, threadId, stackId | |
| `trace.threads` | threadId → `ThreadRef`, lifetime | |
| `trace.jit` | timestamp, methodId, durationTicks | |

Stack interning is the critical design point: a 5 M-sample trace typically has orders of magnitude
fewer distinct stacks, and stacks dominate raw size. Same principle as the existing `MethodTable`
→ type interning.

### Entity resolution

Belongs in 6a, not 6b: it validates ingest + Phase 1's `EntityCanonicalizer` against real trace
output, and needs neither an analyzer nor the observation model to run. Proving this early is a
go/no-go signal worth having *before* 6b's analyzer effort is spent, not after.

The correlation payoff depends entirely on trace-side `MethodRef`/`TypeRef` canonicalizing to the
**same `JoinKey`** as dump-side refs. Practical issues to expect:
- Trace method names come from event payloads with different formatting than ClrMD's — the
  canonicalizer must handle both and is the component that makes or breaks Phase 7.
- Rundown events may be missing/truncated, leaving unresolved method IDs. Emit these as
  `MatchFidelity.None` rather than guessing.
- Generic instantiations frequently appear differently across the two sources — the single most
  likely source of silent join failure. Needs a dedicated cross-source test corpus: capture a dump
  and a trace of the same process, assert that a known set of types/methods joins.
- **Corpus is currently single-shape.** Phase 1's entity-join spike is WCF/EF-on-`w3wp.exe` only —
  no non-WCF/EF sample was obtainable to widen it (accepted as residual risk,
  [modularity-plan.md § 10 point 3](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)).
  If a dump+trace pair from a different host shape (Kestrel self-hosted, plain console, minimal-API
  without WCF/EF) becomes available before or during this phase, add it to the corpus here rather
  than waiting for Phase 7 — this is the cheapest point to discover the join doesn't generalize.

That cross-source corpus is the most valuable test asset this phase produces.

### Phase 6a exit criteria

**Note (2026-09-09): these are the exit criteria for all of 6a; four of ten sections
(`trace.methods`, `trace.gcevents`, `trace.contention`, `trace.cpu-samples`) have shipped so far**
(Index sections table above still lists 6 more, including `trace.stacks`, which several of the
remaining ones — and `trace.cpu-samples`'s own call-tree/inclusive-time gap — depend on for a
`stackId` column). Marked below rather than silently left unmet.

- ~~Multi-GB `.nettrace` indexes within bounded memory, streaming, in reasonable time.~~
  **Partially verified** — streaming/single-pass confirmed for `.etl` against a real 912.1 MB
  capture (24 s); `.nettrace` architecturally identical but unverified (no real sample available,
  see Status above). "Multi-GB" itself not yet measured — 912.1 MB is the largest real sample on
  hand.
- ~~Cross-source entity-resolution corpus exists, with a documented pass rate per entity kind.~~
  **Not yet done.** `trace.methods` proves the ingest+canonicalization pipeline runs end-to-end
  against real trace data, but the actual dump↔trace join-rate corpus (pairing this trace output
  against a dump of the same process, the way `tools/EntityJoinSpike` did for the Phase 1 spike)
  hasn't been built yet — the next natural step once more sections exist to make the corpus worth
  building.

---

## Phase 6b — Trace-Fed Analyzers

Two sub-items, gated differently — see the split explained at the top of this document. Under the
adopted § 8 minimum-viable path, only "First analyzers" is in scope; "Also in this phase" is
deferred along with Phase 5.

### First analyzers — needs Phase 1 SDK + a session router, not the full Phase 5 migration

Aligned with the unified doc's priorities, expressed as capability-declared modules:

| Analyzer | Requires | Emits |
|---|---|---|
| `CpuHotspotAnalyzer` | ~~`trace.cpu-samples`, `trace.stacks`~~ `trace.methods` + `trace.cpu-samples` | ~~`cpu.hotspot` (inclusive/exclusive, `MethodRef`-anchored)~~ **Shipped 2026-09-09, as `cpu.sample-attribution`, exclusive-only** — see below for both the rename and the scope cut. |
| `ContentionAnalyzer` | `trace.contention-events` | ~~`contention.hotspot`~~ **Shipped 2026-09-09, as `contention.episode`** — see below for the rename. (duration, thread count) |
| `GcPauseAnalyzer` | `trace.gc-events` | `gc.pause` — **Shipped 2026-09-09.** (count, total/max pause, per-gen) |
| `ExceptionBurstAnalyzer` | `trace.exception-events` | `exception.burst` (rate, type, top stacks) |
| `AllocationHotspotAnalyzer` | `trace.alloc-samples`, `trace.stacks` | `alloc.hotspot` (rate by type + site) |

**`CpuHotspotAnalyzer` doesn't require `trace.stacks`, unlike the table's original design —
it never got built (see Phase 6a).** Resolving only the sample's leaf instruction pointer against
`trace.methods`'s own address ranges (own remarks in `CacheSectionId.TraceCpuSamples`) gives
real per-method attribution without the deferred stack-walk/interning work, at the cost of
exclusive-only numbers — no inclusive (callee-rolled-up) time, no call tree. `trace.methods` becomes
a real dependency here for the first time since it shipped: an address-range lookup, not a join-key
canonicalization consumer.

Note these are *new analyzers in existing domain packages* (`Plugins.Cpu`, `Plugins.Threads`,
`Plugins.Gc`, `Plugins.Runtime`, `Plugins.Memory`) — not a separate "trace analyzers" package. The
packaging axis is domain, not source. **Not followed by the three shipped analyzers**: Phase 3 (the
plugin-package split that domain packages like `Plugins.Gc` belong to) is deferred under § 8, so
`GcPauseAnalyzer`/`ContentionAnalyzer`/`CpuHotspotAnalyzer` live as plain classes directly in
`DumpDetective.Sources.NetTrace` for now — there is no `Plugins.Gc`/`Plugins.Cpu` package to put
them in yet. They
implement a minimal `ITraceAnalyzer` (no `[RequiresCapability]` attribute, no registry lookup —
nothing resolves those yet either), not a capability-declared module. Re-homing into domain packages
is Phase 3's job, not redone here.

**`ContentionAnalyzer` emits `contention.episode`, not the `contention.hotspot` this table
originally specified.** "Hotspot" is a severity/ranking claim (which episodes are worth flagging),
and docs/refactor/modularity/observation-and-correlation-model.md § 2a already had to correct
exactly this failure mode for a different analyzer (`gc.pressure` → a factual type, severity moved
to synthesis). One raw fact per episode is what an analyzer emits; "hotspot" identification is a
synthesis-rule output once one exists (Phase 5, deferred). `GcPauseAnalyzer`'s `gc.pause` needed no
such rename — "pause" is a factual GC-runtime term, not a verdict.

**`CpuHotspotAnalyzer` emits `cpu.sample-attribution`, for the same reason.** "Hotspot" is again a
ranking claim; "N samples landed in this method" is the raw fact one method's observation carries.

**All three analyzers emit raw per-record (or, for CPU, per-method) observations, not the
aggregated "count, total/max pause" / inclusive-exclusive breakdowns this table's Emits column
describes** — that aggregation is a synthesis-rule job (Phase 5, not built), so
`TraceOrchestrationService` computes it only for a plain console printout, never folding it back
into an `Observation`. See the analyzers' own doc comments for the purity reasoning.

### Also in this phase — genuinely needs Phase 5, deferred under the adopted path

Extend existing dump analyzers with `[OptionalCapability("trace.*")]` where the graded-fidelity
story applies (`GcPressureAnalyzer` gaining `trace.gc-events` is the canonical example). This is
what makes the capability model earn its keep, and it's easy to defer indefinitely if not scheduled
explicitly — which, under § 8, is exactly what's happening for now: it needs the target existing
analyzer already migrated to emit observations, which needs Phase 5, which § 8 defers. Revisit once
Phase 5 is scheduled.

### The interim router — shipped 2026-09-09

docs/refactor/modularity-plan.md § 8 names this explicitly as accepted debt ("Accept an interim
router... not a permanent design") without designing it; phase-6-trace-source.md's own "First
analyzers" note above says building them needs "Phase 1 SDK contracts... and *some* session
router." Both are now concrete rather than a forward reference:

- `DumpDetective.Sources.NetTrace.TraceAnalysisRunner` — builds a trace's own temp container
  (only the sections `ITraceAnalyzer.RequiredSections` actually asks for, not all of them — see its
  own doc remarks for the measured wall-clock cost of building an unneeded section), runs every
  registered `ITraceAnalyzer`, hands back the observations. Deletes the temp container afterward;
  no cache-hit reuse yet (named simplification, see its own remarks).
- `DumpDetective.Cli.Execution.TraceOrchestrationService` — the CLI-facing half: calls
  `TraceAnalysisRunner`, prints a plain console summary, writes `report.json` unconditionally
  (§ 8 step 6 — see below).
- `DumpAnalysisService.ExecuteAsync` sniffs `request.DumpPath`'s extension (`.etl`/`.nettrace`) and
  routes to `TraceOrchestrationService` *before* any dump-specific config resolution or startup
  validation runs, rather than after — those assume a dump path and would reject a trace file.
- `RootCommandBuilder`'s `dump-path` argument and `--output` option descriptions updated to say so
  explicitly, rather than reading as dump-only when they've silently accepted trace paths all
  along.

What this router explicitly does **not** do, all deliberately out of scope for this increment: no
session/artifact model, no capability resolution, no combined dump+trace path (a trace and a dump
can't yet be pointed at in the same run). Verified against the real 912.1 MB `.etl` end to end via
the CLI, and with a routing-decision unit test (`DumpAnalysisServiceRoutingTests`) covering the
extension sniff without standing up the full `DumpAnalysisService` dependency graph.

#### `report.json` (§ 8 step 6) — shipped 2026-09-09

The minimal slice of "report.json unconditional... so a UI has a contract" that's actually
supportable today: `TraceSessionReport` (trace path, generated-at timestamp, the full observation
list) serialized via `TraceReportWriter`, written on every trace run regardless of whether
`--output` was passed — unlike the dump side's `ReportOutputWriter`, which today writes nothing at
all when `--output` is omitted. There was no existing "silent by default" trace behavior worth
preserving, since trace analysis had no report output whatsoever before this.

**Deliberately not the full session-report schema v3**
[phase-8-sinks-and-ui.md](phase-8-sinks-and-ui.md) describes (`sources[]`, `timeline`,
`capabilityReport`, findings with `ConfidenceBreakdown`) — that needs a real session/artifact model
(Phase 4) and a synthesis engine (Phase 5), neither of which exist under § 8. This is one artifact's
raw observations, nothing more.

**A real bug this surfaced, not merely a design choice**: `EntityRef`'s `[JsonPolymorphic]`
discriminator was first named `"kind"`, which collided with `EntityRef.Kind` itself (also
camelCases to `"kind"`) and threw at serialize time on the very first real run — caught immediately
because report writing is unconditional, not a code path someone has to remember to exercise.
Fixed by renaming the discriminator to `"$kind"`, matching the convention `AnalysisReportDocument`
(Reporting project) already established for exactly this collision. Without polymorphic attributes
at all, `Observation.Subjects` (declared `IReadOnlyList<EntityRef>`) would have silently serialized
only `EntityRef`'s own three base members and dropped every subtype field — the method name, the
thread id — making the JSON useless without ever throwing; guarded by
`EntityRef_SerializesAndRoundTripsThroughThePolymorphicBaseType` (fast, synthetic, always-on).

**Real size, not a synthetic worry**: the `HighCPU_11.etl` report is 89 MB for 66,132 observations,
dominated by 64,782 contention episodes. Phase 8's own doc already names this exact problem
("observations optionally embedded or side-carred... a reference, not an inline dump, for big
sessions") and defers the fix — not addressed here, consistent with that.

**CLI wiring**: `--output` now flows from `AnalysisCommandRequest` through
`DumpAnalysisService`/`TraceOrchestrationService` to `TraceReportWriter` — previously silently
ignored for a trace input (dropped on the floor, no error, no output). Missing output directories
are created rather than failing; wrapped write failures now include the real underlying exception
message and the target path, not just a generic wrapper string (also caught by hand while smoke
testing this — the original message gave no way to tell what actually went wrong).

**Relocated 2026-09-09, same day it shipped**: `TraceSessionReport` (the report *shape*) initially
landed in `DumpDetective.Cli` alongside `TraceReportWriter` — expedient (`Cli` already had
transitive `Sdk` access via `Sources.NetTrace`, `Reporting` didn't reference `Sdk` at all yet), not
a reasoned placement. Moved to `DumpDetective.Reporting` on review: that project is the one that
already owns report-shape contracts (`AnalysisReportDocument` lives there), the same reason
`TraceReportWriter`'s file-writing role correctly stays in `Cli` next to `ReportOutputWriter`. Cost
one new `Reporting → Sdk` project reference — clean, no cycle, `Sdk` sits at the bottom of the
dependency graph. `DependencyDirectionTests` updated to match.

**Verified**: `IdentityTests.EntityRef_SerializesAndRoundTripsThroughThePolymorphicBaseType` and
`TraceReportWriterTests` (both fast, synthetic, always-on) plus a manual end-to-end run against the
real `.etl` via the CLI (both default and `--output`-overridden paths). Full suite: 1213 passed, 0
failed.

### Phase 6b exit criteria

- ~~Trace-only session runs end-to-end producing CPU + contention findings at minimum.~~ **Met,
  2026-09-09**: `GcPauseAnalyzer`, `ContentionAnalyzer`, and `CpuHotspotAnalyzer` all run
  end-to-end via `TraceOrchestrationService` against a real trace, producing real findings — CPU
  hotspot resolution surfaced a genuine, plausible hotspot (`System.Data.DataView.RowExist`,
  ~20% of resolved samples) in a capture named for a high-CPU investigation. CPU coverage is
  exclusive-only/leaf-frame-only, not the inclusive/call-tree breakdown the original design table
  described — see Phase 6a's `trace.cpu-samples` write-up for why that's a named scope cut, not an
  oversight.
- Combined dump+trace session runs both sources' analyzers and reports both (uncorrelated). **Not
  done** — the router above is trace-only; wiring a combined run is the natural next increment, not
  bundled into this one.
- ~~≥ 1 existing dump analyzer demonstrably improves via an optional trace capability.~~ Deferred
  under § 8 — see "Also in this phase" above. Not an exit criterion for the adopted path; revisit
  when Phase 5 is scheduled.

---

## Risk / effort

**Highest raw effort in the plan** and the most genuine unknowns — trace parsing, volume, and event
semantics are new territory for this codebase. Phase 2's storage extraction removes maybe half the
work by making the index layer reusable.

Biggest risk is entity resolution quality: if dump and trace names don't reliably join, Phase 7's
correlation is worthless no matter how well-engineered. **Validate the join early** — 6a's
cross-source corpus and join-rate measurement is exactly that validation, and doing it in 6a means
it happens *before* 6b's analyzer effort is spent, not after. That measurement is a genuine
go/no-go signal for the multi-source thesis, and it's cheap to get early.

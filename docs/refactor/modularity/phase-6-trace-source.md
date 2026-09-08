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
| `trace.methods` | methodId → `MethodRef` | Interned; the cross-source join table |
| `trace.types` | typeId → `TypeRef` | Interned |
| `trace.stacks` | stackId → frame list (methodId[]) | Interned; stacks repeat heavily — dedup is the single biggest size win |
| `trace.samples` | timestamp, threadId, stackId | The CPU sample stream; largest section |
| `trace.gcevents` | timestamp, gen, reason, pauseTicks, heapBytes | |
| `trace.allocsamples` | timestamp, typeId, size, stackId | Sampled allocation |
| `trace.contention` | startTicks, durationTicks, threadId, stackId | |
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

- Multi-GB `.nettrace` indexes within bounded memory, streaming, in reasonable time.
- Cross-source entity-resolution corpus exists, with a documented pass rate per entity kind.

---

## Phase 6b — Trace-Fed Analyzers

Two sub-items, gated differently — see the split explained at the top of this document. Under the
adopted § 8 minimum-viable path, only "First analyzers" is in scope; "Also in this phase" is
deferred along with Phase 5.

### First analyzers — needs Phase 1 SDK + a session router, not the full Phase 5 migration

Aligned with the unified doc's priorities, expressed as capability-declared modules:

| Analyzer | Requires | Emits |
|---|---|---|
| `CpuHotspotAnalyzer` | `trace.cpu-samples`, `trace.stacks` | `cpu.hotspot` (inclusive/exclusive, `MethodRef`-anchored) |
| `ContentionAnalyzer` | `trace.contention-events` | `contention.hotspot` (duration, thread count) |
| `GcPauseAnalyzer` | `trace.gc-events` | `gc.pause` (count, total/max pause, per-gen) |
| `ExceptionBurstAnalyzer` | `trace.exception-events` | `exception.burst` (rate, type, top stacks) |
| `AllocationHotspotAnalyzer` | `trace.alloc-samples`, `trace.stacks` | `alloc.hotspot` (rate by type + site) |

Note these are *new analyzers in existing domain packages* (`Plugins.Cpu`, `Plugins.Threads`,
`Plugins.Gc`, `Plugins.Runtime`, `Plugins.Memory`) — not a separate "trace analyzers" package. The
packaging axis is domain, not source.

### Also in this phase — genuinely needs Phase 5, deferred under the adopted path

Extend existing dump analyzers with `[OptionalCapability("trace.*")]` where the graded-fidelity
story applies (`GcPressureAnalyzer` gaining `trace.gc-events` is the canonical example). This is
what makes the capability model earn its keep, and it's easy to defer indefinitely if not scheduled
explicitly — which, under § 8, is exactly what's happening for now: it needs the target existing
analyzer already migrated to emit observations, which needs Phase 5, which § 8 defers. Revisit once
Phase 5 is scheduled.

### Phase 6b exit criteria

- Trace-only session runs end-to-end producing CPU + contention findings at minimum.
- Combined dump+trace session runs both sources' analyzers and reports both (uncorrelated).
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

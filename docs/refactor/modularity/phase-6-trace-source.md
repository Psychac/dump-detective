# Phase 6 — Trace Source

Part of [../modularity-plan.md](../modularity-plan.md). First genuinely new *product* capability in
the plan. Depends on [phase-2-artifact-platform.md](phase-2-artifact-platform.md) (SPI + storage
primitives) and [phase-5-observations-synthesis.md](phase-5-observations-synthesis.md)
(observation model).

## Goal

`DumpDetective.Sources.NetTrace` implementing `IArtifactSource` — streaming `.nettrace` ingest into
a disk-backed index providing `trace.*` capabilities, plus a first set of trace-fed analyzers.
Trace-only sessions work end-to-end; combined sessions produce both sources' findings side by side
(actual *correlation* is Phase 7).

## Ingest

**Library**: `Microsoft.Diagnostics.Tracing.TraceEvent` (`EventPipeEventSource` for streaming
`.nettrace`). *Unverified in this session* — confirm the streaming API surface, memory behavior on
multi-GB traces, and licensing before committing; the alternative is a hand-rolled nettrace parser,
which is real work but gives full control over allocation behavior.

**Non-negotiable**: streaming, single-pass, bounded memory — the same discipline as heap scanning.
A trace can be larger than a dump. Never materialize the event stream.

## Index sections

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

## First analyzers

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

**Also in this phase**: extend existing dump analyzers with `[OptionalCapability("trace.*")]`
where the graded-fidelity story applies (`GcPressureAnalyzer` gaining `trace.gc-events` is the
canonical example). This is what makes the capability model earn its keep, and it's easy to defer
indefinitely if not scheduled explicitly.

## Entity resolution

The correlation payoff depends entirely on trace-side `MethodRef`/`TypeRef` canonicalizing to the
**same `JoinKey`** as dump-side refs. Practical issues to expect:
- Trace method names come from event payloads with different formatting than ClrMD's — the
  canonicalizer must handle both and is the component that makes or breaks Phase 7.
- Rundown events may be missing/truncated, leaving unresolved method IDs. Emit these as
  `MatchFidelity.None` rather than guessing.
- Generic instantiations frequently appear differently across the two sources — the single most
  likely source of silent join failure. Needs a dedicated cross-source test corpus: capture a dump
  and a trace of the same process, assert that a known set of types/methods joins.

That cross-source corpus is the most valuable test asset this phase produces.

## Exit criteria

- Multi-GB `.nettrace` indexes within bounded memory, streaming, in reasonable time.
- Trace-only session runs end-to-end producing CPU + contention findings at minimum.
- Combined dump+trace session runs both sources' analyzers and reports both (uncorrelated).
- Cross-source entity-resolution corpus exists, with a documented pass rate per entity kind.
- ≥ 1 existing dump analyzer demonstrably improves via an optional trace capability.

## Risk / effort

**Highest raw effort in the plan** and the most genuine unknowns — trace parsing, volume, and event
semantics are new territory for this codebase. Phase 2's storage extraction removes maybe half the
work by making the index layer reusable.

Biggest risk is entity resolution quality: if dump and trace names don't reliably join, Phase 7's
correlation is worthless no matter how well-engineered. **Validate the join early** — build the
cross-source corpus and measure join rates *before* investing in the full analyzer set. That
measurement is a genuine go/no-go signal for the multi-source thesis, and it's cheap to get early.

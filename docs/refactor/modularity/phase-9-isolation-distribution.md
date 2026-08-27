# Phase 9 — Process Isolation & Distributed Execution (Speculative)

Part of [../modularity-plan.md](../modularity-plan.md). Explicitly speculative — designed-for, not
scheduled. Pursue only against a concrete driver. Depends on
[phase-2-artifact-platform.md](phase-2-artifact-platform.md) and
[phase-3-plugin-packaging.md](phase-3-plugin-packaging.md).

## Why separate from Phase 3

Phase 3 gives plugins `AssemblyLoadContext` isolation, which solves version conflicts and enables
unloading. A plugin that corrupts memory, hangs, or crashes still takes the host down. Real fault
containment needs process boundaries — a materially bigger investment (IPC, serialization, harder
debugging) that only pays off with a real need to run untrusted or unvetted analyzer code, which
isn't true today (all analyzers are first-party).

## Drivers that would justify it

- Accepting community-contributed plugins without auditing each one.
- An analyzer expensive or unstable enough to warrant isolated resource limits on its own merits
  (an experimental ML-based classifier, say).
- Distributing analysis across machines for extremely large artifacts.
- **Multi-source makes this modestly more attractive** than it was in the dump-only plan: trace
  ingest and dump ingest are independent, so a session with several artifacts has natural
  process-level partitioning — though the memory-safety constraint on concurrent large-artifact
  loading (see [phase-4-session-orchestration.md](phase-4-session-orchestration.md)) limits how much
  that can actually be exploited on one machine.

## Sketch — out-of-process plugins

- Read-only artifact index containers (Phase 2) are opened directly by the plugin host process — no
  index data over IPC, only observations and control messages.
- IPC carries the Phase 1 observation wire schema over a local channel (named pipes / gRPC).
  Deliberately not a new ad hoc protocol — the observation schema already exists for exactly this
  kind of boundary.
- Resource bounds from `ExecutionPolicy` become hard process limits (job objects / cgroups) rather
  than best-effort in-process checks.
- To the session DAG, an out-of-process node looks like any other node with higher latency.

## Sketch — distributed execution

- `IIndexStorage` gains a remote implementation; writer/reader code is unchanged since it only
  depends on the `Stream` SPI.
- Genuinely interesting only alongside distributed *ingest* (splitting heap-segment scanning or
  trace-chunk parsing across machines and merging indices) — a substantially larger design than
  swapping a storage backend, and not sketched here. Own design doc if the driver materializes.

## Exit criteria

Deliberately undefined. Scoping speculative work in detail produces plans nobody asked for; write
this phase properly when a driver is real.

## Risk / effort

High effort, hard to estimate without knowing which half (isolation vs. distribution) matters.
They're largely independent efforts sharing a phase number because both are "beyond what today's
single-process, single-machine, first-party-only model needs."

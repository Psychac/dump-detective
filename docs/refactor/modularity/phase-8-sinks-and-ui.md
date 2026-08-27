# Phase 8 — Sinks & Unified UI

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 5**.
Depends on [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md) for the schema; the low-risk pieces
can land opportunistically much earlier.

## Goal

Replace `IReportFormatter` (produces a string) with **sinks** consuming the versioned session
report schema, and make the report a first-class artifact that a real UI can consume — including
the multi-source and timeline shapes the earlier phases produce.

## Sinks

```csharp
public interface IReportSink
{
    string Name { get; }
    ValueTask PublishAsync(SessionReport report, SinkOptions options, CancellationToken ct);
    ValueTask PublishIncrementalAsync(ReportDelta delta, CancellationToken ct);  // progressive
}
```

The difference from a formatter isn't cosmetic: "turn this into a string" forces anything that
isn't file output to awkwardly stringify first. "Do something with this report" lets a websocket
sink, an HTTP export, and a file writer be peers.

`PublishIncrementalAsync` pairs with Phase 4's progressive execution — findings stream as nodes
complete rather than appearing after the whole graph finishes. On a large session (three dumps plus
a trace) that's the difference between a five-minute blank screen and a report that fills in.

| Package | Sinks |
|---|---|
| `Sinks.File` | text, markdown, html, json — today's four formatters, re-homed |
| `Sinks.Streaming` | websocket/SignalR for live UI |
| `Sinks.Export` | SARIF, OpenTelemetry — speculative, build on concrete demand |

**`report.json` becomes unconditional** — written every run regardless of `--format`. Small cost,
and it's the thing any future UI or external tool can rely on existing.

## Report shape changes

Schema v3 (defined back in Phase 1, populated for real here):

- `sources[]` — every artifact, with capture time, capabilities provided, index stats
- `timeline` — alignment result *and its provenance/confidence*
- `capabilityReport` — analyzers run / degraded / skipped, with the specific missing capability
- findings carry source attribution, `ConfidenceBreakdown`, and observation lineage
- per-artifact detail sections coexist with cross-source correlated sections
- observations optionally embedded or side-carred (they can be large — a reference, not an inline
  dump, for big sessions)

`AnalysisReportDocument` types get promoted from `internal` to `public` — they're a consumer
contract now, held to the schema's versioning policy.

## UI

The report JSON is the contract; the UI is a separate deliverable that consumes it. What the
multi-source model makes newly possible:

- **Timeline view** — artifacts on a time axis; a trace interval with dump points overlaid on it is
  the natural visual for a combined session, and it makes alignment quality *visible* rather than
  buried in a caveat.
- **Entity-centric navigation** — pivot on a `TypeRef`/`MethodRef` and see every observation about
  it from every source. This is only possible because of the entity model; it's arguably the single
  most useful UI affordance the architecture unlocks.
- **Confidence transparency** — surface `LimitingFactors` inline rather than a bare number.
- **Lineage drill-down** — finding → observations → artifact + analyzer + capabilities.

**Live query** (click a type → query the heap beyond what's in the report) remains the bigger,
separate ask: it needs a long-running host exposing capability query surfaces over a service
boundary. Phase 4 makes that structurally possible (the CLI is just one host); it is not scoped
here.

## Migration steps

1. `IReportSink` + `FileReportSink` wrapping existing formatters unchanged *(can land right after
   Phase 1 — genuinely low risk, useful immediately)*.
2. Unconditional `report.json` *(same — land early)*.
3. Promote report types to public; schema-conformance test against generated reports.
4. `StreamingReportSink` + incremental publishing, once Phase 4's progressive execution exists.
5. UI prototype against a real multi-source `report.json`.

## Exit criteria

- `IReportFormatter` gone; all four formats are sinks with unchanged output on golden reports.
- `report.json` written every run; validates against `session-report.schema.json`.
- A UI prototype renders a real dump+trace session including timeline and entity pivot.
- Progressive/streaming path demonstrated on a long-running session.

## Risk / effort

Low-to-medium for steps 1–3 (mechanical). Medium for streaming. The UI is a genuinely separate
product effort that shouldn't gate calling this phase done — recommend timeboxing the prototype and
treating a full UI as its own initiative with its own plan.

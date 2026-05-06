# EventLeakAnalyzer — Presets

Purpose: identify event/delegate retention and listener leaks.

Options observed in code (`EventLeakOptions`):
- `MinSubscribers` (int) — minimum subscribers for an object to be considered a publisher.
- `IncludeNonLeakingEvents` (bool) — when true, scan all delegate fields (enables full subscription graph).
- `TopSubscriberTypesToShow` (int) and `TopDetailedInstancesPerGroup` (int) — presentation knobs for top-N breakdowns.
- `EnableLowIncomingRefsCheck` (bool) — expensive per-subscriber incoming-ref check; disabled by default.
- `EnableDiagnostics` (bool) — emit timing/diagnostic counters.

Fast:
- `MinSubscribers`: 3
- `IncludeNonLeakingEvents`: false
- `TopSubscriberTypesToShow`: 3
- `TopDetailedInstancesPerGroup`: 3
- `EnableLowIncomingRefsCheck`: false
- `EnableDiagnostics`: false

Balanced (default):
- `MinSubscribers`: 0
- `IncludeNonLeakingEvents`: false
- `TopSubscriberTypesToShow`: 5
- `TopDetailedInstancesPerGroup`: 5
- `EnableLowIncomingRefsCheck`: false
- `EnableDiagnostics`: true

Full:
- `MinSubscribers`: 0
- `IncludeNonLeakingEvents`: true
- `TopSubscriberTypesToShow`: 20
- `TopDetailedInstancesPerGroup`: 20
- `EnableLowIncomingRefsCheck`: true (use with caution)
- `EnableDiagnostics`: true

Flow notes:
- `IncludeNonLeakingEvents` turns on the full subscription-graph scan and can be expensive on large heaps.
- `EnableLowIncomingRefsCheck` is extremely costly; enable only for targeted, small-run investigations.

Rationale — when to pick each preset:
- **Fast:** increase `MinSubscribers` to reduce noisy small-multicast events and keep `IncludeNonLeakingEvents=false`.
- **Balanced:** (default) surface likely publishers and a small set of detailed instances while leaving expensive probes disabled.
- **Full:** enable full subscription-graph (`IncludeNonLeakingEvents=true`) and `EnableLowIncomingRefsCheck=true` for deep validation of suspected leaks (high CPU/I/O cost).

Next steps:
- Add a short warning in the presets README about enabling `EnableLowIncomingRefsCheck` only on small dumps or targeted runs.

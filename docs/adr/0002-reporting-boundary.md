# ADR 0002: Reporting Composition/Rendering Boundary

## Status
Accepted

## Context
Refactor Spec 05 requires strict separation between report composition and output formatting.

## Decision
- `ReportBuilder` composes canonical report sections and performs source-level deduplication.
- `IReportFormatter` implementations render already-composed model only.
- Shared wrap behavior is enforced by common helper logic.
- Reporter rendering boundary uses `IReportWriter` abstraction.
- Legacy formatter compatibility stack is removed from active architecture.

## Consequences
- Semantics are decided once in composition layer.
- Formatter parity is testable via golden snapshots.
- No formatter-specific dedup side effects.
- Long values remain representable without truncation.

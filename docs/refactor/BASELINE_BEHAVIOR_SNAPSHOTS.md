# Baseline Behavior Snapshots

## Scope
Refactor validation baseline for Specs 01–07 in the `.NET 10` multi-project architecture.

## Snapshot A — Build and Tests
- Solution build: `dotnet build DumpDetective.slnx` succeeds.
- Test suite: `dotnet test DumpDetective.Tests/DumpDetective.Tests.csproj` succeeds.
- Current test total at capture: `32 passed`.

## Snapshot B — CLI Behavior and Exit Codes
Validated end-to-end CLI flow with and without config:
- CLI dump path missing on disk -> configuration validation failure (`ExitCodes.ConfigurationFailure = 1`).
- Config file provided with alternate dump path -> config value wins over CLI dump path.
- Explicit missing `--config` path -> clear config-not-found failure (`ExitCodes.ConfigurationFailure = 1`).

## Snapshot C — Reporting Boundary
- Canonical composed model is the sole report input to active formatters.
- Source-level dedup is performed before rendering.
- Long values are wrapped (not truncated) in `text`, `markdown`, and `html`.
- Legacy static formatter stack has been removed.

## Snapshot D — Observability and Performance Guardrails
- Normalized diagnostics event model is active in pipeline lifecycle.
- Analyzer and run-level metrics include timing and cache/scan counters.
- Benchmark smoke + baseline threshold comparison are wired in CI.

## Notes
- Physical `src/` + `tests/` directory alignment remains an open structural closure item in Spec 01.

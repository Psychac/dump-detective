# ADR 0001: Dependency Direction for Refactored Architecture

## Status
Accepted

## Context
The refactor introduced a layered architecture (`Core`, `Analysis`, `Reporting`, `Cli`) that must remain stable and testable.

## Decision
Enforce directional dependencies:
- `DumpDetective.Core` -> no project references to higher layers.
- `DumpDetective.Analysis` -> references `DumpDetective.Core` only.
- `DumpDetective.Reporting` -> references `DumpDetective.Core` only.
- `DumpDetective.Cli` -> references `DumpDetective.Core`, `DumpDetective.Analysis`, and `DumpDetective.Reporting`.
- `BenchmarkSuite1` -> may reference all layers for hotspot measurement.
- Legacy `DumpDetective` project remains side-by-side for compatibility and migration continuity.

## Consequences
- Clear layering and isolation of contracts/options in `Core`.
- Pipeline/reporting concerns remain separated and testable.
- Dependency drift can be audited by project reference review and build validation.

# DumpDetective Project Critical Review Index

## Status
Review set created on 2026-05-30.

## Purpose
This index links the project-by-project critical reviews for the active solution only:
- `DumpDetective.Cli`
- `DumpDetective.Reporting`
- `DumpDetective.Analysis`
- `DumpDetective.Core`

## Review Files
- `cli-project-critical-review.md`
- `reporting-project-critical-review.md`
- `analysis-project-critical-review.md`
- `core-project-critical-review.md`

## Suggested Reading Order

### 1. `Cli`
Read first because it shows where feature ownership and orchestration have accumulated incorrectly.

### 2. `Reporting`
Read second because it is the next-largest structural hotspot after CLI ownership drift.

### 3. `Analysis`
Read third because it separates justified performance complexity from accidental structural complexity.

### 4. `Core`
Read last because it is mostly stable and its issues are boundary-tightening issues rather than broad structural problems.

## High-Level Theme Across All Reviews
- `Cli` should become a thinner shell.
- `Reporting` should separate canonical composition from report-app behavior.
- `Analysis` should preserve its performance spine while shrinking broad analyzers and cross-cutting policy hosts.
- `Core` should stay small and tighten its contract boundaries.

## Recommended Next Step
Use the project reviews alongside `architecture-refactor-roadmap.md`.

The roadmap gives program order.

The project reviews give project-local cleanup detail.
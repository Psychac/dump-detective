# Baseline Behavior Snapshots

## Purpose
This document records what Phase 0 snapshots mean and how to use them during refactors.

## Snapshot Files
All baseline snapshots are written to `artifacts/reports/phase0/`.

### `registration-snapshot.json`
Captures structural topology at startup time:
- analyzer list
- finding generators
- trend comparers
- analyzer section builders
- report section builders

Use this file to detect accidental registration drift.

### `single-dump-smoke.json`
Summarizes smoke tests for single-dump report composition behavior.

### `trend-smoke.json`
Summarizes smoke tests for trend report composition behavior.

### `html-smoke.json`
Summarizes smoke tests for embedded JSON/report HTML behavior.

### `guardrail-tests.json`
Summarizes targeted Phase 0 guardrail tests:
- CLI entrypoint behavior guards
- dominator finding generation behavior guards

### `golden-dump-set.manifest.json`
Declares required dump identities for future local dump-backed baseline runs.
It is a contract manifest and can be paired with environment-local path mapping.

## How To Compare
For each refactor wave:
1. Run Phase 0 baseline script before changes.
2. Run Phase 0 baseline script after changes.
3. Compare JSON snapshots and test outcomes.

Expected stable signals:
- no unexpected drops/additions in topology lists
- smoke and guardrail status remains pass
- report behavior snapshots stay equivalent unless change is intentional

## Operational Rule
When a snapshot change is intentional:
- update this document and the relevant architecture/refactor notes
- include why the drift is expected
- include impact scope and rollback signal

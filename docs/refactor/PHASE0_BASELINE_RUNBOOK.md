# Phase 0 Baseline Runbook

## Goal
Create and refresh a reproducible baseline safety net before architecture refactors.

This runbook captures:
- resolved analyzer/registration topology
- smoke checks for single-dump and trend report paths
- baseline artifact files under `artifacts/reports/phase0/`

## Preconditions
- run from repository root
- .NET SDK installed (net10.0)
- test project builds locally

Optional for full end-to-end dump validation:
- local dump files referenced by your local Phase 0 dump manifest overrides

## Command
```powershell
./tools/Phase0/Invoke-Phase0Baseline.ps1
```

Fast path (skip tests):
```powershell
./tools/Phase0/Invoke-Phase0Baseline.ps1 -SkipTests
```

Custom output folder:
```powershell
./tools/Phase0/Invoke-Phase0Baseline.ps1 -OutputDir "artifacts/reports/phase0"
```

## Produced Artifacts
The script writes these files:
- `golden-dump-set.manifest.json`
- `registration-snapshot.json`
- `single-dump-smoke.json`
- `trend-smoke.json`
- `html-smoke.json`
- `guardrail-tests.json`

## Validation Expectations
- `registration-snapshot.json` should reflect current analyzer/finding/trend/section registrations.
- Smoke files should report `status: "pass"` with zero failed tests.
- Any failing guardrail test must be resolved or explicitly accepted before topology moves.

## Scope Note
Current Phase 0 smoke uses integration and unit guardrails that do not require real dump files.
The golden dump manifest is still produced so teams can layer local dump-path baselines without changing script shape.

## Related Docs
- `docs/improvements/consolidated-refactor-program.md` (Phase 0 intent)
- `docs/refactor/BASELINE_BEHAVIOR_SNAPSHOTS.md` (artifact interpretation)

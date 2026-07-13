---
title: Finding 4 — Disk cache: validate satellite files
---

# Finding 4 — Disk cache: validate satellite files

Status: Open — design for robust, non-quick fix.

## Summary

The disk fast-path currently treats the presence of `ObjectIndex.bin` and
`TypeAggregateIndex.bin` as the only signal that an on-disk cache is
complete. Satellite files (for example `RootIndex.bin`, `LargeObjectIndex.bin`,
`HandleSnapshot.bin`) may have failed to write during an earlier writer run
(logged as non-fatal). Later runs then accept the cache fast-path and
silently produce incomplete analyzer outputs.

This document describes the correct, robust fix: an atomic manifest-based
protocol, load-time validation, optional repair/regeneration, and tests.

## Goals

- Ensure cache fast-path only accepts fully-consistent caches.
- Provide an efficient, safe load path with a configurable fast/strict tradeoff.
- Offer a repair mode to regenerate missing satellites when feasible.
- Add tests and observability to prevent silent regressions.

## Index manifest (canonical signal)

Introduce a single manifest file in the index folder, e.g. `index.manifest.json`.
The manifest is written last by the writer and is the canonical "complete"
signal. Minimal manifest fields:

```json
{
  "schemaVersion": 1,
  "writerVersion": "v1",
  "files": [
    { "name": "ObjectIndex.bin", "size": 12345, "sha256": "..." },
    { "name": "TypeAggregateIndex.bin", "size": 2345, "sha256": "..." },
    { "name": "RootIndex.bin", "size": 345, "sha256": "..." }
  ],
  "createdUtc": "2026-07-13T00:00:00Z"
}
```

Notes:
- Checksums may be full SHA256 or a faster CRC32 depending on `strictValidation`.
- Include `schemaVersion` to support future format changes.

## Atomic write protocol (writer-side)

1. Writer writes each satellite file to a temp name (e.g., `*.tmp`) or into a
   temp directory unique to this run (e.g., `index.tmp.<pid>`).
2. For each file, ensure it is flushed to disk (fsync) where supported.
3. Compute sizes and checksums and build `index.manifest.json` in the temp
   location.
4. Atomically publish: rename temp files into place (or move temp dir → final
   dir) and then write the final manifest at the target location. Fallback to
   per-file atomic rename when cross-volume moves are not possible.

Rationale: presence of the final manifest is the only signal readers trust.

## Load-time validation (reader-side)

Replace the heuristic `File.Exists()` checks with manifest-driven validation:

- If `index.manifest.json` is present: parse it and ensure all listed files
  exist and match size and (optionally) checksum.
- If manifest is missing or invalid: treat cache as untrusted and refuse fast-path
  (rebuild from heap) unless configured to `compatAcceptLegacyCache`.
- Provide a `fastValidation` mode that checks only timestamps/sizes for speed;
  fall back to checksum when a mismatch or suspicion is detected.

Readers should return a structured `CacheLoadResult` indicating whether the
fast-path was accepted, whether repair ran, and what was missing.

## Repair / regeneration

If manifest exists but some satellites are missing or mismatched, provide two
configurable behaviors:

- `strict`: refuse fast-path and rebuild from dump.
- `repair`: attempt to regenerate missing satellites from the canonical
  `ObjectIndex.bin` / `TypeAggregateIndex.bin` (cheaper than a full rebuild).

Repair should be implemented as a dedicated, idempotent operation (for
example a `CacheRepairer`) that can be invoked automatically or on demand.
If repair succeeds, re-write and re-validate the manifest atomically.

## Concurrency and locking

- Writers must create caches under a temp path and publish atomically to avoid
  concurrent partial state.
- Use a simple lock file (opened with exclusive access) to prevent simultaneous
  writers targeting the same cache directory.

## Backwards compatibility & migration

- If no manifest exists (older caches), the loader should default to rejecting
  them (safe). Optionally provide a migration tool to scan and generate
  manifests for acceptable legacy caches by computing checksums or performing
  repair.

## Tests to add

- Unit: manifest parsing, mismatch detection, checksum logic.
- Integration: create full cache, delete a satellite file, assert loader
  refuses fast-path (strict mode) and that repair mode recreates it.
- Concurrency test: concurrent writer attempts leave no partial manifest.
- Negative tests: leftover `.tmp` artifacts are ignored.

## Configuration knobs

- `cache.strictValidation` (default: true)
- `cache.repairOnMissing` (default: true)
- `cache.fastValidation` (default: false)
- `cache.compatAcceptLegacyCache` (default: false)

## Observability

- `TryLoadFromCache` should log structured outcomes: `manifest_missing`,
  `missing_files:[...]`, `repair_succeeded`, `repair_failed`, `accepted_fastpath`.
- Return structured `CacheLoadResult` up the call chain for caller decisions.

## Migration

- Provide a `cache:migrate` tool that can be run once to generate manifests for
  existing caches (compute sizes/checksums or run repair).

## Next steps

- Implement `IndexManifest` model and writer changes.
- Implement `CacheRepairer` and manifest-driven loader changes.
- Add tests and a migration utility.
